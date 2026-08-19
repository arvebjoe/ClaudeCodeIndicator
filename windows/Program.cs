using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeCodeIndicator;

/// <summary>The three states Claude Code reports through hooks.</summary>
internal enum IndicatorState { Done, Working, Waiting }

internal sealed class AppSettings
{
    public string? EspAddress { get; set; }
    public bool FirstRunDone { get; set; }
    /// <summary>Silences the Waiting chime. Muted-not-Enabled so the default (false) plays sound.</summary>
    public bool SoundMuted { get; set; }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance: a second launch (e.g. autostart + manual) just exits,
        // so two listeners never fight over the port.
        using var mutex = new Mutex(true, "ClaudeCodeIndicator_SingleInstance", out bool isNew);
        if (!isNew) return;

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var ctx = new TrayContext();
        Application.Run(ctx);
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private const int Port = 8787;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClaudeCodeIndicator";

    // The hooks this app manages in ~/.claude/settings.json. Each is written as a
    // matcher group with a single HTTP handler pointing at our local listener.
    private static readonly (string Event, string Path, string? Matcher)[] HookSpec =
    {
        ("UserPromptSubmit", "working", null),
        ("PreToolUse",       "working", null),
        // Only permission_prompt means "blocked, needs you" → yellow. idle_prompt
        // fires when Claude is *done* and waiting for the next prompt, so routing it
        // to /waiting would override the green from Stop and leave the icon stuck on
        // yellow after every finished turn.
        ("Notification",     "waiting", "permission_prompt"),
        ("Stop",             "done",    null),
    };

