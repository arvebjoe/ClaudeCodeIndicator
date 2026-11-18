using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClaudeCodeIndicator;

/// <summary>The two small windows the app needs: a text prompt and a message box.</summary>
internal static class Dialogs
{
    /// <summary>Returns the entered text, or null if cancelled.</summary>
    public static Task<string?> PromptAsync(string title, string prompt, string current)
    {
        var tcs = new TaskCompletionSource<string?>();
        var box = new TextBox { Text = current };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        var window = MakeWindow(title, new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap },
                box,
                Buttons(cancel, ok),
            }
        });

        ok.Click += (_, _) => { tcs.TrySetResult(box.Text ?? ""); window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Closed += (_, _) => tcs.TrySetResult(null);
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        Present(window);
        return tcs.Task;
    }

    public static void ShowMessage(string title, string message)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        var window = MakeWindow(title, new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                Buttons(ok),
            }
        });
        ok.Click += (_, _) => window.Close();
        Present(window);
    }

    private static Window MakeWindow(string title, Control content) => new()
    {
        Title = title,
        Width = 380,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        ShowInTaskbar = false,
        Topmost = true,
        Content = new Border { Padding = new Avalonia.Thickness(18), Child = content },
    };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    /// <summary>A menu-bar app isn't the active app, so bring the window forward explicitly.</summary>
    private static void Present(Window window)
    {
        window.Show();
        window.Activate();
    }
}
