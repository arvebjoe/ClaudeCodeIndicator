using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace ClaudeCodeIndicator;

/// <summary>Code-only Avalonia application: no main window, just the menu-bar item.</summary>
internal sealed class App : Application
{
    private MenuBar? _menuBar;

    public override void Initialize()
    {
        Name = "Claude Code Indicator";
        Styles.Add(new FluentTheme()); // for the small dialogs
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Dialogs come and go; only "Quit" ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _menuBar = new MenuBar(this, desktop);
            desktop.Exit += (_, _) => _menuBar?.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
