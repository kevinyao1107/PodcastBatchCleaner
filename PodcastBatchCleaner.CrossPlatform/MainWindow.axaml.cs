using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PodcastBatchCleaner.CrossPlatform.Services;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;

namespace PodcastBatchCleaner.CrossPlatform;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FfmpegAudioProcessor _processor = new();
    private readonly FfplayAudioPlayer _player = new();
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _processingCancellation;
    private AudioFileItem? _selectedFile;
    private string? _currentFolder;
    private string? _outputFolder;
    private string? _aiAudioFolder;
    private string? _ffmpegPath;
    private string? _ffplayPath;
    private bool _isDraggingPosition;
    private bool _isProcessing;
    private double _silenceSeconds = 0.1;
    private double _silenceThresholdDb = -35;
    private bool _enableDenoise = true;
    private bool _useAiProcessedAudio;
    private bool _reduceRoomTone;
    private bool _enhanceVoiceEq;
    private bool _normalizeLoudness = true;
    private bool _enableLimiter = true;
    private double _volumeGainDb;
    private int _playbackSourceIndex;
    private string _statusText = "請先選取資料夾。";
    private string _playbackPositionText = "00:00";
    private string _playbackDurationText = "00:00";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _ffmpegPath = FindTool("ffmpeg");
        _ffplayPath = FindTool("ffplay");
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        StatusText = _ffmpegPath is null
            ? "找不到 FFmpeg。請指定 ffmpeg 後再輸出。"
            : "已找到 FFmpeg，可以選取資料夾開始。";
    }

    public ObservableCollection<AudioFileItem> AudioFiles { get; } = [];

    public string CurrentFolderText => _currentFolder is null
        ? "尚未選取資料夾"
        : $"目前資料夾：{_currentFolder}";

    public string OutputFolderText => _outputFolder is null
        ? "輸出資料夾：尚未設定，預設會建立在來源資料夾下的 processed"
        : $"輸出資料夾：{_outputFolder}";

    public string AiAudioFolderText => _aiAudioFolder is null
        ? "AI 音檔資料夾：尚未設定"
        : $"AI 音檔資料夾：{_aiAudioFolder}";

    public string FfmpegPathText => _ffmpegPath is null
        ? "FFmpeg：尚未找到，請指定 ffmpeg 或 ffmpeg.exe"
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

    public bool UseAiProcessedAudio
    {
        get => _useAiProcessedAudio;
        set => SetField(ref _useAiProcessedAudio, value);
    }

    public bool ReduceRoomTone
    {
        get => _reduceRoomTone;
        set => SetField(ref _reduceRoomTone, value);
    }

    public bool EnhanceVoiceEq
    {
        get => _enhanceVoiceEq;
        set => SetField(ref _enhanceVoiceEq, value);
    }

    public bool NormalizeLoudness
    {
        get => _normalizeLoudness;
        set => SetField(ref _normalizeLoudness, value);
    }

    public bool EnableLimiter
    {
        get => _enableLimiter;
        set => SetField(ref _enableLimiter, value);
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

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _processingCancellation?.Cancel();
        _playbackTimer.Stop();
        _player.Dispose();
        base.OnClosing(e);
    }

    private async void SelectFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "選取要處理音檔的資料夾",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is not { Length: > 0 } path)
        {
            return;
        }

        _currentFolder = path;
        _outputFolder ??= Path.Combine(path, "processed");
        OnPropertyChanged(nameof(CurrentFolderText));
        OnPropertyChanged(nameof(OutputFolderText));

        await LoadAudioFilesAsync(path);
    }

    private async void ChooseOutputFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "選取處理後音檔輸出資料夾",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is not { Length: > 0 } path)
        {
            return;
        }

        _outputFolder = path;
        OnPropertyChanged(nameof(OutputFolderText));
        StatusText = "已設定輸出資料夾。";
    }

    private async void ChooseAiAudioFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "選取 AI 處理後音檔資料夾",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is not { Length: > 0 } path)
        {
            return;
        }

        _aiAudioFolder = path;
        UseAiProcessedAudio = true;
        OnPropertyChanged(nameof(AiAudioFolderText));
        StatusText = "已設定 AI 音檔資料夾。";
    }

    private async void ChooseFfmpeg_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選取 ffmpeg",
            AllowMultiple = false
        });

        var file = files.FirstOrDefault();
        if (file?.Path.LocalPath is not { Length: > 0 } path)
        {
            return;
        }

        _ffmpegPath = path;
        _ffplayPath = FindSiblingTool(path, "ffplay") ?? _ffplayPath;
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

    private async void ProcessSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = AudioList.SelectedItem as AudioFileItem;
        await ProcessItemsAsync(selected is null ? [] : [selected]);
    }

    private async void ProcessAll_Click(object? sender, RoutedEventArgs e)
    {
        await ProcessItemsAsync(AudioFiles.ToList());
    }

    private void CancelProcessing_Click(object? sender, RoutedEventArgs e)
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
            StatusText = "請先指定 ffmpeg。";
            return;
        }

        _outputFolder ??= _currentFolder is null
            ? Path.Combine(AppContext.BaseDirectory, "processed")
            : Path.Combine(_currentFolder, "processed");

        OnPropertyChanged(nameof(OutputFolderText));
        Directory.CreateDirectory(_outputFolder);

        _isProcessing = true;
        _processingCancellation = new CancellationTokenSource();

        var options = new AudioProcessingOptions(
            SilenceSeconds,
            SilenceThresholdDb,
            EnableDenoise,
            ReduceRoomTone,
            false,
            EnhanceVoiceEq,
            NormalizeLoudness,
            EnableLimiter,
            VolumeGainDb,
            FfmpegAudioProcessor.KeepOriginalChannelMode,
            0,
            _outputFolder);

        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                _processingCancellation.Token.ThrowIfCancellationRequested();

                item.IsProcessing = true;
                item.Status = $"處理中 {index + 1}/{items.Count}";
                StatusText = $"處理中：{item.FileName}";

                var processingInputPath = GetProcessingInputPath(item);
                if (!string.Equals(processingInputPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    item.Status = "使用 AI 檔";
                    StatusText = $"使用 AI 音檔：{item.FileName}";
                }
                else if (UseAiProcessedAudio)
                {
                    item.Status = "未找到 AI 檔";
                    StatusText = $"找不到 AI 音檔，使用原檔：{item.FileName}";
                }

                var outputPath = FfmpegAudioProcessor.MakeOutputPath(
                    item.FilePath,
                    options.OutputFolder,
                    item.CustomOutputFileName);
                await _processor.ProcessAsync(
                    ffmpegPath,
                    processingInputPath,
                    outputPath,
                    options,
                    cancellationToken: _processingCancellation.Token);

                item.ProcessedPath = outputPath;
                item.Status = "完成";
                item.IsProcessing = false;
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
        }
    }

    private void AudioList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedFile = AudioList.SelectedItem as AudioFileItem;
        OnPropertyChanged(nameof(SelectedFileText));
    }

    private string GetProcessingInputPath(AudioFileItem item)
    {
        if (!UseAiProcessedAudio)
        {
            return item.FilePath;
        }

        var aiPath = FfmpegAudioProcessor.FindAiProcessedAudio(item.FilePath, _aiAudioFolder);
        return aiPath ?? item.FilePath;
    }

    private void PlaybackSource_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        StopPlayback();
    }

    private async void Play_Click(object? sender, RoutedEventArgs e)
    {
        await PlaySelectedAsync(TimeSpan.Zero);
    }

    private void Pause_Click(object? sender, RoutedEventArgs e)
    {
        _player.PauseOrResume();
        StatusText = "已切換暫停/播放。";
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private void PositionSlider_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _isDraggingPosition = true;
    }

    private async void PositionSlider_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _isDraggingPosition = false;
        if (_player.IsPlaying && _ffplayPath is not null)
        {
            await _player.SeekAsync(_ffplayPath, TimeSpan.FromSeconds(PositionSlider.Value));
        }
    }

    private void PositionSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isDraggingPosition)
        {
            PlaybackPositionText = FormatTime(TimeSpan.FromSeconds(PositionSlider.Value));
        }
    }

    private async Task PlaySelectedAsync(TimeSpan startAt)
    {
        if (_selectedFile is null)
        {
            StatusText = "請先選取音檔。";
            return;
        }

        if (_ffplayPath is null || !File.Exists(_ffplayPath))
        {
            StatusText = "找不到 ffplay，請指定包含 ffplay 的 FFmpeg 資料夾。";
            return;
        }

        var path = GetPlaybackPath(_selectedFile);
        if (path is null)
        {
            StatusText = "這個音檔還沒有處理後版本。";
            return;
        }

        var duration = _selectedFile.Duration ?? await ProbeDurationAsync(path) ?? TimeSpan.Zero;
        PositionSlider.Maximum = Math.Max(1, duration.TotalSeconds);
        PlaybackDurationText = FormatTime(duration);

        await _player.PlayAsync(_ffplayPath, path, startAt);
        _playbackTimer.Start();
        StatusText = $"播放：{Path.GetFileName(path)}";
    }

    private async Task<TimeSpan?> ProbeDurationAsync(string path)
    {
        var ffmpegPath = _ffmpegPath;
        if (ffmpegPath is null)
        {
            return null;
        }

        return await _processor.ProbeDurationAsync(ffmpegPath, path);
    }

    private string? GetPlaybackPath(AudioFileItem item)
    {
        if (PlaybackSourceIndex == 1)
        {
            return item.HasProcessedFile ? item.ProcessedPath : null;
        }

        return item.FilePath;
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDraggingPosition)
        {
            return;
        }

        var position = _player.Position;
        PositionSlider.Value = Math.Min(PositionSlider.Maximum, position.TotalSeconds);
        PlaybackPositionText = FormatTime(position);
    }

    private void StopPlayback()
    {
        _player.Stop();
        _playbackTimer.Stop();
        PositionSlider.Value = 0;
        PlaybackPositionText = "00:00";
        StatusText = "已停止。";
    }

    private static string? FindTool(string baseName)
    {
        var executableName = OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;
        var fromPath = FindInPath(executableName);
        if (fromPath is not null)
        {
            return fromPath;
        }

        var local = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(local))
        {
            return local;
        }

        var localTools = Path.Combine(AppContext.BaseDirectory, "tools", executableName);
        return File.Exists(localTools) ? localTools : null;
    }

    private static string? FindSiblingTool(string selectedToolPath, string siblingBaseName)
    {
        var directory = Path.GetDirectoryName(selectedToolPath);
        if (directory is null)
        {
            return null;
        }

        var executableName = OperatingSystem.IsWindows() ? $"{siblingBaseName}.exe" : siblingBaseName;
        var sibling = Path.Combine(directory, executableName);
        return File.Exists(sibling) ? sibling : null;
    }

    private static string? FindInPath(string fileName)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return paths
            .Select(path => Path.Combine(path, fileName))
            .FirstOrDefault(File.Exists);
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
