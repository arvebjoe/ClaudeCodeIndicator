using System;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace ClaudeCodeIndicator;

/// <summary>Plays the chimes with <c>/usr/bin/afplay</c>, which handles MP3 and returns at once.</summary>
internal sealed class AfplaySoundPlayer : ISoundPlayer
{
    private bool _unavailable; // afplay missing or refused to start — stop retrying

    public void Play(SoundCue cue)
    {
        if (_unavailable) return;
        string? path = AppResources.Find(Path.Combine(".sounds", cue == SoundCue.Waiting ? "pop.mp3" : "done.mp3"));
        if (path is null) return;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/afplay") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose(); // plays on its own; Dispose doesn't stop it
        }
        catch { _unavailable = true; }
    }
}

/// <summary>
/// "Start at login" as a per-user LaunchAgent in ~/Library/LaunchAgents. It stores the
/// executable's path, so the .app mustn't be moved after turning this on.
/// </summary>
internal static class LaunchAgent
{
    private const string Label = "com.claudecodeindicator.agent";

    private static string PlistPath => Path.Combine(ExecutableSearch.HomeDirectory, "Library", "LaunchAgents", Label + ".plist");

    public static bool IsEnabled => File.Exists(PlistPath);

    /// <summary>True when running from inside an .app bundle (not `dotnet run`).</summary>
    public static bool RunningFromBundle =>
        Environment.ProcessPath?.Contains(".app/Contents/MacOS/", StringComparison.Ordinal) == true;

    public static void Set(bool enable)
    {
        if (!enable)
        {
            File.Delete(PlistPath); // takes effect at next login; nothing to unload, it already ran
            return;
        }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("executable path unknown");
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{SecurityElement.Escape(exe)}</string>
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>ProcessType</key>
              <string>Interactive</string>
            </dict>
            </plist>
            """);
    }
}

/// <summary>Banner notifications through osascript (no entitlements or signing needed).</summary>
internal static class Notifier
{
    public static void Show(string title, string message)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"display notification {Quote(message)} with title {Quote(title)}");
            Process.Start(psi)?.Dispose();
        }
        catch { /* a notification is never worth failing over */ }
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
