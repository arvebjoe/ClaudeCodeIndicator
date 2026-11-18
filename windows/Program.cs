using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeCodeIndicator;

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

        // The core posts every state change through this, so it all runs on the UI thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var ctx = new TrayContext();
        Application.Run(ctx);
    }
}

/// <summary>
/// The Windows front end: a NotifyIcon with a context menu, rendered from
/// <see cref="IndicatorController"/> (core/), which does the actual work.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClaudeCodeIndicator";

    private readonly IndicatorController _core;
    private readonly MciSoundPlayer _sound = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _usageItem;
    private readonly ToolStripMenuItem _codexItem;
    private readonly ToolStripMenuItem _espItem;
    private readonly ToolStripMenuItem _usageRefreshItem;
    private readonly ToolStripMenuItem _usageTrackItem;
    private readonly ToolStripMenuItem _codexTrackItem;
    private readonly ToolStripMenuItem _hooksItem;
    private readonly ToolStripMenuItem _soundItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Icon[] _icons = new Icon[3];
    private readonly IntPtr[] _ownedHandles;           // generated-icon handles to free on exit

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public TrayContext()
    {
        // --- core (%APPDATA%\ClaudeCodeIndicator\settings.json, probe folders) ---
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeCodeIndicator");
        var ui = SynchronizationContext.Current
                 ?? throw new InvalidOperationException("TrayContext must be created on the UI thread");
        _core = new IndicatorController(dir, ui, () => new ConPtyTerminal(), _sound);

        // --- icons: load <state>.ico next to the exe, else draw a colored dot ---
        var owned = new List<IntPtr>();
        foreach (IndicatorState s in Enum.GetValues<IndicatorState>())
            _icons[(int)s] = LoadIcon($"{s.ToString().ToLowerInvariant()}.ico", s, owned);
        _ownedHandles = owned.ToArray();

        // --- context menu ---
        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem { Enabled = false };
        _usageItem = new ToolStripMenuItem { Enabled = false };
        _codexItem = new ToolStripMenuItem { Enabled = false };
        _espItem = new ToolStripMenuItem { Enabled = false };
        _usageRefreshItem = new ToolStripMenuItem("Refresh usage now", null, (_, _) => _core.RefreshNow());
        _usageTrackItem = new ToolStripMenuItem("Read usage every 10 min", null, (_, _) => _core.ToggleUsageTracking());
        _codexTrackItem = new ToolStripMenuItem("Read Codex usage every 10 min", null, (_, _) => _core.ToggleCodexTracking());
        var setEspItem = new ToolStripMenuItem("Set ESP32 address…", null, (_, _) => PromptForEsp());
        _hooksItem = new ToolStripMenuItem("Auto-configure Claude Code hooks", null, (_, _) => ToggleClaudeHooks())
        {
            Checked = ClaudeHooks.IsInstalled()
        };
        _soundItem = new ToolStripMenuItem("Play sounds (waiting / done)", null, (_, _) => _core.ToggleSound());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = IsStartupEnabled()
        };
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        menu.Items.Add(_statusItem);
        menu.Items.Add(_usageItem);
        menu.Items.Add(_codexItem);
        menu.Items.Add(_espItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_usageRefreshItem);
        menu.Items.Add(_usageTrackItem);
        menu.Items.Add(_codexTrackItem);
        menu.Items.Add(setEspItem);
        menu.Items.Add(_hooksItem);
        menu.Items.Add(_soundItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu
        };
        Render();
        _core.Changed += Render;

        // --- first run: register autostart so it "just works" after a reboot ---
        if (_core.TakeFirstRun())
        {
            SetStartup(true);
            _startupItem.Checked = true;
        }

        try
        {
            _core.StartListener();
        }
        catch (HttpListenerException ex)
        {
            MessageBox.Show(
                $"Could not start the local listener on port {HookListener.Port}.\n\n{ex.Message}\n\n" +
                "If this is an access error, run once in an elevated prompt:\n" +
                $"netsh http add urlacl url=http://localhost:{HookListener.Port}/ user=%USERNAME%",
                "Claude Code Indicator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _tray.ShowBalloonTip(2000, "Claude Code Indicator",
            $"Listening on http://localhost:{HookListener.Port}", ToolTipIcon.Info);

        // --- usage credits + Codex usage: first reads shortly after start, then every 10 min ---
        _core.StartTimers();
    }

    /// <summary>Copies the core's state into the icon, tooltip and menu. UI thread only.</summary>
    private void Render()
    {
        _tray.Icon = _icons[(int)_core.State];
        string tip = _core.TooltipText;
        _tray.Text = tip.Length > 127 ? tip[..127] : tip; // NotifyIcon.Text limit

        _statusItem.Text = _core.StatusText;
        _usageItem.Text = _core.UsageText;
        _codexItem.Text = _core.CodexText;
        _espItem.Text = _core.EspText;
        _usageRefreshItem.Enabled = !_core.UsageReadInProgress;
        _usageTrackItem.Checked = !_core.Settings.UsageTrackingOff;
        _codexTrackItem.Checked = !_core.Settings.CodexUsageOff;
        _soundItem.Checked = !_core.Settings.SoundMuted;
    }

    // --------------------------------------------------------------------- ESP

    private void PromptForEsp()
    {
        using var dlg = new IpInputForm(_core.Settings.EspAddress ?? string.Empty);
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _core.SetEspAddress(dlg.Value);
    }

    // --------------------------------------------------- Claude Code settings

    private void ToggleClaudeHooks()
    {
        bool installed;
        try
        {
            installed = ClaudeHooks.Toggle();
        }
        catch (ClaudeSettingsException ex)
        {
            MessageBox.Show(ex.Message, "Claude Code Indicator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _hooksItem.Checked = installed;
        _tray.ShowBalloonTip(2000, "Claude Code Indicator",
            installed
                ? "Added indicator hooks to settings.json (.bak saved)"
                : "Removed indicator hooks from settings.json",
            ToolTipIcon.Info);
    }

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

    // ------------------------------------------------------------------- icons

    private static Icon LoadIcon(string fileName, IndicatorState state, List<IntPtr> owned)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(path))
        {
            try { return new Icon(path); }
            catch { /* bad file -> generated fallback */ }
        }
        var (r, g, b) = IndicatorStates.Rgb(state);
        return MakeCircleIcon(Color.FromArgb(r, g, b), owned);
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
            _core.Changed -= Render;
            _core.Dispose();   // listener, timers, hidden claude session
            _sound.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            foreach (var h in _ownedHandles)
            {
                try { DestroyIcon(h); } catch { }
            }
        }
        base.Dispose(disposing);
    }
}
