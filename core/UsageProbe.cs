using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeIndicator;

/// <summary>The "Usage credits" row from Claude Code's <c>/usage</c> screen.</summary>
public sealed record UsageReading(decimal Spent, decimal Limit, string? Resets, DateTime ReadAt)
{
    public decimal Remaining => Limit - Spent;
    public string Short => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "${0:0.00} / ${1:0.00}", Spent, Limit) + (Resets is null ? "" : $" · resets {Resets}");
}

/// <summary>
/// Keeps one interactive <c>claude</c> session running in a hidden pseudo terminal
/// (supplied by the front end: ConPTY on Windows) and reads the usage-credits line by
/// typing <c>/usage</c> into it. <c>claude -p "/usage"</c> doesn't work for this: print
/// mode leaves the "$X / $Y spent" row out, it only exists on the interactive screen.
///
/// The session runs in its own empty folder with <c>--setting-sources local</c>, so
/// user-level hooks (including this app's own) never fire for it, and with
/// <c>--strict-mcp-config</c> so it doesn't spin up MCP servers. Nothing is ever
/// sent to the model: <c>/usage</c> is a local command.
/// </summary>
public sealed class UsageProbe : IDisposable
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
    private readonly Func<IPseudoTerminal> _terminalFactory;
    private readonly object _gate = new();
    private readonly StringBuilder _screen = new();   // raw output since the last request
    private readonly SemaphoreSlim _busy = new(1, 1);

    private IPseudoTerminal? _terminal;
    private Stream? _input;
    private Thread? _reader;
    private DateTime _lastOutput;

    public UsageProbe(string workDir, Func<IPseudoTerminal> terminalFactory)
    {
        _workDir = workDir;
        _terminalFactory = terminalFactory;
        Directory.CreateDirectory(_workDir);
    }

    /// <summary>Optional tap on everything the session writes (diagnostics).</summary>
    public Action<string>? RawOutput { get; set; }

    public bool IsRunning => _terminal?.IsRunning == true;

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

    public static UsageReading? TryParse(string text)
    {
        // The panel can be redrawn several times; the last match is the freshest.
        Match? last = null;
        foreach (Match m in UsageLine.Matches(text)) last = m;
        if (last is null) return null;

        static decimal Num(string s) => decimal.Parse(s.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        string? resets = last.Groups["resets"].Success ? last.Groups["resets"].Value : null;
        return new UsageReading(Num(last.Groups["spent"].Value), Num(last.Groups["limit"].Value), resets, DateTime.Now);
    }

    public static string Strip(string raw)
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

    // ------------------------------------------------------------- session

    private void Start()
    {
        string exe = FindClaude() ?? throw new FileNotFoundException("claude not found on PATH");

        var terminal = _terminalFactory();
        _terminal = terminal;
        // Plan mode as a seatbelt: we never send a prompt, but if a keystroke ever
        // went astray the session still can't edit files or run commands.
        terminal.Start(new PseudoTerminalStartInfo(
            exe,
            new[] { "--setting-sources", "local", "--strict-mcp-config", "--permission-mode", "plan" },
            _workDir,
            BuildEnvironment(),
            Cols, Rows));

        _input = terminal.Input;
        lock (_gate) _screen.Clear();
        _lastOutput = DateTime.UtcNow;
        var output = terminal.Output;
        _reader = new Thread(() => ReadLoop(output)) { IsBackground = true, Name = "UsageProbe output" };
        _reader.Start();
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment()
    {
        // Same environment as ours, but with a terminal that claims color + UTF-8.
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        env["TERM"] = "xterm-256color";
        // In case the tray app itself was started from inside a Claude session.
        foreach (var k in new[] { "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" })
            env.Remove(k);
        return env;
    }

    private void ReadLoop(Stream stream)
    {
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
        var dirs = new List<string> { Path.Combine(ExecutableSearch.HomeDirectory, ".local", "bin") };
        dirs.AddRange(ExecutableSearch.PathDirectories());
        if (OperatingSystem.IsWindows())
            return ExecutableSearch.Find(new[] { "claude.exe" }, dirs);
        dirs.AddRange(ExecutableSearch.UnixFallbackDirectories());
        return ExecutableSearch.Find(new[] { "claude" }, dirs);
    }

    private void StopProcess()
    {
        try { _terminal?.Dispose(); } catch { } // also ends the client; the reader then sees EOF
        _terminal = null;
        _input = null;
    }

    public void Dispose() => StopProcess();
}
