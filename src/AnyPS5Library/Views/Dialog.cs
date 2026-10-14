using Avalonia.Controls;
using Avalonia.Layout;

namespace AnyPS5Library.Views;

/// <summary>Minimal modal alert / confirm (Avalonia ships no message box).</summary>
public static class Dialog
{
    public static async Task<bool> ShowAsync(Window owner, string title, string message, bool confirm)
    {
        bool result = false;
        var window = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var ok = new Button { Content = confirm ? "Continue" : "OK", Classes = { "accent" }, IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, IsVisible = confirm };
        ok.Click += (_, _) => { result = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 15 },
                new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };
        await window.ShowDialog(owner);
        return result;
    }
}
