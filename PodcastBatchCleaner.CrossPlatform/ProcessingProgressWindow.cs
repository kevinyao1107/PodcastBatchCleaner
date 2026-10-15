using Avalonia;
using Avalonia.Controls;

namespace PodcastBatchCleaner.CrossPlatform;

public sealed class ProcessingProgressWindow : Window
{
    private readonly TextBlock _stage = new() { Text = "正在處理", FontSize = 18 };
    private readonly TextBlock _file = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _percent = new();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 12 };
    private bool _canClose, _cancelRequested;
    public new Window? Owner { get; set; }
    public event EventHandler? CancelRequested;
    public ProcessingProgressWindow()
    {
        Title = "音檔處理進度"; Width = 480; Height = 260; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Content = "取消處理", CornerRadius = new CornerRadius(17) };
        cancel.Click += (_, _) => RequestCancel();
        Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = { _stage, _file, _progress, _percent, cancel } };
        Closing += (_, e) => { if (!_canClose) { e.Cancel = true; RequestCancel(); } };
    }
    public new void Show() { if (Owner is { } owner) base.Show(owner); else base.Show(); }
    public void SetProgress(string fileName, double percent)
    {
        var split = fileName.IndexOf('：');
        _stage.Text = split > 0 ? fileName[..split] : "正在處理音檔";
        _file.Text = split > 0 ? fileName[(split + 1)..] : fileName;
        _progress.IsIndeterminate = false; _progress.Value = Math.Clamp(percent, 0, 100);
        _percent.Text = $"{_progress.Value:0}%";
    }
    public void SetIndeterminate(string stage, string fileName)
    { _stage.Text = stage; _file.Text = fileName; _progress.IsIndeterminate = true; _percent.Text = "處理中"; }
    public void SetStatus(string status) => _stage.Text = status;
    public void CompleteAndClose() { _canClose = true; Close(); }
    private void RequestCancel()
    { if (_cancelRequested) return; _cancelRequested = true; SetStatus("正在取消..."); CancelRequested?.Invoke(this, EventArgs.Empty); }
}