    private readonly NotifyIcon _tray;
    private readonly Control _marshal;                 // hidden control for UI-thread marshalling
    private readonly HttpListener _listener = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _espItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _hooksItem;
    private readonly ToolStripMenuItem _soundItem;
    private readonly string _settingsPath;
    private readonly Icon[] _icons = new Icon[3];
    private readonly IntPtr[] _ownedHandles;           // generated-icon handles to free on exit
    private AppSettings _settings;
    private IndicatorState _state = IndicatorState.Done;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    // MCI plays MP3 with no extra dependencies (System.Media.SoundPlayer is WAV-only,
    // and pulling in WPF's MediaPlayer just for a chime isn't worth it).
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue,
                                            int returnLength, IntPtr callback);

    private const string SoundAlias = "ccIndicatorWaiting";
    private bool _soundOpened;      // MCI device opened once, then replayed with "play from 0"
    private bool _soundUnavailable; // file missing or MCI refused it — stop retrying

    public TrayContext()
    {
        // --- settings (%APPDATA%\ClaudeCodeIndicator\settings.json) ---
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeCodeIndicator");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
        _settings = LoadSettings();

        // --- UI-thread marshalling helper (never shown) ---
        _marshal = new Control();
        _ = _marshal.Handle; // touching Handle forces creation on this (UI) thread

        // --- icons: load <state>.ico next to the exe, else draw a colored dot ---
        var owned = new List<IntPtr>();
        _icons[(int)IndicatorState.Done]    = LoadIcon("done.ico",    Color.FromArgb(67, 160, 71),  owned);
        _icons[(int)IndicatorState.Working] = LoadIcon("working.ico", Color.FromArgb(211, 115, 85), owned);
        _icons[(int)IndicatorState.Waiting] = LoadIcon("waiting.ico", Color.FromArgb(229, 57, 53),  owned);
        _ownedHandles = owned.ToArray();

        // --- context menu ---
        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Status: Idle") { Enabled = false };
        _espItem = new ToolStripMenuItem("ESP32: not set") { Enabled = false };
        var setEspItem = new ToolStripMenuItem("Set ESP32 address…", null, (_, _) => PromptForEsp());
        _hooksItem = new ToolStripMenuItem("Auto-configure Claude Code hooks", null, (_, _) => ToggleClaudeHooks())
        {
            Checked = ClaudeHooksInstalled()
        };
        _soundItem = new ToolStripMenuItem("Play sound when waiting", null, (_, _) => ToggleSound())
        {
            Checked = !_settings.SoundMuted
        };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = IsStartupEnabled()
        };
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        menu.Items.Add(_statusItem);
        menu.Items.Add(_espItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(setEspItem);
        menu.Items.Add(_hooksItem);
        menu.Items.Add(_soundItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new NotifyIcon
        {
            Icon = _icons[(int)_state],
            Text = "Claude Code — Idle",
            Visible = true,
            ContextMenuStrip = menu
        };
        UpdateEspMenu();

        // --- first run: register autostart so it "just works" after a reboot ---
        if (!_settings.FirstRunDone)
        {
            SetStartup(true);
            _startupItem.Checked = true;
            _settings.FirstRunDone = true;
            SaveSettings();
        }

        StartListener();
        _tray.ShowBalloonTip(2000, "Claude Code Indicator",
            $"Listening on http://localhost:{Port}", ToolTipIcon.Info);
    }

    // ---------------------------------------------------------------- listener

    private void StartListener()
    {
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            MessageBox.Show(
                $"Could not start the local listener on port {Port}.\n\n{ex.Message}\n\n" +
                "If this is an access error, run once in an elevated prompt:\n" +
                $"netsh http add urlacl url=http://localhost:{Port}/ user=%USERNAME%",
                "Claude Code Indicator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; } // listener stopped / disposed
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string path = (ctx.Request.Url?.AbsolutePath ?? string.Empty)
                .Trim('/').ToLowerInvariant();

            // Claude Code POSTs the event JSON as the body. We route by path, so we
            // just drain the body. (session_id lives in here if you ever want to
            // aggregate multiple concurrent sessions instead of latest-wins.)
            using (var reader = new StreamReader(ctx.Request.InputStream))
                await reader.ReadToEndAsync();

            ctx.Response.StatusCode = 200; // 2xx empty body = success to the hook
            ctx.Response.Close();

            IndicatorState? next = path switch
            {
                "working" => IndicatorState.Working,
                "waiting" => IndicatorState.Waiting,
                "done"    => IndicatorState.Done,
                _ => null
            };
            if (next is { } s) RequestState(s);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    // ------------------------------------------------------------------- state

    private void RequestState(IndicatorState s)
    {
        if (_marshal.IsHandleCreated && _marshal.InvokeRequired)
            _marshal.BeginInvoke((MethodInvoker)(() => ApplyState(s)));
        else
            ApplyState(s);
    }

    private void ApplyState(IndicatorState s)
    {
        // Chime only on the transition *into* Waiting, so a repeated Notification
        // hook for the same prompt doesn't pop twice.
        bool entersWaiting = s == IndicatorState.Waiting && _state != IndicatorState.Waiting;
        _state = s;
        _tray.Icon = _icons[(int)s];
        (string label, _) = Describe(s);
        _tray.Text = $"Claude Code — {label}";
        _statusItem.Text = $"Status: {label}";
        _ = PushToEspAsync(s);
        if (entersWaiting) PlayWaitingSound();
    }

    private static (string label, string hex) Describe(IndicatorState s) => s switch
    {
        IndicatorState.Working => ("Working", "D37355"),
        IndicatorState.Waiting => ("Waiting", "E53935"),
        _                      => ("Done",    "43A047"),
    };

    // ------------------------------------------------------------------- sound

    /// <summary>
    /// Pops <c>.sounds\pop.mp3</c> (next to the exe) when a prompt needs attention.
    /// Always called on the UI thread — MCI wants a message pump.
    /// </summary>
    private void PlayWaitingSound()
    {
        if (_settings.SoundMuted || _soundUnavailable) return;

        if (!_soundOpened)
        {
            string path = Path.Combine(AppContext.BaseDirectory, ".sounds", "pop.mp3");
            if (!File.Exists(path)) { _soundUnavailable = true; return; }

            // "type mpegvideo" is the MCI driver that handles MP3; a couple of Windows
            // installs prefer letting MCI pick by extension, so fall back to that.
            if (mciSendString($"open \"{path}\" type mpegvideo alias {SoundAlias}", null, 0, IntPtr.Zero) != 0 &&
                mciSendString($"open \"{path}\" alias {SoundAlias}", null, 0, IntPtr.Zero) != 0)
            {
                _soundUnavailable = true;
                return;
            }
            _soundOpened = true;
        }

        // "from 0" rewinds, so a second prompt re-triggers the clip instead of no-oping
        // at the end of the previous playback.
        mciSendString($"play {SoundAlias} from 0", null, 0, IntPtr.Zero);
    }

    private void ToggleSound()
    {
        _settings.SoundMuted = !_settings.SoundMuted;
        _soundItem.Checked = !_settings.SoundMuted;
        SaveSettings();
        if (!_settings.SoundMuted) PlayWaitingSound(); // preview so the toggle is audible
    }

    // --------------------------------------------------------------------- ESP

    private async Task PushToEspAsync(IndicatorState s)
    {
        string? ip = _settings.EspAddress;
        if (string.IsNullOrWhiteSpace(ip)) return; // ESP optional

        (string name, string hex) = Describe(s);
        string url = $"http://{ip}/state?value={name.ToLowerInvariant()}&rgb={hex}";
        try { using var resp = await _http.GetAsync(url); }
        catch { /* ESP offline or not present — ignore */ }
    }

    private void PromptForEsp()
    {
        using var dlg = new IpInputForm(_settings.EspAddress ?? string.Empty);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        string value = dlg.Value.Trim();
        _settings.EspAddress = string.IsNullOrWhiteSpace(value) ? null : value;
        SaveSettings();
        UpdateEspMenu();
        _ = PushToEspAsync(_state); // sync the ESP to the current color right away
    }

    private void UpdateEspMenu() =>
        _espItem.Text = string.IsNullOrWhiteSpace(_settings.EspAddress)
            ? "ESP32: not set"
            : $"ESP32: {_settings.EspAddress}";

    // ----------------------------------------------------------------- startup

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    private void ToggleStartup()
    {
        bool enable = !IsStartupEnabled();
        SetStartup(enable);
        _startupItem.Checked = enable;
    }

    private static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;
        if (enable) key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    // ---------------------------------------------------------------- settings

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath))
                       ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(_settingsPath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    // --------------------------------------------------- Claude Code settings

    private static string GetClaudeSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");

    private bool ClaudeHooksInstalled()
    {
        try
        {
            string path = GetClaudeSettingsPath();
            if (!File.Exists(path)) return false;
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && HasMyHooks(root);
        }
        catch { return false; }
    }

    private void ToggleClaudeHooks()
    {
        string path = GetClaudeSettingsPath();
        JsonObject root;
        try
        {
            if (File.Exists(path))
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                root = new JsonObject();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not read Claude Code settings:\n{path}\n\n{ex.Message}\n\n" +
                "If the file has comments or invalid JSON, fix it first — nothing was changed.",
                "Claude Code Indicator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool installed = HasMyHooks(root);
        if (installed) RemoveMyHooks(root);
        else InstallMyHooks(root);

        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not write Claude Code settings:\n{path}\n\n{ex.Message}",
                "Claude Code Indicator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _hooksItem.Checked = !installed;
        _tray.ShowBalloonTip(2000, "Claude Code Indicator",
            installed
                ? "Removed indicator hooks from settings.json"
                : "Added indicator hooks to settings.json (.bak saved)",
            ToolTipIcon.Info);
    }

    private static bool HasMyHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        foreach (var kvp in hooks)
        {
            if (kvp.Value is not JsonArray groups) continue;
            foreach (var g in groups)
                if (g is JsonObject group && group["hooks"] is JsonArray handlers)
                    foreach (var h in handlers)
                        if (IsMyHandler(h)) return true;
        }
        return false;
    }

    private static void InstallMyHooks(JsonObject root)
    {
        RemoveMyHooks(root); // idempotent: clear any existing indicator hooks first

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var (ev, path, matcher) in HookSpec)
        {
            if (hooks[ev] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[ev] = groups;
            }

            var handler = new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"http://localhost:{Port}/{path}"
            };
            var group = new JsonObject();
            if (matcher != null) group["matcher"] = matcher;
            group["hooks"] = new JsonArray { handler };
            groups.Add(group);
        }
    }

    private static void RemoveMyHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;

        foreach (var ev in hooks.Select(k => k.Key).ToList())
        {
            if (hooks[ev] is not JsonArray groups) continue;

            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i] is not JsonObject group) continue;
                if (group["hooks"] is JsonArray handlers)
                {
                    for (int h = handlers.Count - 1; h >= 0; h--)
                        if (IsMyHandler(handlers[h]))
                            handlers.RemoveAt(h);

                    if (handlers.Count == 0) groups.RemoveAt(i);
                }
            }

            if (groups.Count == 0) hooks.Remove(ev);
        }

        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static bool IsMyHandler(JsonNode? handler)
    {
        if (handler is not JsonObject o) return false;
        if (o["url"] is not JsonValue v || !v.TryGetValue<string>(out var url) || url is null)
            return false;
        return url.Contains($"localhost:{Port}/", StringComparison.OrdinalIgnoreCase)
            || url.Contains($"127.0.0.1:{Port}/", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------- icons

    private static Icon LoadIcon(string fileName, Color fallback, List<IntPtr> owned)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(path))
        {
            try { return new Icon(path); }
            catch { /* bad file -> generated fallback */ }
        }
        return MakeCircleIcon(fallback, owned);
    }

    private static Icon MakeCircleIcon(Color color, List<IntPtr> owned)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(70, 0, 0, 0));
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }
        IntPtr handle = bmp.GetHicon();
        owned.Add(handle); // freed in Dispose so we don't leak GDI handles
        return Icon.FromHandle(handle);
    }

    // -------------------------------------------------------------------- exit

    private void ExitApp()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (_listener.IsListening) _listener.Stop(); _listener.Close(); } catch { }
            if (_soundOpened)
            {
                try { mciSendString($"close {SoundAlias}", null, 0, IntPtr.Zero); } catch { }
            }
            _http.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _marshal.Dispose();
            foreach (var h in _ownedHandles)
            {
                try { DestroyIcon(h); } catch { }
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>Tiny modal dialog for entering the ESP32 IP / hostname.</summary>
internal sealed class IpInputForm : Form
{
    private readonly TextBox _box;
    public string Value => _box.Text;

    public IpInputForm(string current)
    {
        Text = "ESP32 address";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 112);

        var label = new Label
        {
            Text = "ESP32 IP address or hostname (leave blank to disable):",
            AutoSize = false,
            Location = new Point(12, 12),
            Size = new Size(296, 30)
        };
        _box = new TextBox
        {
            Text = current,
            Location = new Point(12, 46),
            Size = new Size(296, 23)
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(152, 78),
            Size = new Size(75, 25)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(233, 78),
            Size = new Size(75, 25)
        };

        Controls.Add(label);
        Controls.Add(_box);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
