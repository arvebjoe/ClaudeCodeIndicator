using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeIndicator;

/// <summary>The "Usage credits" row from Claude Code's <c>/usage</c> screen.</summary>
internal sealed record UsageReading(decimal Spent, decimal Limit, string? Resets, DateTime ReadAt)
{
    public decimal Remaining => Limit - Spent;
    public string Short => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "${0:0.00} / ${1:0.00}", Spent, Limit) + (Resets is null ? "" : $" · resets {Resets}");
}

/// <summary>
/// Keeps one interactive <c>claude</c> session running in a hidden pseudo-console
/// (ConPTY) and reads the usage-credits line by typing <c>/usage</c> into it.
/// <c>claude -p "/usage"</c> doesn't work for this: print mode leaves the
/// "$X / $Y spent" row out, it only exists on the interactive screen.
///
/// The session runs in its own empty folder with <c>--setting-sources local</c>, so
/// user-level hooks (including this app's own) never fire for it, and with
/// <c>--strict-mcp-config</c> so it doesn't spin up MCP servers. Nothing is ever
/// sent to the model: <c>/usage</c> is a local command.
/// </summary>
internal sealed class UsageProbe : IDisposable
{
    // "$15.37 / $800.00 spent · Resets Dec 1 (Europe/Oslo)"
    private static readonly Regex UsageLine = new(
        @"\$\s*(?<spent>[\d,]+(?:\.\d+)?)\s*/\s*\$\s*(?<limit>[\d,]+(?:\.\d+)?)\s*spent" +
        @"(?:\s*·\s*Resets\s+(?<resets>[A-Z][a-z]{2,8}\s+\d{1,2}))?",
        RegexOptions.Compiled);

