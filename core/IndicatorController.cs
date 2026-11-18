using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeIndicator;

/// <summary>
/// Everything the indicator does apart from drawing it: hook state (latest event wins,
/// globally), the usage and Codex reads, ESP pushes, chimes and settings. A front end
/// creates one on its UI thread, renders from its properties whenever <see cref="Changed"/>
/// fires, and calls its methods from menu clicks.
///
/// Threading: all state lives on the UI thread. Hook events and timer ticks are posted
/// there through the <see cref="SynchronizationContext"/> given to the constructor, and
/// the async reads resume there, so nothing here needs locking. <see cref="Changed"/> is
/// raised on the UI thread too.
/// </summary>
public sealed class IndicatorController : IDisposable
{
    // Usage credits, read from a hidden claude session's /usage screen every 10 minutes and whenever Claude goes idle.
    public static readonly TimeSpan UsageInterval = TimeSpan.FromMinutes(10);
    // A Stop hook (→ idle) also triggers a fresh read, but not more often than this.
    private static readonly TimeSpan UsageOnIdleMinGap = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstUsageRead = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstCodexRead = TimeSpan.FromSeconds(15); // staggered so the reads don't overlap

    private readonly SynchronizationContext _ui;
    private readonly SettingsStore _store;
    private readonly ISoundPlayer _sound;
    private readonly HookListener _listener = new();
    private readonly EspClient _esp = new();
    private readonly UsageProbe _usageProbe;
    private readonly CodexUsageProbe _codexProbe;
    private readonly Timer _usageTimer;
    private readonly Timer _codexTimer;
    private DateTime _lastUsageRead = DateTime.MinValue;
    private bool _codexReading;
    private bool _anyState;     // false until the first hook event: the status reads "Idle"

    /// <param name="dataDirectory">Where settings.json and the probes' working folders go.</param>
    /// <param name="ui">The UI thread's context; every state change runs there.</param>
    /// <param name="terminalFactory">Creates the pseudo terminal for the hidden claude session.</param>
    /// <param name="sound">Plays the chimes.</param>
    public IndicatorController(string dataDirectory, SynchronizationContext ui,
                               Func<IPseudoTerminal> terminalFactory, ISoundPlayer sound)
    {
        Directory.CreateDirectory(dataDirectory);
        _ui = ui;
        _sound = sound;
        _store = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
        _usageProbe = new UsageProbe(Path.Combine(dataDirectory, "usage-probe"), terminalFactory);
        _codexProbe = new CodexUsageProbe(Path.Combine(dataDirectory, "codex-probe"));

        _usageTimer = new Timer(_ => Post(() => _ = RefreshUsageAsync()));
        _codexTimer = new Timer(_ => Post(() => _ = RefreshCodexUsageAsync()));

        _listener.ClaudeState += s => Post(() => ApplyState(s));
        _listener.CodexState += s => Post(() => ApplyCodexState(s));

        if (Settings.CodexUsageOff) CodexText = "Codex: off";
    }

    /// <summary>Raised on the UI thread after anything shown in the menu or tooltip changed.</summary>
    public event Action? Changed;

    public AppSettings Settings => _store.Settings;
    public void SaveSettings() => _store.Save();

    public IndicatorState State { get; private set; } = IndicatorState.Done;
    public IndicatorState CodexState { get; private set; } = IndicatorState.Done;
    public UsageReading? Usage { get; private set; }
    public CodexUsageReading? CodexUsage { get; private set; }

    /// <summary>A /usage read is running; the "Refresh usage now" item should be disabled.</summary>
    public bool UsageReadInProgress { get; private set; }

    public string StatusLabel => _anyState ? IndicatorStates.Describe(State).Label : "Idle";
    public string StatusText => $"Status: {StatusLabel}";
    public string UsageText { get; private set; } = "Usage: not read yet";
    public string CodexText { get; private set; } = "Codex: not read yet";
    public string EspText => string.IsNullOrWhiteSpace(Settings.EspAddress)
        ? "ESP32: not set"
        : $"ESP32: {Settings.EspAddress}";

