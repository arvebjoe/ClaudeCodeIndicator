using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

        ExtendPath();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
