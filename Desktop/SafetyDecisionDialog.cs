#if CROSS_PLATFORM
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
namespace PodcastBatchCleaner.CrossPlatform;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
namespace PodcastBatchCleanerWpf;
#endif

internal static class SafetyDecisionDialog
{
    public static Task<string?> ShowAsync(MainWindow owner, string title, string message,
        IReadOnlyList<(string Id, string Text)> choices)
    {
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        panel.Children.Add(new ScrollViewer { Content = new TextBlock
        { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) } });
        var dialog = new Window { Title = title, Width = 620, Height = 440, MinWidth = 440, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
            Background = new SolidColorBrush(Color.FromRgb(25, 26, 28)), Foreground = Brushes.White };
#if CROSS_PLATFORM
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; dialog.Close(); } };
#else
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; dialog.Close(); } };
#endif
#if !CROSS_PLATFORM
        dialog.Owner = owner;
        dialog.Resources.MergedDictionaries.Add(owner.Resources);
        string? selected = null;
#endif
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.Text, Margin = new Thickness(4, 8, 0, 0), Padding = new Thickness(12, 8, 12, 8) };
            button.Click += (_, _) =>
            {
#if CROSS_PLATFORM
                dialog.Close(choice.Id);
#else
                selected = choice.Id; dialog.Close();
#endif
            };
            actions.Children.Add(button);
        }
#if CROSS_PLATFORM
        return dialog.ShowDialog<string?>(owner);
#else
        dialog.ShowDialog(); return Task.FromResult(selected);
#endif
    }
}