    /// <summary>Multi-line tooltip. The front end truncates it to its platform's limit.</summary>
    public string TooltipText
    {
        get
        {
            string text = $"Claude Code — {StatusLabel}";
            if (Usage is not null) text += $"\n{Usage.Short}";
            if (CodexUsage is not null) text += $"\nCodex {CodexUsage.Short}";
            return text;
        }
    }

    /// <summary>
    /// True exactly once, on the very first launch, so the front end can turn on
    /// start-at-login. The flag is saved right away.
    /// </summary>
    public bool TakeFirstRun()
    {
        if (Settings.FirstRunDone) return false;
        Settings.FirstRunDone = true;
        SaveSettings();
        return true;
    }

    /// <summary>Starts the usage/Codex timers (as enabled in settings).</summary>
    public void StartTimers()
    {
        if (!Settings.UsageTrackingOff) _usageTimer.Change(FirstUsageRead, UsageInterval);
        if (!Settings.CodexUsageOff) _codexTimer.Change(FirstCodexRead, UsageInterval);
    }

    /// <summary>Starts the hook listener. Throws <see cref="System.Net.HttpListenerException"/> if the port is taken.</summary>
    public void StartListener() => _listener.Start();

    // ------------------------------------------------------------------- state

    private void ApplyState(IndicatorState s)
    {
        // Chime only on the transition *into* Waiting/Done, so a repeated Notification
        // hook for the same prompt doesn't pop twice.
        bool entersWaiting = s == IndicatorState.Waiting && State != IndicatorState.Waiting;
        bool entersDone    = s == IndicatorState.Done    && State != IndicatorState.Done;
        State = s;
        _anyState = true;
        OnChanged();
        _ = PushToEspAsync();
        if (entersWaiting) PlaySound(SoundCue.Waiting);
        if (entersDone)    PlaySound(SoundCue.Done);

        // Going idle: the push above already carried the last known budget; also
        // read /usage again so the gauge reflects the turn that just finished
        // (RefreshUsageAsync pushes the new value to the ESP when done).
        if (entersDone && !Settings.UsageTrackingOff &&
            DateTime.UtcNow - _lastUsageRead > UsageOnIdleMinGap)
            _ = RefreshUsageAsync();
    }

    // Codex's own state, from optional Codex hooks POSTing /codex/<state> (see
    // firmware/ClaudeCodeIndicatorRound/README.md). Only forwarded to the ESP.
    private void ApplyCodexState(IndicatorState s)
    {
        if (CodexState == s) return;
        CodexState = s;
        _ = PushToEspAsync();
    }

    // ------------------------------------------------------------------- usage

    /// <summary>"Refresh usage now": reads /usage, and Codex too unless that's off.</summary>
    public void RefreshNow()
    {
        _ = RefreshUsageAsync();
        if (!Settings.CodexUsageOff) _ = RefreshCodexUsageAsync();
    }

    /// <summary>Runs on the UI thread; the probe's awaits resume here too.</summary>
    private async Task RefreshUsageAsync()
    {
        if (UsageReadInProgress) return;
        UsageReadInProgress = true;
        _lastUsageRead = DateTime.UtcNow;
        if (Usage is null) UsageText = "Usage: reading…";
        OnChanged();
        try
        {
            var reading = await _usageProbe.ReadAsync();
            if (reading is not null) Usage = reading;
            UsageText = Usage is null
                ? "Usage: couldn't read /usage"
                : $"Usage: {Usage.Short}" + (reading is null ? " (stale)" : "");
        }
        catch (Exception ex)
        {
            // claude missing, no pseudo terminal, ... — keep the last value.
            UsageText = Usage is null ? $"Usage: unavailable ({ex.Message})" : $"Usage: {Usage.Short} (stale)";
            _usageProbe.Dispose(); // next read starts a fresh session
        }
        finally
        {
            UsageReadInProgress = false;
            OnChanged();
            _ = PushToEspAsync(); // refresh the matrix's quota gauge
        }
    }

