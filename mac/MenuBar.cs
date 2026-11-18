using System;
using System.IO;
using System.Net;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace ClaudeCodeIndicator;

/// <summary>
/// The macOS front end: a menu-bar item with a menu, rendered from
/// <see cref="IndicatorController"/> (core/), which does the actual work.
/// Same menu as the Windows tray app.
/// </summary>
internal sealed class MenuBar : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IndicatorController _core;
    private readonly AfplaySoundPlayer _sound = new();
    private readonly TrayIcon _tray;
    private readonly WindowIcon[] _icons = new WindowIcon[3];
    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _usageItem;
    private readonly NativeMenuItem _codexItem;
    private readonly NativeMenuItem _espItem;
    private readonly NativeMenuItem _usageRefreshItem;
    private readonly NativeMenuItem _usageTrackItem;
    private readonly NativeMenuItem _codexTrackItem;
    private readonly NativeMenuItem _hooksItem;
    private readonly NativeMenuItem _soundItem;
    private readonly NativeMenuItem _loginItem;
    private bool _espDialogOpen;

    public MenuBar(Application app, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;

        // --- core (~/Library/Application Support/ClaudeCodeIndicator) ---
        string dir = Path.Combine(ExecutableSearch.HomeDirectory, "Library", "Application Support", "ClaudeCodeIndicator");
        _core = new IndicatorController(dir, new UiThreadContext(), () => new UnixPtyTerminal(), _sound);

        // --- icons: <state>.png from the bundle if present, else a drawn dot ---
        foreach (IndicatorState s in Enum.GetValues<IndicatorState>())
            _icons[(int)s] = DotIcons.Load(s);

        // --- menu ---
        _statusItem = Info();
        _usageItem = Info();
        _codexItem = Info();
        _espItem = Info();
        _usageRefreshItem = Action("Refresh usage now", () => _core.RefreshNow());
        _usageTrackItem = Toggle("Read usage every 10 min", () => _core.ToggleUsageTracking());
        _codexTrackItem = Toggle("Read Codex usage every 10 min", () => _core.ToggleCodexTracking());
        var setEspItem = Action("Set ESP32 address…", PromptForEsp);
        _hooksItem = Toggle("Auto-configure Claude Code hooks", ToggleClaudeHooks);
        _hooksItem.IsChecked = ClaudeHooks.IsInstalled();
        _soundItem = Toggle("Play sounds (waiting / done)", () => _core.ToggleSound());
        _loginItem = Toggle("Start at login", ToggleLogin);
        _loginItem.IsChecked = LaunchAgent.IsEnabled;
        var quitItem = Action("Quit", () => _lifetime.Shutdown());

        var menu = new NativeMenu();
        menu.Add(_statusItem);
        menu.Add(_usageItem);
        menu.Add(_codexItem);
        menu.Add(_espItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_usageRefreshItem);
        menu.Add(_usageTrackItem);
        menu.Add(_codexTrackItem);
        menu.Add(setEspItem);
        menu.Add(_hooksItem);
        menu.Add(_soundItem);
        menu.Add(_loginItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quitItem);

        _tray = new TrayIcon { Menu = menu, IsVisible = true };
        TrayIcon.SetIcons(app, new TrayIcons { _tray });
        Render();
        _core.Changed += Render;

        // --- first run from the .app: start at login so it "just works" after a reboot.
        // Not from `dotnet run`, or the login item would point into bin/Debug.
        if (LaunchAgent.RunningFromBundle && _core.TakeFirstRun())
        {
            LaunchAgent.Set(true);
            _loginItem.IsChecked = LaunchAgent.IsEnabled;
        }

        try
        {
            _core.StartListener();
        }
        catch (HttpListenerException ex)
        {
            Dialogs.ShowMessage("Claude Code Indicator",
                $"Could not start the local listener on port {HookListener.Port}.\n\n{ex.Message}\n\n" +
                "Is another copy of the indicator, or another program, using that port?");
        }

        // --- usage credits + Codex usage: first reads shortly after start, then every 10 min ---
        _core.StartTimers();
    }

    /// <summary>Copies the core's state into the icon, tooltip and menu. UI thread only.</summary>
    private void Render()
    {
        _tray.Icon = _icons[(int)_core.State];
        _tray.ToolTipText = _core.TooltipText;

        _statusItem.Header = _core.StatusText;
        _usageItem.Header = _core.UsageText;
        _codexItem.Header = _core.CodexText;
        _espItem.Header = _core.EspText;
        _usageRefreshItem.IsEnabled = !_core.UsageReadInProgress;
        _usageTrackItem.IsChecked = !_core.Settings.UsageTrackingOff;
        _codexTrackItem.IsChecked = !_core.Settings.CodexUsageOff;
        _soundItem.IsChecked = !_core.Settings.SoundMuted;
    }

    private static NativeMenuItem Info() => new() { IsEnabled = false };

    private static NativeMenuItem Action(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static NativeMenuItem Toggle(string header, Action onClick)
    {
        var item = Action(header, onClick);
        item.ToggleType = MenuItemToggleType.CheckBox;
        return item;
    }

    // --------------------------------------------------------------------- ESP

    private async void PromptForEsp()
    {
        if (_espDialogOpen) return;
        _espDialogOpen = true;
        try
        {
            string? value = await Dialogs.PromptAsync("ESP32 address",
                "ESP32 IP address or hostname (leave blank to disable):",
                _core.Settings.EspAddress ?? string.Empty);
            if (value is not null) _core.SetEspAddress(value);
        }
        finally { _espDialogOpen = false; }
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
            _hooksItem.IsChecked = ClaudeHooks.IsInstalled(); // undo the menu's own check flip
            Dialogs.ShowMessage("Claude Code Indicator", ex.Message);
            return;
        }

        _hooksItem.IsChecked = installed;
        Notifier.Show("Claude Code Indicator",
            installed
                ? "Added indicator hooks to settings.json (.bak saved)"
                : "Removed indicator hooks from settings.json");
    }

    // ------------------------------------------------------------------- login

    private void ToggleLogin()
    {
        try { LaunchAgent.Set(!LaunchAgent.IsEnabled); }
        catch (Exception ex) { Dialogs.ShowMessage("Claude Code Indicator", $"Could not change the login item.\n\n{ex.Message}"); }
        _loginItem.IsChecked = LaunchAgent.IsEnabled;
    }

    public void Dispose()
    {
        _core.Changed -= Render;
        _core.Dispose();   // listener, timers, hidden claude session
        _tray.IsVisible = false;
        _tray.Dispose();
    }

    /// <summary>Posts the core's callbacks to Avalonia's UI thread.</summary>
    private sealed class UiThreadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => Dispatcher.UIThread.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state) => Dispatcher.UIThread.Invoke(() => d(state));
        public override SynchronizationContext CreateCopy() => this;
    }
}
