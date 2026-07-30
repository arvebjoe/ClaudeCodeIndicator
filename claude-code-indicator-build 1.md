# Claude Code Desktop Status Indicator — Build Spec

A Windows tray app that shows Claude Code's live state as a colored notification-area
icon (and optionally drives an ESP32 RGB indicator on your desk):

- **Green** — done / idle (Claude finished responding)
- **Red** — working (you submitted a prompt, or a tool is running)
- **Yellow** — waiting on you (permission prompt or idle prompt)

It works by registering **Claude Code HTTP hooks** that POST lifecycle events to a tiny
local listener inside the tray app. The app maps each event to a color, updates its tray
icon, and (if an ESP32 address is set) forwards the color to the ESP over HTTP. The ESP is
entirely optional — the tray icon is a complete indicator on its own.

> This doc is a build handoff. Hand it to Claude Code and have it create the project,
> drop in the two files below verbatim, publish, and run. **Target is Windows only**
> (`net9.0-windows` + WinForms); it cannot be built or run on Linux/macOS.

---

## Architecture / data flow

```
Claude Code (any terminal/session)
   │  hooks: HTTP POST to http://localhost:8787/{working|waiting|done}
   ▼
ClaudeCodeIndicator.exe  (tray app, single instance)
   ├─ updates NotifyIcon color + tooltip + menu status
   └─ if ESP32 address set: GET http://<esp>/state?value=...&rgb=...
                                              │
                                              ▼
                                  ESP32 RGB indicator (optional)
```

State is **latest-event-wins** globally, which is the intuitive behavior for one or two
concurrent sessions. The hook body (which carries `session_id`) is read and discarded; if
per-session aggregation is ever wanted, that's where to start.

## State model

