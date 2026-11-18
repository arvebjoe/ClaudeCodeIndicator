using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeIndicator;

/// <summary>
/// <see cref="IPseudoTerminal"/> on a Unix pty: <c>openpty</c> for the pair, then
/// <c>posix_spawn</c> (not fork, which isn't safe in a .NET process) with the child in a new
/// session and the pty's slave side as its stdin/stdout/stderr. The slave is opened by name
/// as the new session leader's first terminal, which makes it the child's controlling
/// terminal on Linux. On macOS the child may end up without one; a TUI only needs its
/// stdio to be a tty, which they are either way.
///
/// Only non-variadic libc functions are called: variadic ones (fcntl, ioctl, open) have a
/// different calling convention on Apple Silicon that P/Invoke doesn't follow.
/// </summary>
internal sealed class UnixPtyTerminal : IPseudoTerminal
{
    private int _pid;
    private bool _exited;
    private FileStream? _input, _output;
    private SafeFileHandle? _outputHandle;

    public Stream Input => _input ?? throw new InvalidOperationException("not started");
    public Stream Output => _output ?? throw new InvalidOperationException("not started");

    public bool IsRunning
    {
        get
        {
            if (_pid <= 0 || _exited) return false;
            int r = waitpid(_pid, out _, WNOHANG);
            if (r == 0) return true;
            _exited = true; // reaped now (r == pid) or not ours to wait for any more (-1)
            return false;
        }
    }

