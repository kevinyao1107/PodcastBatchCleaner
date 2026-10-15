using System.ComponentModel;
using System.IO;
using PodcastBatchCleaner.Core.Models;
#if CROSS_PLATFORM
namespace PodcastBatchCleaner.CrossPlatform;
#else
namespace PodcastBatchCleanerWpf;
#endif

public partial class MainWindow
{
    private void InitializeWorkspaceUi() => AudioFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FileCountText));
    public bool HasProcessingReport => _processingReportItem is not null;
    public bool HasNoAudioFiles => AudioFiles.Count == 0;
    public AudioFileItem? SelectedAudioItem => _selectedFile;
    public bool HasSelectedAudioItem => _selectedFile is not null;
#if !CROSS_PLATFORM
    private void AudioList_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (AudioList.View is System.Windows.Controls.GridView view && view.Columns.Count > 0)
            view.Columns[0].Width = Math.Max(180, AudioList.ActualWidth - 220);
    }
#endif
    private int SelectedExportCount => AudioList.SelectedItems.Count > 0
        ? AudioList.SelectedItems.Count : _selectedFile is null ? 0 : 1;
    public bool CanExportSelection => CanStartWork && SelectedExportCount > 0;
    public bool CanExportAll => CanStartWork && AudioFiles.Count > 0;
    public string ExportSelectedText => MergeSegments ? "合併選取" : "輸出選取";
    public string ExportAllText => MergeSegments ? "合併全部" : "輸出全部";
    public string OutputScopeText => $"已選 {SelectedExportCount} / {AudioFiles.Count} 個音檔 · "
        + (MergeSegments ? "合併成單一檔案" : "每個音檔分別輸出");
    public string PlaybackContextText => PlaybackSourceIndex switch
    {
        2 => _lastMergedOutputPath is null ? "尚無整集成品" : $"整集 · {Path.GetFileName(_lastMergedOutputPath)}",
        3 => _effectPlaybackPath is null ? "尚無單項比較" : $"單項比較 · {Path.GetFileName(_effectPlaybackPath)}",
        _ => _selectedFile is null ? "尚未選取音檔" : $"{(PlaybackSourceIndex == 0 ? "原音" : "處理後")} · {_selectedFile.FileName}"
    };

    private void RefreshWorkspaceUi()
    {
        foreach (var name in new[] { nameof(CanExportSelection), nameof(CanExportAll), nameof(ExportSelectedText),
            nameof(ExportAllText), nameof(OutputScopeText), nameof(PlaybackContextText), nameof(HasNoAudioFiles), nameof(HasProcessingReport),
            nameof(SelectedAudioItem), nameof(HasSelectedAudioItem) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