    // CSI (ESC [ ... final), OSC (ESC ] ... BEL/ST) and two-byte escapes.
    private static readonly Regex CursorForward = new(@"\x1b\[(\d*)C", RegexOptions.Compiled);
    private static readonly Regex Csi = new(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Osc = new(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)", RegexOptions.Compiled);
    private static readonly Regex OtherEsc = new(@"\x1b[@-Z\\-_]", RegexOptions.Compiled);

    private const short Cols = 220, Rows = 60; // wide, so the row never wraps
    private const int MaxBuffer = 200_000;

    private readonly string _workDir;
    private readonly object _gate = new();
    private readonly StringBuilder _screen = new();   // raw output since the last request
    private readonly SemaphoreSlim _busy = new(1, 1);

    private IntPtr _hpc;
    private SafeFileHandle? _inWrite, _outRead;
    private FileStream? _input;
    private Thread? _reader;
    private IntPtr _process, _thread;
    private DateTime _lastOutput;

    public UsageProbe(string workDir)
    {
        _workDir = workDir;
        Directory.CreateDirectory(_workDir);
    }

    /// <summary>Optional tap on everything the session writes (diagnostics).</summary>
    public Action<string>? RawOutput { get; set; }

    public bool IsRunning => _process != IntPtr.Zero && WaitForSingleObject(_process, 0) == WAIT_TIMEOUT;

    /// <summary>
    /// Opens <c>/usage</c> (closing a previous one with Esc first) and waits for the
    /// usage-credits row. Returns null if it doesn't show up in time. Starts or
    /// restarts the claude session as needed.
    /// </summary>
    public async Task<UsageReading?> ReadAsync(CancellationToken ct = default)
    {
        await _busy.WaitAsync(ct);
        try
        {
            if (!IsRunning)
            {
                StopProcess();
                Start();
                await WaitForStartupAsync(ct);
            }
            else
            {
                Send("\x1b");                       // close the open /usage panel
                await Task.Delay(500, ct);
            }

            Clear();
            Send("/usage");
            await Task.Delay(400, ct);              // let the slash-command menu settle
            Send("\r");

            // The panel first shows a cached value with "Refreshing…", then redraws.
            // Once the row appears, wait for output to go quiet and take the last match.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            UsageReading? found = null;
            DateTime? firstSeen = null;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, ct);
                if (!IsRunning) return found;
                if (TryParse(Snapshot()) is { } r)
                {
                    found = r;
                    firstSeen ??= DateTime.UtcNow;
                    bool quiet = DateTime.UtcNow - _lastOutput > TimeSpan.FromSeconds(1.5);
                    if (quiet || DateTime.UtcNow - firstSeen > TimeSpan.FromSeconds(10)) return found;
                }
            }
            return found;
        }
        finally { _busy.Release(); }
    }

    /// <summary>Plain text of the screen output since the last request (for diagnostics).</summary>
    public string Snapshot()
    {
        string raw;
        lock (_gate) raw = _screen.ToString();
        return Strip(raw);
    }

    internal static UsageReading? TryParse(string text)
    {
        // The panel can be redrawn several times; the last match is the freshest.
        Match? last = null;
        foreach (Match m in UsageLine.Matches(text)) last = m;
        if (last is null) return null;

        static decimal Num(string s) => decimal.Parse(s.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        string? resets = last.Groups["resets"].Success ? last.Groups["resets"].Value : null;
        return new UsageReading(Num(last.Groups["spent"].Value), Num(last.Groups["limit"].Value), resets, DateTime.Now);
    }

    internal static string Strip(string raw)
    {
        // Renderers skip over unchanged cells with "cursor forward"; turn those back
        // into spaces so words don't run together, then drop every other escape.
        string s = CursorForward.Replace(raw, m => new string(' ', m.Groups[1].Length == 0 ? 1 : int.Parse(m.Groups[1].Value)));
        s = Osc.Replace(s, "");
        s = Csi.Replace(s, "\n");   // other cursor moves usually mean "new place on screen"
        s = OtherEsc.Replace(s, "");
        return s;
    }

    private async Task WaitForStartupAsync(CancellationToken ct)
    {
        // Wait until the UI has drawn and gone quiet. Answer the one-time
        // "Is this a project you trust?" question if it appears. Its default choice
        // is "No, exit", so move down to "Yes, I trust this folder" before Enter.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        bool trusted = false;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            string text = Snapshot();
            if (!trusted && text.Contains("Yes, I trust", StringComparison.OrdinalIgnoreCase))
            {
                trusted = true;
                Clear();
                Send("\x1b[B");                     // arrow down
                await Task.Delay(400, ct);
                if (!Regex.IsMatch(Snapshot(), @"❯\s*Yes")) return; // not where we expect: give up this round
                Send("\r");
                await Task.Delay(1000, ct);
                Clear();
                continue;
            }
            // The welcome banner ("Claude Code v2.x") means the prompt is up.
            if (text.Contains("Claude Code v") && DateTime.UtcNow - _lastOutput > TimeSpan.FromSeconds(2)) return;
        }
    }

    private void Clear() { lock (_gate) _screen.Clear(); }

    private void Send(string text)
    {
        if (_input is null) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _input.Write(bytes, 0, bytes.Length);
        _input.Flush();
    }

    // ----------------------------------------------------------- ConPTY plumbing

    private void Start()
    {
        string exe = FindClaude() ?? throw new FileNotFoundException("claude.exe not found on PATH");

        if (!CreatePipe(out var inRead, out _inWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out _outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception();

        int hr = CreatePseudoConsole(new COORD { X = Cols, Y = Rows }, inRead, outWrite, 0, out _hpc);
        // The pseudo-console holds its own copies of these ends.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr);

        _input = new FileStream(_inWrite, FileAccess.Write, 1, false);
        lock (_gate) _screen.Clear();
        _lastOutput = DateTime.UtcNow;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "UsageProbe output" };
        _reader.Start();

        // Attribute list carrying the pseudo-console handle.
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attrList = Marshal.AllocHGlobal(size);
        IntPtr envBlock = BuildEnvironment();
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

            // Plan mode as a seatbelt: we never send a prompt, but if a keystroke ever
            // went astray the session still can't edit files or run commands.
            string cmd = $"\"{exe}\" --setting-sources local --strict-mcp-config --permission-mode plan";
            if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envBlock,
                    _workDir, ref si, out var pi))
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

    private static IntPtr BuildEnvironment()
    {
        // Same environment as ours, but with a terminal that claims color + UTF-8.
        var env = Environment.GetEnvironmentVariables();
        env["TERM"] = "xterm-256color";
        // In case the tray app itself was started from inside a Claude session.
        foreach (var k in new[] { "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" })
            env.Remove(k);
        var sb = new StringBuilder();
        var keys = new System.Collections.Generic.List<string>();
        foreach (System.Collections.DictionaryEntry e in env) keys.Add((string)e.Key);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys) sb.Append(k).Append('=').Append(env[k]).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private void ReadLoop()
    {
        var stream = new FileStream(_outRead!, FileAccess.Read, 1, false);
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192];
        try
        {
            int n;
            while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
            {
                int c = decoder.GetChars(bytes, 0, n, chars, 0);
                lock (_gate)
                {
                    _screen.Append(chars, 0, c);
                    if (_screen.Length > MaxBuffer) _screen.Remove(0, _screen.Length - MaxBuffer / 2);
                }
                _lastOutput = DateTime.UtcNow;
                RawOutput?.Invoke(new string(chars, 0, c));
            }
        }
        catch { /* pipe closed */ }
    }

    private static string? FindClaude()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), "claude.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    private void StopProcess()
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
        try { _outRead?.Dispose(); } catch { }
        _outRead = null;
    }

    public void Dispose() => StopProcess();

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
