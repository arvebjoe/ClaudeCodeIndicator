using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace ClaudeCodeIndicator;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Single instance: a second launch (e.g. login item + manual) just exits,
        // so two listeners never fight over the port.
        using var mutex = new Mutex(true, "ClaudeCodeIndicator_SingleInstance", out bool isNew);
        if (!isNew) return 0;

        // A menu-bar app started from Finder has nowhere to show a crash, so log it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("unhandled", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write("unobserved task", e.Exception);
        try
        {
            ExtendPath();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("startup", ex);
            throw;
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false }) // menu-bar only
            .LogToTrace();

    /// <summary>
    /// An app started from Finder or at login gets PATH=/usr/bin:/bin:/usr/sbin:/sbin, not
    /// the user's shell PATH. Add the usual install folders so claude and codex are found,
    /// and so npm-installed ones (#!/usr/bin/env node) can find node.
    /// </summary>
    private static void ExtendPath()
    {
        var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (string d in ExecutableSearch.UnixFallbackDirectories())
            if (!dirs.Contains(d) && Directory.Exists(d)) dirs.Add(d);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, dirs.Distinct()));
    }
}

/// <summary>Appends crashes to ~/Library/Logs/ClaudeCodeIndicator.log (visible in Console.app).</summary>
internal static class CrashLog
{
    public static void Write(string kind, object error)
    {
        try
        {
            string dir = Path.Combine(ExecutableSearch.HomeDirectory, "Library", "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ClaudeCodeIndicator.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind}: {error}{Environment.NewLine}");
        }
        catch { /* nothing left to report to */ }
    }
}
