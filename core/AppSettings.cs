using System.IO;
using System.Text.Json;

namespace ClaudeCodeIndicator;

public sealed class AppSettings
{
    public string? EspAddress { get; set; }
    public bool FirstRunDone { get; set; }
    /// <summary>Silences the Waiting and Done chimes. Muted-not-Enabled so the default (false) plays sound.</summary>
    public bool SoundMuted { get; set; }
    /// <summary>Turns off the periodic /usage reading (and the hidden claude session it keeps).</summary>
    public bool UsageTrackingOff { get; set; }
    /// <summary>Turns off the periodic Codex usage reading (codex app-server).</summary>
    public bool CodexUsageOff { get; set; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Errors fall back to defaults.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
        Settings = Load();
    }

    public AppSettings Settings { get; }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Settings, Indented)); }
        catch { /* non-fatal */ }
    }
}
