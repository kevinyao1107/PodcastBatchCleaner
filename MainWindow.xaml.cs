using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;
using Forms = System.Windows.Forms;

namespace PodcastBatchCleanerWpf;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FfmpegAudioProcessor _processor = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _processingCancellation;
    private AudioFileItem? _selectedFile;
    private string? _currentFolder;
    private string? _outputFolder;
    private string? _ffmpegPath;
    private bool _isDraggingPosition;
    private bool _isProcessing;
    private double _silenceSeconds = 0.1;
    private double _silenceThresholdDb = -35;
    private bool _enableDenoise = true;
    private bool _reduceRoomTone;
    private double _volumeGainDb;
    private int _playbackSourceIndex;
    private string _currentProcessingFileText = string.Empty;
    private string _processingProgressText = string.Empty;
    private double _processingProgress;
    private bool _isProgressVisible;
    private string _statusText = "請先選取資料夾。";
    private string _playbackPositionText = "00:00";
    private string _playbackDurationText = "00:00";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.MediaFailed += Player_MediaFailed;
    }

    public ObservableCollection<AudioFileItem> AudioFiles { get; } = [];

    public string CurrentFolderText => _currentFolder is null
        ? "尚未選取資料夾"
        : $"目前資料夾：{_currentFolder}";

    public string OutputFolderText => _outputFolder is null
        ? "輸出資料夾：尚未設定，預設會建立在來源資料夾下的 processed"
        : $"輸出資料夾：{_outputFolder}";

    public string FfmpegPathText => _ffmpegPath is null
        ? "FFmpeg：尚未找到，請指定 ffmpeg.exe 後再輸出"
        : $"FFmpeg：{_ffmpegPath}";

    public string FileCountText => $"{AudioFiles.Count} 個音檔";

    public string SelectedFileText => _selectedFile is null
        ? "尚未選取音檔"
        : $"目前選取：{_selectedFile.FileName}";

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string CurrentProcessingFileText
    {
        get => _currentProcessingFileText;
        set => SetField(ref _currentProcessingFileText, value);
    }

    public string ProcessingProgressText
    {
        get => _processingProgressText;
        set => SetField(ref _processingProgressText, value);
    }

    public double ProcessingProgress
    {
        get => _processingProgress;
        set => SetField(ref _processingProgress, Math.Clamp(value, 0, 100));
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        set => SetField(ref _isProgressVisible, value);
    }

    public string PlaybackPositionText
    {
        get => _playbackPositionText;
        set => SetField(ref _playbackPositionText, value);
    }

    public string PlaybackDurationText
    {
        get => _playbackDurationText;
        set => SetField(ref _playbackDurationText, value);
    }

    public double SilenceSeconds
    {
        get => _silenceSeconds;
        set => SetField(ref _silenceSeconds, Math.Clamp(Math.Round(value, 1), 0.1, 1.0));
    }

    public double SilenceThresholdDb
    {
        get => _silenceThresholdDb;
        set => SetField(ref _silenceThresholdDb, Math.Clamp(Math.Round(value), -80, -5));
    }

    public bool EnableDenoise
    {
        get => _enableDenoise;
        set => SetField(ref _enableDenoise, value);
    }

    public bool ReduceRoomTone
    {
        get => _reduceRoomTone;
        set => SetField(ref _reduceRoomTone, value);
    }

    public double VolumeGainDb
    {
        get => _volumeGainDb;
        set => SetField(ref _volumeGainDb, Math.Clamp(Math.Round(value, 1), -24, 24));
    }

    public int PlaybackSourceIndex
    {
        get => _playbackSourceIndex;
        set => SetField(ref _playbackSourceIndex, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ffmpegPath = FfmpegAudioProcessor.TryFindFfmpeg();
        OnPropertyChanged(nameof(FfmpegPathText));

        StatusText = _ffmpegPath is null
            ? "找不到 FFmpeg。播放可使用；輸出前請指定 ffmpeg.exe。"
            : "已找到 FFmpeg，可以選取資料夾開始。";

        await Task.CompletedTask;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _processingCancellation?.Cancel();
        _playbackTimer.Stop();
        _player.Close();
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "選取要處理音檔的資料夾",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        _currentFolder = dialog.SelectedPath;
        _outputFolder ??= Path.Combine(_currentFolder, "processed");
        OnPropertyChanged(nameof(CurrentFolderText));
        OnPropertyChanged(nameof(OutputFolderText));

        await LoadAudioFilesAsync(_currentFolder);
    }

    private void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "選取處理後音檔輸出資料夾",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = _outputFolder ?? _currentFolder ?? string.Empty
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        _outputFolder = dialog.SelectedPath;
        OnPropertyChanged(nameof(OutputFolderText));
        StatusText = "已設定輸出資料夾。";
    }

    private void ChooseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選取 ffmpeg.exe",
            Filter = "FFmpeg|ffmpeg.exe|執行檔|*.exe|所有檔案|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _ffmpegPath = dialog.FileName;
        OnPropertyChanged(nameof(FfmpegPathText));
        StatusText = "已設定 FFmpeg。";
    }

    private async Task LoadAudioFilesAsync(string folder)
    {
        StopPlayback();
        AudioFiles.Clear();
        OnPropertyChanged(nameof(FileCountText));

        StatusText = "正在掃描資料夾...";

        var files = await Task.Run(() => Directory
            .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(FfmpegAudioProcessor.IsSupportedAudioFile)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

        foreach (var file in files)
        {
            AudioFiles.Add(new AudioFileItem { FilePath = file });
        }

        OnPropertyChanged(nameof(FileCountText));
        StatusText = files.Count == 0 ? "這個資料夾沒有支援的音檔。" : $"已載入 {files.Count} 個音檔。";

        if (_ffmpegPath is not null)
        {
            _ = LoadDurationsAsync(AudioFiles.ToList());
        }
    }

    private async Task LoadDurationsAsync(IReadOnlyList<AudioFileItem> items)
    {
        var ffmpegPath = _ffmpegPath;
        if (ffmpegPath is null)
        {
            return;
        }

        foreach (var item in items)
        {
            try
            {
                item.Duration = await _processor.ProbeDurationAsync(ffmpegPath, item.FilePath);
            }
            catch
            {
                item.Duration = null;
            }
        }
    }

    private async void ProcessSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = AudioList.SelectedItems.Cast<AudioFileItem>().ToList();
        if (selected.Count == 0 && _selectedFile is not null)
        {
            selected.Add(_selectedFile);
        }

        await ProcessItemsAsync(selected);
    }

    private async void ProcessAll_Click(object sender, RoutedEventArgs e)
    {
        await ProcessItemsAsync(AudioFiles.ToList());
    }

    private void CancelProcessing_Click(object sender, RoutedEventArgs e)
    {
        _processingCancellation?.Cancel();
        StatusText = "正在取消處理...";
    }

    private async Task ProcessItemsAsync(IReadOnlyList<AudioFileItem> items)
    {
        if (_isProcessing)
        {
            StatusText = "目前已經在處理中。";
            return;
        }

        if (items.Count == 0)
        {
            StatusText = "沒有可處理的音檔。";
            return;
        }

        var ffmpegPath = _ffmpegPath;
        if (ffmpegPath is null || !File.Exists(ffmpegPath))
        {
            StatusText = "請先指定 ffmpeg.exe。";
            return;
        }

        _outputFolder ??= _currentFolder is null
            ? Path.Combine(AppContext.BaseDirectory, "processed")
            : Path.Combine(_currentFolder, "processed");

        OnPropertyChanged(nameof(OutputFolderText));
        Directory.CreateDirectory(_outputFolder);

        _isProcessing = true;
        _processingCancellation = new CancellationTokenSource();
        IsProgressVisible = true;
        ProcessingProgress = 0;
        ProcessingProgressText = "0%";
        CurrentProcessingFileText = string.Empty;

        var options = new AudioProcessingOptions(
            SilenceSeconds,
            SilenceThresholdDb,
            EnableDenoise,
            ReduceRoomTone,
            VolumeGainDb,
            _outputFolder);

        var progressWindow = new ProcessingProgressWindow
        {
            Owner = this
        };
        progressWindow.CancelRequested += (_, _) => _processingCancellation?.Cancel();
        progressWindow.Show();

        try
        {
            var total = items.Count;
            for (var index = 0; index < total; index++)
            {
                var itemIndex = index;
                var item = items[index];
                _processingCancellation.Token.ThrowIfCancellationRequested();

                item.IsProcessing = true;
                item.Status = $"處理中 {index + 1}/{total}";
                StatusText = $"處理中：{item.FileName}";

                CurrentProcessingFileText = item.FileName;
                progressWindow.SetProgress(item.FileName, ProcessingProgress);

                var outputPath = FfmpegAudioProcessor.MakeOutputPath(
                    item.FilePath,
                    options.OutputFolder,
                    item.CustomOutputFileName);
                if (item.Duration is null)
                {
                    item.Duration = await _processor.ProbeDurationAsync(
                        ffmpegPath,
                        item.FilePath,
                        _processingCancellation.Token);
                }

                var progress = new Progress<AudioProcessingProgress>(current =>
                {
                    var overallPercent = Math.Clamp(((itemIndex + current.Percent) / total) * 100, 0, 100);
                    ProcessingProgress = overallPercent;
                    ProcessingProgressText = $"{overallPercent:0}%";
                    progressWindow.SetProgress(item.FileName, overallPercent);
                    item.Status = $"{overallPercent:0}%";
                    StatusText = $"處理中：{item.FileName}";
                });

                await _processor.ProcessAsync(
                    ffmpegPath,
                    item.FilePath,
                    outputPath,
                    options,
                    item.Duration,
                    progress,
                    cancellationToken: _processingCancellation.Token);

                item.ProcessedPath = outputPath;
                item.Status = "完成";
                item.IsProcessing = false;
                ProcessingProgress = Math.Clamp(((itemIndex + 1d) / total) * 100, 0, 100);
                ProcessingProgressText = $"{ProcessingProgress:0}%";
                progressWindow.SetProgress(item.FileName, ProcessingProgress);
            }

            StatusText = "處理完成。";
        }
        catch (OperationCanceledException)
        {
            foreach (var item in items.Where(item => item.IsProcessing))
            {
                item.IsProcessing = false;
                item.Status = "已取消";
            }

            StatusText = "處理已取消。";
        }
        catch (Exception ex)
        {
            foreach (var item in items.Where(item => item.IsProcessing))
            {
                item.IsProcessing = false;
                item.Status = "失敗";
            }

            StatusText = $"處理失敗：{ex.Message}";
        }
        finally
        {
            _isProcessing = false;
            _processingCancellation?.Dispose();
            _processingCancellation = null;
            CurrentProcessingFileText = string.Empty;
            IsProgressVisible = false;
            progressWindow.Close();
        }
    }

    private void AudioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedFile = AudioList.SelectedItem as AudioFileItem;
        OnPropertyChanged(nameof(SelectedFileText));
        OpenSelectedForPlayback(autoPlay: false);
    }

    private void AudioList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedForPlayback(autoPlay: true);
    }

    private void PlaybackSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenSelectedForPlayback(autoPlay: false);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is null)
        {
            StatusText = "請先選取音檔。";
            return;
        }

        if (_player.Source is null)
        {
            OpenSelectedForPlayback(autoPlay: true);
            return;
        }

        _player.Play();
        _playbackTimer.Start();
        StatusText = "播放中。";
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _player.Pause();
        StatusText = "已暫停。";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private void PositionSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingPosition = true;
    }

    private void PositionSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingPosition = false;
        SeekToSlider();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingPosition)
        {
            PlaybackPositionText = FormatTime(TimeSpan.FromSeconds(PositionSlider.Value));
        }
    }

    private void Player_MediaOpened(object? sender, EventArgs e)
    {
        var duration = _player.NaturalDuration.HasTimeSpan
            ? _player.NaturalDuration.TimeSpan
            : TimeSpan.Zero;

        PositionSlider.Maximum = Math.Max(1, duration.TotalSeconds);
        PlaybackDurationText = FormatTime(duration);

        if (_selectedFile is not null && _selectedFile.Duration is null && PlaybackSourceIndex == 0)
        {
            _selectedFile.Duration = duration;
        }
    }

    private void Player_MediaEnded(object? sender, EventArgs e)
    {
        _player.Stop();
        _playbackTimer.Stop();
        PositionSlider.Value = 0;
        PlaybackPositionText = "00:00";
        StatusText = "播放結束。";
    }

    private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        StatusText = $"播放失敗：{e.ErrorException.Message}";
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDraggingPosition)
        {
            return;
        }

        PositionSlider.Value = Math.Min(PositionSlider.Maximum, _player.Position.TotalSeconds);
        PlaybackPositionText = FormatTime(_player.Position);
    }

    private void OpenSelectedForPlayback(bool autoPlay)
    {
        if (_selectedFile is null)
        {
            return;
        }

        var path = GetPlaybackPath(_selectedFile);
        if (path is null)
        {
            StatusText = "這個音檔還沒有處理後版本。";
            return;
        }

        _player.Open(new Uri(path));
        PositionSlider.Value = 0;
        PlaybackPositionText = "00:00";
        StatusText = autoPlay ? $"播放：{Path.GetFileName(path)}" : $"已載入：{Path.GetFileName(path)}";

        if (autoPlay)
        {
            _player.Play();
            _playbackTimer.Start();
        }
    }

    private string? GetPlaybackPath(AudioFileItem item)
    {
        if (PlaybackSourceIndex == 1)
        {
            return item.HasProcessedFile ? item.ProcessedPath : null;
        }

        return item.FilePath;
    }

    private void SeekToSlider()
    {
        if (_player.Source is null)
        {
            return;
        }

        _player.Position = TimeSpan.FromSeconds(PositionSlider.Value);
        PlaybackPositionText = FormatTime(_player.Position);
    }

    private void StopPlayback()
    {
        _player.Stop();
        _playbackTimer.Stop();
        PositionSlider.Value = 0;
        PlaybackPositionText = "00:00";
        StatusText = "已停止。";
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
