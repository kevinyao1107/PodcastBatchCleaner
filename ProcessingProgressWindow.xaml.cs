using System.Windows;

namespace PodcastBatchCleanerWpf;

public partial class ProcessingProgressWindow : Window
{
    public ProcessingProgressWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? CancelRequested;

    public void SetProgress(string fileName, double percent)
    {
        var clampedPercent = Math.Clamp(percent, 0, 100);
        var (stage, displayFileName) = SplitProgressText(fileName);
        StatusText.Text = stage;
        FileText.Text = displayFileName;
        Progress.Value = clampedPercent;
        PercentText.Text = $"{clampedPercent:0}%";
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("正在取消...");
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private static (string Stage, string FileName) SplitProgressText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ("正在處理音檔", string.Empty);
        }

        var separatorIndex = text.IndexOf('：');
        if (separatorIndex <= 0 || separatorIndex >= text.Length - 1)
        {
            return ("正在處理音檔", text);
        }

        return (text[..separatorIndex], text[(separatorIndex + 1)..]);
    }
}