| Claude Code hook event | Matcher | Endpoint hit | Indicator |
|---|---|---|---|
| `UserPromptSubmit` | — | `/working` | Red (#E53935) |
| `PreToolUse` | — (all tools) | `/working` | Red (#E53935) |
| `Notification` | `permission_prompt\|idle_prompt` | `/waiting` | Yellow (#FFB300) |
| `Stop` | — | `/done` | Green (#43A047) |

Initial state on launch is Green/idle. `PreToolUse` re-arms Red after you approve a
permission prompt (which had turned it Yellow).

## Requirements

- Windows 10/11
- .NET 9 SDK
- Listener uses loopback port **8787** (change the `Port` constant if it clashes)

## Project layout

```
ClaudeCodeIndicator/
├── ClaudeCodeIndicator.csproj
├── Program.cs
├── working.ico   (optional — falls back to a drawn red dot)
├── waiting.ico   (optional — falls back to a drawn amber dot)
└── done.ico      (optional — falls back to a drawn green dot)
```

---

## File: `ClaudeCodeIndicator.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>ClaudeCodeIndicator</AssemblyName>
    <RootNamespace>ClaudeCodeIndicator</RootNamespace>
    <Version>1.0.0</Version>
    <!-- Keeps the colored .ico files next to the exe if you add them -->
    <ApplicationIcon></ApplicationIcon>
  </PropertyGroup>

</Project>
```

## File: `Program.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Runtime.InteropServices;
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
        ("Notification",     "waiting", "permission_prompt|idle_prompt"),
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
    private readonly string _settingsPath;
    private readonly Icon[] _icons = new Icon[3];
    private readonly IntPtr[] _ownedHandles;           // generated-icon handles to free on exit
    private AppSettings _settings;
    private IndicatorState _state = IndicatorState.Done;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

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
        _icons[(int)IndicatorState.Working] = LoadIcon("working.ico", Color.FromArgb(229, 57, 53),  owned);
        _icons[(int)IndicatorState.Waiting] = LoadIcon("waiting.ico", Color.FromArgb(255, 179, 0),  owned);
        _ownedHandles = owned.ToArray();

        // --- context menu ---
        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Status: Idle") { Enabled = false };
        _espItem = new ToolStripMenuItem("ESP32: not set") { Enabled = false };
        var setEspItem = new ToolStripMenuItem("Set ESP32 address\u2026", null, (_, _) => PromptForEsp());
        _hooksItem = new ToolStripMenuItem("Auto-configure Claude Code hooks", null, (_, _) => ToggleClaudeHooks())
        {
            Checked = ClaudeHooksInstalled()
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
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new NotifyIcon
        {
            Icon = _icons[(int)_state],
            Text = "Claude Code \u2014 Idle",
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
        _state = s;
        _tray.Icon = _icons[(int)s];
        (string label, _) = Describe(s);
        _tray.Text = $"Claude Code \u2014 {label}";
        _statusItem.Text = $"Status: {label}";
        _ = PushToEspAsync(s);
    }

    private static (string label, string hex) Describe(IndicatorState s) => s switch
    {
        IndicatorState.Working => ("Working", "E53935"),
        IndicatorState.Waiting => ("Waiting", "FFB300"),
        _                      => ("Done",    "43A047"),
    };

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
                "If the file has comments or invalid JSON, fix it first \u2014 nothing was changed.",
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
```

---

## Build & deploy

Publish to a **fixed** folder (the autostart registry entry points at the exe's path, so
it must not move afterward):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Tools\ClaudeCodeIndicator
```

Run `C:\Tools\ClaudeCodeIndicator\ClaudeCodeIndicator.exe` once. On first run it registers
itself under `HKCU\...\Run` so it starts at login. A balloon confirms it's listening on
`http://localhost:8787`.

## Claude Code hooks

**Option A (recommended):** right-click the tray icon → **Auto-configure Claude Code
hooks**. This writes/removes the four hooks in `~/.claude/settings.json`
(`%USERPROFILE%\.claude\settings.json`) surgically — it only touches matcher groups whose
handler URL points at `localhost:8787`, leaving everything else intact, and saves a
`settings.json.bak` before each write. Claude Code's file watcher picks the change up live;
no restart needed.

**Option B (manual):** add this to `~/.claude/settings.json` yourself:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [{ "type": "http", "url": "http://localhost:8787/working" }] }
    ],
    "PreToolUse": [
      { "hooks": [{ "type": "http", "url": "http://localhost:8787/working" }] }
    ],
    "Notification": [
      { "matcher": "permission_prompt|idle_prompt",
        "hooks": [{ "type": "http", "url": "http://localhost:8787/waiting" }] }
    ],
    "Stop": [
      { "hooks": [{ "type": "http", "url": "http://localhost:8787/done" }] }
    ]
  }
}
```

## Tray right-click menu

- **Status: …** — current state (read-only)
- **ESP32: …** — current ESP target, or "not set" (read-only)
- **Set ESP32 address…** — enter IP/hostname; blank disables ESP push. Stored in
  `%APPDATA%\ClaudeCodeIndicator\settings.json`.
- **Auto-configure Claude Code hooks** — checkable; toggles the hooks in `settings.json`.
- **Start with Windows** — checkable; toggles the `HKCU\...\Run` entry.
- **Exit**

## State icons

Drop `working.ico`, `waiting.ico`, `done.ico` next to the exe to use custom icons.
If a file is missing or invalid, the app draws an anti-aliased colored dot in the matching
palette, so it's never blank.

## ESP32 endpoint contract (firmware — next step, not yet built)

When an ESP32 address is set, each state change fires:

```
GET http://<esp-ip>/state?value=<working|waiting|done>&rgb=<hexcolor>
```

Example: `GET http://192.168.0.123/state?value=working&rgb=E53935`

- `value` — semantic state, lets the firmware decide effects (e.g. pulse while working)
- `rgb` — 6-digit hex (no `#`), so the LED color matches the tray palette exactly

The push is fire-and-forget with a 2 s timeout; an offline or absent ESP is silently
ignored. Firmware target: a small HTTP server on the ESP32-S3 that parses this request and
drives a WS2812B ring. (Ask Claude to generate the Arduino/ESP-IDF sketch next.)

## Behavior & safety notes

- **Single instance** via a named mutex — autostart + a manual launch won't double-bind the port.
- **Surgical settings edits** — only the app's own hooks are added/removed; re-serialization
  reflows whitespace (standard indented JSON) but is semantically identical. If
  `settings.json` contains comments/JSONC or is invalid, the app warns and changes nothing.
- **Backup** — `settings.json.bak` is written before every hooks change.
- **Listener** binds both `http://localhost:8787/` and `http://127.0.0.1:8787/`. If
  `HttpListener.Start()` fails with an access error, run once elevated:
  `netsh http add urlacl url=http://localhost:8787/ user=%USERNAME%`
- **GDI handles** for generated fallback icons are freed on exit.

## Suggested build checklist for Claude Code

1. Create folder `ClaudeCodeIndicator/` and add the two files above verbatim.
2. `dotnet publish -c Release -r win-x64 --self-contained false -o C:\Tools\ClaudeCodeIndicator`.
3. Launch the exe; confirm the tray icon appears (green) and the "listening" balloon shows.
4. Tray menu → **Auto-configure Claude Code hooks** (verify it ticks and a `.bak` appears).
5. In a Claude Code session: submit a prompt → icon goes red; on a permission prompt →
   yellow; when it finishes → green.
6. (Optional) add `*.ico` files next to the exe.
7. (Optional) build the ESP32-S3 firmware per the endpoint contract and set its IP via
   **Set ESP32 address…**.