    public void Start(PseudoTerminalStartInfo info)
    {
        var size = new WinSize { Rows = (ushort)info.Rows, Columns = (ushort)info.Columns };
        var name = new byte[1024];
        if (openpty(out int master, out int slave, name, IntPtr.Zero, ref size) != 0)
            throw new IOException($"openpty failed (errno {Marshal.GetLastPInvokeError()})");
        string slaveName = Encoding.UTF8.GetString(name, 0, Array.IndexOf(name, (byte)0));

        var allocations = new List<IntPtr>();
        IntPtr Alloc(int bytes)
        {
            IntPtr p = Marshal.AllocHGlobal(bytes);
            allocations.Add(p);
            for (int i = 0; i < bytes; i += 8) Marshal.WriteInt64(p, i, 0);
            return p;
        }

        // posix_spawn_file_actions_t / posix_spawnattr_t / sigset_t are a pointer on macOS
        // and structs of a few hundred bytes on Linux; 1 KiB covers both.
        IntPtr actions = Alloc(1024), attr = Alloc(1024), sigMask = Alloc(1024), sigDefault = Alloc(1024);
        bool actionsInit = false, attrInit = false;
        try
        {
            Check(posix_spawn_file_actions_init(actions), "file_actions_init"); actionsInit = true;
            Check(posix_spawn_file_actions_addclose(actions, master), "addclose master");
            if (slave > 2) Check(posix_spawn_file_actions_addclose(actions, slave), "addclose slave");
            // Opened after setsid, so it becomes the controlling terminal where the OS does that on open.
            Check(posix_spawn_file_actions_addopen(actions, 0, slaveName, O_RDWR, 0), "addopen");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1), "dup2 stdout");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2), "dup2 stderr");
            Check(posix_spawn_file_actions_addchdir_np(actions, info.WorkingDirectory), "addchdir");

            Check(posix_spawnattr_init(attr), "spawnattr_init"); attrInit = true;
            // Unblock everything, and reset SIGPIPE (ignored by the .NET runtime) to default.
            Check(sigemptyset(sigMask), "sigemptyset");
            Check(posix_spawnattr_setsigmask(attr, sigMask), "setsigmask");
            Check(sigemptyset(sigDefault), "sigemptyset");
            Check(sigaddset(sigDefault, SIGPIPE), "sigaddset");
            Check(posix_spawnattr_setsigdefault(attr, sigDefault), "setsigdefault");
            int flags = SpawnSetSigDef | SpawnSetSigMask | (OperatingSystem.IsMacOS()
                ? MacSpawnSetSid | MacSpawnCloexecDefault  // only the fds set up above reach the child
                : LinuxSpawnSetSid);
            Check(posix_spawnattr_setflags(attr, (short)flags), "setflags");

            var argv = new List<string> { info.FileName };
            argv.AddRange(info.Arguments);
            var envp = new List<string>();
            foreach (var (k, v) in info.Environment) envp.Add($"{k}={v}");
            IntPtr argvPtr = StringArray(argv, allocations);
            IntPtr envpPtr = StringArray(envp, allocations);

            int rc = posix_spawn(out _pid, info.FileName, actions, attr, argvPtr, envpPtr);
            if (rc != 0) throw new IOException($"posix_spawn {info.FileName} failed (errno {rc})");
        }
        catch
        {
            close(master);
            throw;
        }
        finally
        {
            close(slave); // the child has its own copy
            if (actionsInit) posix_spawn_file_actions_destroy(actions);
            if (attrInit) posix_spawnattr_destroy(attr);
            foreach (IntPtr p in allocations) Marshal.FreeHGlobal(p);
        }

        // Separate descriptors for writing and reading, so closing one doesn't pull the
        // other out from under a blocked Read on the probe's reader thread.
        int inputFd = dup(master);
        _input = new FileStream(new SafeFileHandle(inputFd, ownsHandle: true), FileAccess.Write, 1);
        _outputHandle = new SafeFileHandle(master, ownsHandle: true);
        _output = new FileStream(_outputHandle, FileAccess.Read, 1);
    }

    public void Dispose()
    {
        if (_pid > 0 && !_exited)
        {
            // setsid made the child a process-group leader: signal the whole group so
            // anything it started goes too.
            kill(-_pid, SIGTERM);
            if (!WaitForExit(3000))
            {
                kill(-_pid, SIGKILL);
                WaitForExit(1000);
            }
        }
        try { _input?.Dispose(); } catch { }
        _input = null;
        // Close the handle rather than the stream: the reader thread may be inside a Read.
        // Once the child is gone that Read returns EOF/EIO and the thread ends.
        try { _outputHandle?.Dispose(); } catch { }
        _outputHandle = null;
    }

    private bool WaitForExit(int ms)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (!IsRunning) return true;
            Thread.Sleep(50);
        }
        return !IsRunning;
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new IOException($"{what} failed (errno {(rc > 0 ? rc : Marshal.GetLastPInvokeError())})");
    }

    /// <summary>A NULL-terminated array of NUL-terminated UTF-8 strings, as argv/envp.</summary>
    private static IntPtr StringArray(List<string> items, List<IntPtr> allocations)
    {
        IntPtr array = Marshal.AllocHGlobal(IntPtr.Size * (items.Count + 1));
        allocations.Add(array);
        for (int i = 0; i < items.Count; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(items[i] + "\0");
            IntPtr s = Marshal.AllocHGlobal(utf8.Length);
            allocations.Add(s);
            Marshal.Copy(utf8, 0, s, utf8.Length);
            Marshal.WriteIntPtr(array, i * IntPtr.Size, s);
        }
        Marshal.WriteIntPtr(array, items.Count * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    // ----------------------------------------------------------------- libc

    private const int O_RDWR = 2, WNOHANG = 1, SIGPIPE = 13, SIGTERM = 15, SIGKILL = 9;
    private const int SpawnSetSigDef = 0x04, SpawnSetSigMask = 0x08;
    private const int MacSpawnSetSid = 0x0400, MacSpawnCloexecDefault = 0x4000, LinuxSpawnSetSid = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize { public ushort Rows, Columns, XPixels, YPixels; }

    private const string LibC = "libc";

    static UnixPtyTerminal()
    {
        // "libc" isn't a loadable name on either OS; point it at the real library.
        NativeLibrary.SetDllImportResolver(typeof(UnixPtyTerminal).Assembly, (name, _, _) =>
            name != LibC ? IntPtr.Zero
            : NativeLibrary.Load(OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6"));
    }

    [DllImport(LibC, SetLastError = true)]
    private static extern int openpty(out int master, out int slave, byte[] name, IntPtr termios, ref WinSize size);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_init(IntPtr actions);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_destroy(IntPtr actions);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_addclose(IntPtr actions, int fd);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);
    [DllImport(LibC)]
    private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport(LibC)]
    private static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(LibC)] private static extern int posix_spawnattr_init(IntPtr attr);
    [DllImport(LibC)] private static extern int posix_spawnattr_destroy(IntPtr attr);
    [DllImport(LibC)] private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
    [DllImport(LibC)] private static extern int posix_spawnattr_setsigmask(IntPtr attr, IntPtr set);
    [DllImport(LibC)] private static extern int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr set);
    [DllImport(LibC, SetLastError = true)] private static extern int sigemptyset(IntPtr set);
    [DllImport(LibC, SetLastError = true)] private static extern int sigaddset(IntPtr set, int signal);
    [DllImport(LibC)]
    private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr actions, IntPtr attr, IntPtr argv, IntPtr envp);
    [DllImport(LibC, SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport(LibC, SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport(LibC, SetLastError = true)] private static extern int dup(int fd);
    [DllImport(LibC, SetLastError = true)] private static extern int close(int fd);
}