    public void ToggleUsageTracking()
    {
        Settings.UsageTrackingOff = !Settings.UsageTrackingOff;
        SaveSettings();
        if (Settings.UsageTrackingOff)
        {
            _usageTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _usageProbe.Dispose(); // close the hidden claude session
            Usage = null;          // don't keep showing a value that won't update
            UsageText = "Usage: off";
            OnChanged();
            _ = PushToEspAsync(); // ESP falls back to the mascot
        }
        else
        {
            _usageTimer.Change(UsageInterval, UsageInterval);
            OnChanged();
            _ = RefreshUsageAsync();
        }
    }

    /// <summary>Runs on the UI thread. Spawns codex app-server, asks once, kills it.</summary>
    private async Task RefreshCodexUsageAsync()
    {
        if (_codexReading) return;
        _codexReading = true;
        if (CodexUsage is null) { CodexText = "Codex: reading…"; OnChanged(); }
        try
        {
            if (!CodexUsageProbe.IsInstalled)
            {
                CodexText = "Codex: not installed";
                return;
            }
            CodexUsage = await _codexProbe.ReadAsync();
            CodexText = $"Codex: {CodexUsage.Short}" +
                (CodexUsage.ResetsText is { } r ? $" · resets {r}" : "");
        }
        catch (Exception ex)
        {
            // Not signed in, older codex without the method, timeout, ... — keep the last value.
            CodexText = CodexUsage is null
                ? $"Codex: unavailable ({ex.Message})"
                : $"Codex: {CodexUsage.Short} (stale)";
        }
        finally
        {
            _codexReading = false;
            OnChanged();
            _ = PushToEspAsync(); // refresh the round display's Codex ring
        }
    }

    public void ToggleCodexTracking()
    {
        Settings.CodexUsageOff = !Settings.CodexUsageOff;
        SaveSettings();
        if (Settings.CodexUsageOff)
        {
            _codexTimer.Change(Timeout.Infinite, Timeout.Infinite);
            CodexUsage = null;
            CodexText = "Codex: off";
            _ = PushToEspAsync(); // Codex ring goes dim (unknown)
            OnChanged();
        }
        else
        {
            _codexTimer.Change(UsageInterval, UsageInterval);
            OnChanged();
            _ = RefreshCodexUsageAsync();
        }
    }

    // ------------------------------------------------------------------- sound

    private void PlaySound(SoundCue cue)
    {
        if (Settings.SoundMuted) return;
        try { _sound.Play(cue); } catch { /* a chime is never worth crashing over */ }
    }

    public void ToggleSound()
    {
        Settings.SoundMuted = !Settings.SoundMuted;
        SaveSettings();
        OnChanged();
        if (!Settings.SoundMuted) PlaySound(SoundCue.Waiting); // preview so the toggle is audible
    }

    // --------------------------------------------------------------------- ESP

    /// <summary>Saves the ESP address (blank disables it) and syncs the ESP right away.</summary>
    public void SetEspAddress(string? value)
    {
        value = value?.Trim();
        Settings.EspAddress = string.IsNullOrWhiteSpace(value) ? null : value;
        SaveSettings();
        OnChanged();
        _ = PushToEspAsync(); // sync the ESP to the current color right away
    }

    private Task PushToEspAsync()
    {
        string? address = Settings.EspAddress;
        if (string.IsNullOrWhiteSpace(address)) return Task.CompletedTask; // ESP optional
        string url = EspClient.BuildUrl(address, State, Usage, CodexState, CodexUsage,
                                        DateOnly.FromDateTime(DateTime.Now));
        return _esp.PushAsync(url);
    }

    // ------------------------------------------------------------------ plumbing

    private void Post(Action action) => _ui.Post(_ => action(), null);

    private void OnChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _listener.Dispose();
        _usageTimer.Dispose();
        _codexTimer.Dispose();
        _usageProbe.Dispose(); // closes the hidden claude session
        _esp.Dispose();
    }
}
