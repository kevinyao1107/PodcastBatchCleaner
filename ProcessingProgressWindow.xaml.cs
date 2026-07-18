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
        StatusText.Text = "正在處理音檔";
        FileText.Text = fileName;
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
}
