using Avalonia.Controls;

namespace PodcastBatchCleaner.CrossPlatform.Services;

public enum MessageBoxButton { OK }
public enum MessageBoxImage { Information, Warning }

public static class DesktopMessageBox
{
    public static void Show(Window owner, string message, string title, MessageBoxButton button, MessageBoxImage image)
    {
        var close = new Button { Content = "關閉", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var panel = new DockPanel { Margin = new Avalonia.Thickness(20) };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        panel.Children.Add(new ScrollViewer
        {
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 16) }
        });
        var dialog = new Window { Title = title, Width = 560, Height = 380, MinWidth = 400, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        close.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(owner);
    }
}
