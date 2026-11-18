using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeIndicator;

/// <summary>
/// <see cref="IPseudoTerminal"/> on a Windows pseudo console (ConPTY): two pipes, a
/// pseudo console between them, and the process started with it attached.
/// </summary>
internal sealed class ConPtyTerminal : IPseudoTerminal
{
    private IntPtr _hpc;
    private SafeFileHandle? _inWrite, _outRead;
    private FileStream? _input, _output;
    private IntPtr _process, _thread;

    public Stream Input => _input ?? throw new InvalidOperationException("not started");
    public Stream Output => _output ?? throw new InvalidOperationException("not started");

    public bool IsRunning => _process != IntPtr.Zero && WaitForSingleObject(_process, 0) == WAIT_TIMEOUT;

    public void Start(PseudoTerminalStartInfo info)
    {
        if (!CreatePipe(out var inRead, out _inWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out _outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception();

        int hr = CreatePseudoConsole(new COORD { X = (short)info.Columns, Y = (short)info.Rows },
                                     inRead, outWrite, 0, out _hpc);
        // The pseudo-console holds its own copies of these ends.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr);

        _input = new FileStream(_inWrite, FileAccess.Write, 1, false);
        _output = new FileStream(_outRead, FileAccess.Read, 1, false);

        // Attribute list carrying the pseudo-console handle.
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attrList = Marshal.AllocHGlobal(size);
        IntPtr envBlock = BuildEnvironmentBlock(info.Environment);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;
            // Null std handles + this flag, or a child of a process whose stdio is
            // redirected inherits those pipes instead of the pseudo-console.
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

            var cmd = new StringBuilder(Quote(info.FileName));
            foreach (string arg in info.Arguments) cmd.Append(' ').Append(Quote(arg));

            if (!CreateProcess(null, cmd.ToString(), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envBlock,
                    info.WorkingDirectory, ref si, out var pi))
                throw new Win32Exception();
            _process = pi.hProcess;
            _thread = pi.hThread;
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            Marshal.FreeHGlobal(envBlock); // CreateProcess copies the block
        }
    }

    // Enough for our arguments (paths and plain flags); no embedded quotes to escape.
    private static string Quote(string s) => s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t' }) < 0 ? s : $"\"{s}\"";

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> env)
    {
        // Sorted, NUL-separated, double-NUL-terminated UTF-16, as CreateProcess wants it.
        var keys = new List<string>(env.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var k in keys) sb.Append(k).Append('=').Append(env[k]).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    public void Dispose()
    {
        if (_hpc != IntPtr.Zero) { ClosePseudoConsole(_hpc); _hpc = IntPtr.Zero; } // also ends the client
        if (_process != IntPtr.Zero)
        {
            if (WaitForSingleObject(_process, 3000) == WAIT_TIMEOUT) TerminateProcess(_process, 1);
            CloseHandle(_process); _process = IntPtr.Zero;
        }
        if (_thread != IntPtr.Zero) { CloseHandle(_thread); _thread = IntPtr.Zero; }
        try { _input?.Dispose(); } catch { }
        _input = null;
        // Close the handle rather than the stream: the probe's reader thread may still be
        // inside a Read on it, and it ends on its own once the pipe reports EOF.
        try { _outRead?.Dispose(); } catch { }
        _outRead = null;
    }

    // ----------------------------------------------------------------- Win32

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint WAIT_TIMEOUT = 0x102;
    private const int STARTF_USESTDHANDLES = 0x00000100;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
