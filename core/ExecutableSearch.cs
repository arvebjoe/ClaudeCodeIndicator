using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCodeIndicator;

/// <summary>Finds CLI tools (claude, codex) the way a shell would, plus a few install folders.</summary>
public static class ExecutableSearch
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The folders on PATH, in order.</summary>
    public static IEnumerable<string> PathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);

    /// <summary>
    /// Install folders that are often missing from PATH: a GUI app started at login on
    /// macOS gets only /usr/bin:/bin:/usr/sbin:/sbin, not the user's shell PATH.
    /// </summary>
    public static IEnumerable<string> UnixFallbackDirectories() => new[]
    {
        Path.Combine(Home, ".local", "bin"),
        Path.Combine(Home, ".claude", "local"),
        Path.Combine(Home, ".npm-global", "bin"),
        "/opt/homebrew/bin",
        "/usr/local/bin",
    };

    /// <summary>
    /// The first existing file, trying each name in every folder before moving on to the
    /// next name (so a native exe anywhere wins over a script shim).
    /// </summary>
    public static string? Find(IEnumerable<string> fileNames, IEnumerable<string> directories)
    {
        var dirs = new List<string>(directories);
        foreach (string name in fileNames)
            foreach (string dir in dirs)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(p)) return p;
                }
                catch { /* malformed PATH entry */ }
            }
        return null;
    }

    public static string HomeDirectory => Home;
}
