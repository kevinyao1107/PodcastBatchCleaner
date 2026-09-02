using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
    private readonly DeepFilterNetAudioProcessor _deepFilterNetProcessor = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _playbackTimer;
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _processingCancellation;
    private AudioFileItem? _selectedFile;
    private AudioFileItem? _draggedAudioFile;
    private System.Windows.Point _dragStartPoint;
    private string? _currentFolder;
    private string? _outputFolder;
    private string? _aiAudioFolder;
    private string? _ffmpegPath;
    private bool _isDraggingPosition;
    private bool _isProcessing;
    private bool _loadedDeepFilterNetPreference;
    private double _silenceSeconds = 0.1;
    private double _silenceThresholdDb = -35;
    private bool _enableDenoise = true;
    private bool _useAiProcessedAudio;
    private bool _enableDeepFilterNet;
    private bool _enableDeepFilterNetPostFilter = true;
    private bool _fallbackToFfmpegWhenAiFails = true;
    private string? _deepFilterNetPath;
    private bool _reduceRoomTone;
    private bool _enhanceVoiceEq;
    private bool _normalizeLoudness = true;
    private bool _enableLimiter = true;
    private bool _mergeSegments;
    private double _mergeGapSeconds = 0.5;
    private string _mergedOutputFileName = "podcast_merged.m4a";
    private string? _introAudioPath;
    private string? _outroAudioPath;
    private string? _coverImagePath;
    private double _introFadeSeconds = 1;
    private double _outroFadeSeconds = 1;
    private string _podcastTitle = string.Empty;
    private string _podcastArtist = string.Empty;
    private string _podcastAlbum = string.Empty;
    private AudioOutputFormat _selectedOutputFormat = FfmpegAudioProcessor.OutputFormats[0];
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

        LoadSettings();
    }

    public ObservableCollection<AudioFileItem> AudioFiles { get; } = [];

    public IReadOnlyList<AudioOutputFormat> OutputFormats => FfmpegAudioProcessor.OutputFormats;

    public string CurrentFolderText => _currentFolder is null
        ? "尚未選取資料夾"
        : $"目前資料夾：{_currentFolder}";

    public string OutputFolderText => _outputFolder is null
        ? "輸出資料夾：尚未設定，預設會建立在來源資料夾下的 processed"
        : $"輸出資料夾：{_outputFolder}";

    public string AiAudioFolderText => _aiAudioFolder is null
        ? "AI 音檔資料夾：尚未設定"
        : $"AI 音檔資料夾：{_aiAudioFolder}";

    public string DeepFilterNetPathText => _deepFilterNetPath is null
        ? "DeepFilterNet：尚未設定 deep-filter.exe"
        : $"DeepFilterNet：{_deepFilterNetPath}";

    public string DeepFilterNetStatusText
    {
        get
        {
            if (!EnableDeepFilterNet)
            {
                return "AI 工具：已停用";
            }

            return string.IsNullOrWhiteSpace(_deepFilterNetPath) || !File.Exists(_deepFilterNetPath)
                ? "AI 工具：未找到"
                : "AI 工具：DeepFilterNet 已就緒";
        }
    }

    public string FfmpegPathText => _ffmpegPath is null
        ? "FFmpeg：尚未找到，請指定 ffmpeg.exe 後再輸出"
        : $"FFmpeg：{_ffmpegPath}";

    public string FileCountText => $"{AudioFiles.Count} 個音檔";

    public string SelectedFileText => _selectedFile is null
        ? "尚未選取音檔"
        : $"目前選取：{_selectedFile.FileName}";

    public string IntroAudioText => _introAudioPath is null
        ? "片頭音檔：未設定"
        : $"片頭音檔：{Path.GetFileName(_introAudioPath)}";

    public string OutroAudioText => _outroAudioPath is null
        ? "片尾音檔：未設定"
        : $"片尾音檔：{Path.GetFileName(_outroAudioPath)}";

    public string CoverImageText => _coverImagePath is null
        ? "封面圖片：未設定"
        : $"封面圖片：{Path.GetFileName(_coverImagePath)}";

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

    public bool UseAiProcessedAudio
    {
        get => _useAiProcessedAudio;
        set => SetField(ref _useAiProcessedAudio, value);
    }

    public bool EnableDeepFilterNet
    {
        get => _enableDeepFilterNet;
        set
        {
            if (SetField(ref _enableDeepFilterNet, value))
            {
                OnPropertyChanged(nameof(DeepFilterNetStatusText));
            }
        }
    }

    public bool EnableDeepFilterNetPostFilter
    {
        get => _enableDeepFilterNetPostFilter;
        set => SetField(ref _enableDeepFilterNetPostFilter, value);
    }

    public bool FallbackToFfmpegWhenAiFails
    {
        get => _fallbackToFfmpegWhenAiFails;
        set => SetField(ref _fallbackToFfmpegWhenAiFails, value);
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

    public bool MergeSegments
    {
        get => _mergeSegments;
        set => SetField(ref _mergeSegments, value);
    }

    public double MergeGapSeconds
    {
        get => _mergeGapSeconds;
        set => SetField(ref _mergeGapSeconds, Math.Clamp(Math.Round(value, 1), 0, 10));
    }

    public string MergedOutputFileName
    {
        get => _mergedOutputFileName;
        set => SetField(ref _mergedOutputFileName, value);
    }

    public double IntroFadeSeconds
    {
        get => _introFadeSeconds;
        set => SetField(ref _introFadeSeconds, Math.Clamp(Math.Round(value, 1), 0, 10));
    }

    public double OutroFadeSeconds
    {
        get => _outroFadeSeconds;
        set => SetField(ref _outroFadeSeconds, Math.Clamp(Math.Round(value, 1), 0, 10));
    }

    public string PodcastTitle
    {
        get => _podcastTitle;
        set => SetField(ref _podcastTitle, value);
    }

    public string PodcastArtist
    {
        get => _podcastArtist;
        set => SetField(ref _podcastArtist, value);
    }

    public string PodcastAlbum
    {
        get => _podcastAlbum;
        set => SetField(ref _podcastAlbum, value);
    }

    public AudioOutputFormat SelectedOutputFormat
    {
        get => _selectedOutputFormat;
        set => SetField(ref _selectedOutputFormat, value);
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

    private void ApplyPodcastPreset_Click(object sender, RoutedEventArgs e)
    {
        ApplyProcessingPreset(
            silenceSeconds: 0.3,
            silenceThresholdDb: -35,
            enableDenoise: true,
            reduceRoomTone: true,
            enhanceVoiceEq: true,
            normalizeLoudness: true,
            enableLimiter: true,
            volumeGainDb: 0,
            "已套用：一般 Podcast。");
    }

    private void ApplyRoomTonePreset_Click(object sender, RoutedEventArgs e)
    {
        ApplyProcessingPreset(
            silenceSeconds: 0.3,
            silenceThresholdDb: -34,
            enableDenoise: true,
            reduceRoomTone: true,
            enhanceVoiceEq: false,
            normalizeLoudness: true,
            enableLimiter: true,
            volumeGainDb: 0,
            "已套用：空間音重。");
    }

    private void ApplyThinVoicePreset_Click(object sender, RoutedEventArgs e)
    {
        ApplyProcessingPreset(
            silenceSeconds: 0.2,
            silenceThresholdDb: -36,
            enableDenoise: true,
            reduceRoomTone: true,
            enhanceVoiceEq: true,
            normalizeLoudness: true,
            enableLimiter: true,
            volumeGainDb: 1.5,
            "已套用：聲音偏薄。");
    }

    private void ApplyTrimOnlyPreset_Click(object sender, RoutedEventArgs e)
    {
        ApplyProcessingPreset(
            silenceSeconds: 0.1,
            silenceThresholdDb: -35,
            enableDenoise: false,
            reduceRoomTone: false,
            enhanceVoiceEq: false,
            normalizeLoudness: false,
            enableLimiter: false,
            volumeGainDb: 0,
            "已套用：只剪輯不後製。");
    }

    private void ApplyProcessingPreset(
        double silenceSeconds,
        double silenceThresholdDb,
        bool enableDenoise,
        bool reduceRoomTone,
        bool enhanceVoiceEq,
        bool normalizeLoudness,
        bool enableLimiter,
        double volumeGainDb,
        string statusText)
    {
        SilenceSeconds = silenceSeconds;
        SilenceThresholdDb = silenceThresholdDb;
        EnableDenoise = enableDenoise;
        ReduceRoomTone = reduceRoomTone;
        EnhanceVoiceEq = enhanceVoiceEq;
        NormalizeLoudness = normalizeLoudness;
        EnableLimiter = enableLimiter;
        VolumeGainDb = volumeGainDb;
        StatusText = statusText;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_ffmpegPath) || !File.Exists(_ffmpegPath))
        {
            _ffmpegPath = FfmpegAudioProcessor.TryFindFfmpeg();
        }

        var hadValidDeepFilterNetPath = !string.IsNullOrWhiteSpace(_deepFilterNetPath) && File.Exists(_deepFilterNetPath);
        if (!hadValidDeepFilterNetPath)
        {
            _deepFilterNetPath = DeepFilterNetAudioProcessor.TryFindDeepFilterNet();
        }

        if (!_loadedDeepFilterNetPreference && !string.IsNullOrWhiteSpace(_deepFilterNetPath) && File.Exists(_deepFilterNetPath))
        {
            EnableDeepFilterNet = true;
        }

        OnPropertyChanged(nameof(FfmpegPathText));
        OnPropertyChanged(nameof(DeepFilterNetPathText));
        OnPropertyChanged(nameof(DeepFilterNetStatusText));

        StatusText = _ffmpegPath is null
            ? "找不到 FFmpeg。播放可使用；輸出前請指定 ffmpeg.exe。"
            : "已找到 FFmpeg，可以選取資料夾開始。";

        if (!string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
        {
            await LoadAudioFilesAsync(_currentFolder);
        }

        await Task.CompletedTask;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveSettings();
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

    private void ChooseAiAudioFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "選取 AI 處理後音檔資料夾",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = _aiAudioFolder ?? _currentFolder ?? string.Empty
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        _aiAudioFolder = dialog.SelectedPath;
        UseAiProcessedAudio = true;
        OnPropertyChanged(nameof(AiAudioFolderText));
        StatusText = "已設定 AI 音檔資料夾。";
    }

    private void ChooseDeepFilterNet_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選取 deep-filter.exe",
            Filter = "DeepFilterNet|deep-filter.exe|執行檔|*.exe|所有檔案|*.*",
            InitialDirectory = _currentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _deepFilterNetPath = dialog.FileName;
        EnableDeepFilterNet = true;
        OnPropertyChanged(nameof(DeepFilterNetPathText));
        OnPropertyChanged(nameof(DeepFilterNetStatusText));
        StatusText = "已設定 DeepFilterNet。";
    }

    private void ClearDeepFilterNet_Click(object sender, RoutedEventArgs e)
    {
        _deepFilterNetPath = null;
        EnableDeepFilterNet = false;
        OnPropertyChanged(nameof(DeepFilterNetPathText));
        OnPropertyChanged(nameof(DeepFilterNetStatusText));
        StatusText = "已清除 DeepFilterNet。";
    }

    private void ChooseIntroAudio_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = ChooseAudioFile("選取片頭音檔");

        if (selectedPath is null)
        {
            return;
        }

        _introAudioPath = selectedPath;
        OnPropertyChanged(nameof(IntroAudioText));
        StatusText = "已設定片頭音檔。";
    }

    private void ChooseOutroAudio_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = ChooseAudioFile("選取片尾音檔");

        if (selectedPath is null)
        {
            return;
        }

        _outroAudioPath = selectedPath;
        OnPropertyChanged(nameof(OutroAudioText));
        StatusText = "已設定片尾音檔。";
    }

    private void ClearIntroAudio_Click(object sender, RoutedEventArgs e)
    {
        _introAudioPath = null;
        OnPropertyChanged(nameof(IntroAudioText));
        StatusText = "已清除片頭音檔。";
    }

    private void ClearOutroAudio_Click(object sender, RoutedEventArgs e)
    {
        _outroAudioPath = null;
        OnPropertyChanged(nameof(OutroAudioText));
        StatusText = "已清除片尾音檔。";
    }

    private void ChooseCoverImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選取封面圖片",
            Filter = "圖片|*.jpg;*.jpeg;*.png;*.webp;*.bmp|所有檔案|*.*",
            InitialDirectory = _currentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _coverImagePath = dialog.FileName;
        OnPropertyChanged(nameof(CoverImageText));
        StatusText = "已設定封面圖片。";
    }

    private void ClearCoverImage_Click(object sender, RoutedEventArgs e)
    {
        _coverImagePath = null;
        OnPropertyChanged(nameof(CoverImageText));
        StatusText = "已清除封面圖片。";
    }

    private string? ChooseAudioFile(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "音檔|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.wma;*.mp4|所有檔案|*.*",
            InitialDirectory = _currentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
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

    private async void CompareSelected_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedAudioFile();
        if (item is null)
        {
            StatusText = "請先選取要比較的音檔。";
            return;
        }

        await ProcessComparisonAsync(item);
    }

    private async void PreviewSelected_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedAudioFile();
        if (item is null)
        {
            StatusText = "請先選取要試聽的音檔。";
            return;
        }

        await ProcessPreviewAsync(item);
    }

    private void MoveSelectedUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedItem(-1);
    }

    private void MoveSelectedDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedItem(1);
    }

    private void CancelProcessing_Click(object sender, RoutedEventArgs e)
    {
        _processingCancellation?.Cancel();
        StatusText = "正在取消處理...";
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _outputFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = _currentFolder is null
                ? Path.Combine(AppContext.BaseDirectory, "processed")
                : Path.Combine(_currentFolder, "processed");
        }

        if (!Directory.Exists(folder))
        {
            StatusText = "輸出資料夾還不存在。";
            return;
        }

        OpenFolder(folder);
    }

    private void OpenSelectedFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedAudioFile();
        if (item is null)
        {
            StatusText = "請先選取音檔。";
            return;
        }

        OpenFileLocation(item.FilePath);
    }

    private void OpenProcessedFile_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedAudioFile();
        if (item is null)
        {
            StatusText = "請先選取音檔。";
            return;
        }

        if (!item.HasProcessedFile || item.ProcessedPath is null)
        {
            StatusText = "這個音檔還沒有處理後版本。";
            return;
        }

        OpenFileLocation(item.ProcessedPath);
    }

    private void MoveSelectedItem(int direction)
    {
        if (AudioList.SelectedItem is not AudioFileItem selected)
        {
            StatusText = "請先選取要移動的音檔。";
            return;
        }

        var currentIndex = AudioFiles.IndexOf(selected);
        var nextIndex = currentIndex + direction;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= AudioFiles.Count)
        {
            return;
        }

        AudioFiles.Move(currentIndex, nextIndex);
        AudioList.SelectedItem = selected;
        AudioList.ScrollIntoView(selected);
        StatusText = "已調整合併順序。";
    }

    private AudioFileItem? GetSelectedAudioFile()
    {
        return AudioList.SelectedItem as AudioFileItem ?? _selectedFile;
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
        var preflightErrors = ValidateProcessingInputs(items, ffmpegPath, _outputFolder);
        if (preflightErrors.Count > 0)
        {
            ShowPreflightErrors(preflightErrors);
            StatusText = "處理前檢查未通過。";
            return;
        }

        Directory.CreateDirectory(_outputFolder);

        _isProcessing = true;
        _processingCancellation = new CancellationTokenSource();
        IsProgressVisible = true;
        ProcessingProgress = 0;
        ProcessingProgressText = "0%";
        CurrentProcessingFileText = string.Empty;
        var summary = new ProcessingRunSummary(items.Count, _outputFolder, MergeSegments);
        var showSummary = false;
        var deepFilterTempDirectory = EnableDeepFilterNet
            ? Path.Combine(_outputFolder, $".deepfilter_stage_{Guid.NewGuid():N}")
            : null;

        var options = new AudioProcessingOptions(
            SilenceSeconds,
            SilenceThresholdDb,
            EnableDenoise,
            ReduceRoomTone,
            EnhanceVoiceEq,
            NormalizeLoudness,
            EnableLimiter,
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
            if (MergeSegments)
            {
                var mergedOutputPath = await ProcessMergedItemsAsync(ffmpegPath, items, options, progressWindow);
                summary.SuccessCount = items.Count;
                summary.OutputPath = mergedOutputPath;
                StatusText = "統整輸出完成。";
                showSummary = true;
                return;
            }

            var total = items.Count;
            for (var index = 0; index < total; index++)
            {
                var itemIndex = index;
                var item = items[index];
                _processingCancellation.Token.ThrowIfCancellationRequested();

                try
                {
                    item.IsProcessing = true;
                    item.Status = $"處理中 {index + 1}/{total}";
                    StatusText = $"處理中：{item.FileName}";

                    CurrentProcessingFileText = item.FileName;
                    progressWindow.SetProgress(item.FileName, ProcessingProgress);

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
                        item.CustomOutputFileName,
                        SelectedOutputFormat.Extension);
                    if (!TryGetTrimRange(item, out var trimStart, out var trimEnd, out var trimError))
                    {
                        item.Status = "剪輯時間錯誤";
                        throw new InvalidOperationException(trimError);
                    }

                    if (item.Duration is null)
                    {
                        item.Duration = await _processor.ProbeDurationAsync(
                            ffmpegPath,
                            processingInputPath,
                            _processingCancellation.Token);
                    }

                    var effectiveDuration = GetEffectiveDuration(item.Duration, trimStart, trimEnd);
                    if (EnableDeepFilterNet)
                    {
                        try
                        {
                            processingInputPath = await ProcessWithDeepFilterNetAsync(
                                ffmpegPath,
                                processingInputPath,
                                item.FileName,
                                deepFilterTempDirectory!,
                                progressWindow,
                                Math.Clamp((itemIndex / (double)total) * 100, 0, 100),
                                Math.Clamp(((itemIndex + 0.45d) / total) * 100, 0, 100),
                                effectiveDuration,
                                trimStart,
                                trimEnd,
                                _processingCancellation.Token);

                            trimStart = null;
                            trimEnd = null;
                            effectiveDuration = await _processor.ProbeDurationAsync(
                                ffmpegPath,
                                processingInputPath,
                                _processingCancellation.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                        {
                            item.Status = "AI 失敗，改用 FFmpeg";
                            StatusText = $"DeepFilterNet 失敗，改用 FFmpeg：{item.FileName}";
                            progressWindow.SetProgress($"AI 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                        }
                    }

                    var progress = new Progress<AudioProcessingProgress>(current =>
                    {
                        var itemPercent = EnableDeepFilterNet
                            ? 0.45d + (current.Percent * 0.55d)
                            : current.Percent;
                        var overallPercent = Math.Clamp(((itemIndex + itemPercent) / total) * 100, 0, 100);
                        ProcessingProgress = overallPercent;
                        ProcessingProgressText = $"{overallPercent:0}%";
                        progressWindow.SetProgress(item.FileName, overallPercent);
                        item.Status = $"{overallPercent:0}%";
                        StatusText = $"處理中：{item.FileName}";
                    });

                    await _processor.ProcessAsync(
                        ffmpegPath,
                        processingInputPath,
                        outputPath,
                        options,
                        effectiveDuration,
                        progress,
                        cancellationToken: _processingCancellation.Token,
                        audioCodec: SelectedOutputFormat.AudioCodec,
                        trimStart: trimStart,
                        trimEnd: trimEnd);

                    item.ProcessedPath = outputPath;
                    item.Status = "完成";
                    item.IsProcessing = false;
                    summary.SuccessCount++;
                    summary.OutputPath ??= outputPath;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    item.IsProcessing = false;
                    item.Status = "失敗";
                    summary.Failures.Add(new ProcessingFailure(item.FileName, ex.Message));
                }
                finally
                {
                    ProcessingProgress = Math.Clamp(((itemIndex + 1d) / total) * 100, 0, 100);
                    ProcessingProgressText = $"{ProcessingProgress:0}%";
                    progressWindow.SetProgress(item.FileName, ProcessingProgress);
                }
            }

            StatusText = summary.Failures.Count == 0
                ? "處理完成。"
                : $"處理完成，{summary.Failures.Count} 個檔案失敗。";
            showSummary = true;
        }
        catch (OperationCanceledException)
        {
            summary.WasCanceled = true;
            foreach (var item in items.Where(item => item.IsProcessing))
            {
                item.IsProcessing = false;
                item.Status = "已取消";
            }

            StatusText = "處理已取消。";
            showSummary = true;
        }
        catch (Exception ex)
        {
            foreach (var item in items.Where(item => item.IsProcessing))
            {
                item.IsProcessing = false;
                item.Status = "失敗";
            }

            if (summary.Failures.Count == 0)
            {
                summary.Failures.Add(new ProcessingFailure(MergeSegments ? "統整輸出" : "批次處理", ex.Message));
            }

            StatusText = $"處理失敗：{ex.Message}";
            showSummary = true;
        }
        finally
        {
            _isProcessing = false;
            _processingCancellation?.Dispose();
            _processingCancellation = null;
            CurrentProcessingFileText = string.Empty;
            IsProgressVisible = false;
            progressWindow.Close();

            if (showSummary)
            {
                ShowProcessingSummary(summary);
            }

            if (deepFilterTempDirectory is not null)
            {
                TryDeleteDirectory(deepFilterTempDirectory);
            }
        }
    }

    private async Task<string> ProcessMergedItemsAsync(
        string ffmpegPath,
        IReadOnlyList<AudioFileItem> items,
        AudioProcessingOptions options,
        ProcessingProgressWindow progressWindow)
    {
        var tempDirectory = Path.Combine(options.OutputFolder, $".merge_stage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var segmentPaths = new List<string>();
        var hasIntro = !string.IsNullOrWhiteSpace(_introAudioPath) && File.Exists(_introAudioPath);
        var hasOutro = !string.IsNullOrWhiteSpace(_outroAudioPath) && File.Exists(_outroAudioPath);
        var totalSteps = items.Count + 1d + (hasIntro ? 1 : 0) + (hasOutro ? 1 : 0);
        var completedSteps = 0d;

        try
        {
            if (hasIntro)
            {
                _processingCancellation?.Token.ThrowIfCancellationRequested();

                var introFileName = Path.GetFileName(_introAudioPath!);
                CurrentProcessingFileText = introFileName;
                StatusText = $"準備片頭：{introFileName}";
                progressWindow.SetProgress(introFileName, ProcessingProgress);

                var introDuration = await _processor.ProbeDurationAsync(
                    ffmpegPath,
                    _introAudioPath!,
                    _processingCancellation?.Token ?? CancellationToken.None);
                var introPath = Path.Combine(tempDirectory, "0000_intro.wav");
                var introProgress = new Progress<AudioProcessingProgress>(current =>
                    UpdateMergeStepProgress(introFileName, completedSteps, current.Percent, totalSteps, progressWindow));

                await _processor.PrepareClipForMergeAsync(
                    ffmpegPath,
                    _introAudioPath!,
                    introPath,
                    IntroFadeSeconds,
                    0,
                    introDuration,
                    introProgress,
                    _processingCancellation?.Token ?? CancellationToken.None);

                segmentPaths.Add(introPath);
                completedSteps++;
                SetOverallMergeProgress(completedSteps, totalSteps);
            }

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                _processingCancellation?.Token.ThrowIfCancellationRequested();

                item.IsProcessing = true;
                item.Status = $"統整處理 {index + 1}/{items.Count}";
                CurrentProcessingFileText = item.FileName;
                StatusText = $"統整處理：{item.FileName}";
                progressWindow.SetProgress(item.FileName, ProcessingProgress);

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

                var duration = await _processor.ProbeDurationAsync(
                    ffmpegPath,
                    processingInputPath,
                    _processingCancellation?.Token ?? CancellationToken.None);
                item.Duration ??= duration;

                if (!TryGetTrimRange(item, out var trimStart, out var trimEnd, out var trimError))
                {
                    item.Status = "剪輯時間錯誤";
                    throw new InvalidOperationException($"{item.FileName}：{trimError}");
                }

                var segmentPath = Path.Combine(tempDirectory, $"{index + 1:0000}.wav");
                var stepStart = completedSteps;
                var effectiveDuration = GetEffectiveDuration(duration, trimStart, trimEnd);

                if (EnableDeepFilterNet)
                {
                    try
                    {
                        processingInputPath = await ProcessWithDeepFilterNetAsync(
                            ffmpegPath,
                            processingInputPath,
                            item.FileName,
                            tempDirectory,
                            progressWindow,
                            Math.Clamp((stepStart / totalSteps) * 100, 0, 100),
                            Math.Clamp(((stepStart + 0.45d) / totalSteps) * 100, 0, 100),
                            effectiveDuration,
                            trimStart,
                            trimEnd,
                            _processingCancellation?.Token ?? CancellationToken.None);

                        trimStart = null;
                        trimEnd = null;
                        effectiveDuration = await _processor.ProbeDurationAsync(
                            ffmpegPath,
                            processingInputPath,
                            _processingCancellation?.Token ?? CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                    {
                        item.Status = "AI 失敗，改用 FFmpeg";
                        StatusText = $"DeepFilterNet 失敗，改用 FFmpeg：{item.FileName}";
                        progressWindow.SetProgress($"AI 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                    }
                }

                var progress = new Progress<AudioProcessingProgress>(current =>
                {
                    var itemPercent = EnableDeepFilterNet
                        ? 0.45d + (current.Percent * 0.55d)
                        : current.Percent;
                    UpdateMergeStepProgress(item.FileName, stepStart, itemPercent, totalSteps, progressWindow);
                    item.Status = $"{ProcessingProgress:0}%";
                });

                await _processor.ProcessAsync(
                    ffmpegPath,
                    processingInputPath,
                    segmentPath,
                    options,
                    effectiveDuration,
                    progress,
                    _processingCancellation?.Token ?? CancellationToken.None,
                    audioCodec: "pcm_s16le",
                    sampleRate: 44100,
                    channels: 2,
                    trimStart: trimStart,
                    trimEnd: trimEnd);

                segmentPaths.Add(segmentPath);
                item.Status = "已加入統整";
                item.IsProcessing = false;
                completedSteps++;
                SetOverallMergeProgress(completedSteps, totalSteps);
            }

            if (hasOutro)
            {
                _processingCancellation?.Token.ThrowIfCancellationRequested();

                var outroFileName = Path.GetFileName(_outroAudioPath!);
                CurrentProcessingFileText = outroFileName;
                StatusText = $"準備片尾：{outroFileName}";
                progressWindow.SetProgress(outroFileName, ProcessingProgress);

                var outroDuration = await _processor.ProbeDurationAsync(
                    ffmpegPath,
                    _outroAudioPath!,
                    _processingCancellation?.Token ?? CancellationToken.None);
                var outroPath = Path.Combine(tempDirectory, "9999_outro.wav");
                var outroStepStart = completedSteps;
                var outroProgress = new Progress<AudioProcessingProgress>(current =>
                    UpdateMergeStepProgress(outroFileName, outroStepStart, current.Percent, totalSteps, progressWindow));

                await _processor.PrepareClipForMergeAsync(
                    ffmpegPath,
                    _outroAudioPath!,
                    outroPath,
                    0,
                    OutroFadeSeconds,
                    outroDuration,
                    outroProgress,
                    _processingCancellation?.Token ?? CancellationToken.None);

                segmentPaths.Add(outroPath);
                completedSteps++;
                SetOverallMergeProgress(completedSteps, totalSteps);
            }

            var mergedOutputPath = FfmpegAudioProcessor.MakeMergedOutputPath(
                options.OutputFolder,
                MergedOutputFileName,
                SelectedOutputFormat.Extension);
            var mergeProgress = new Progress<AudioProcessingProgress>(current =>
            {
                UpdateMergeStepProgress(
                    Path.GetFileName(mergedOutputPath),
                    completedSteps,
                    current.Percent,
                    totalSteps,
                    progressWindow);
                StatusText = $"合併輸出：{Path.GetFileName(mergedOutputPath)}";
            });

            await _processor.MergeAsync(
                ffmpegPath,
                segmentPaths,
                mergedOutputPath,
                MergeGapSeconds,
                mergeProgress,
                _processingCancellation?.Token ?? CancellationToken.None,
                new AudioMetadataOptions(
                    PodcastTitle,
                    PodcastArtist,
                    PodcastAlbum,
                    _coverImagePath),
                SelectedOutputFormat);

            foreach (var item in items)
            {
                item.ProcessedPath = mergedOutputPath;
                item.Status = "已統整";
                item.IsProcessing = false;
            }

            ProcessingProgress = 100;
            ProcessingProgressText = "100%";
            progressWindow.SetProgress(Path.GetFileName(mergedOutputPath), 100);
            return mergedOutputPath;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task ProcessComparisonAsync(AudioFileItem item)
    {
        if (_isProcessing)
        {
            StatusText = "目前已經在處理中。";
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

        var preflightErrors = ValidateProcessingInputs([item], ffmpegPath, _outputFolder);
        if (preflightErrors.Count > 0)
        {
            ShowPreflightErrors(preflightErrors);
            StatusText = "A/B 比較前檢查未通過。";
            return;
        }

        var comparisonFolder = Path.Combine(_outputFolder, "ab_compare");
        Directory.CreateDirectory(comparisonFolder);

        _isProcessing = true;
        _processingCancellation = new CancellationTokenSource();
        IsProgressVisible = true;
        ProcessingProgress = 0;
        ProcessingProgressText = "0%";
        CurrentProcessingFileText = item.FileName;

        var progressWindow = new ProcessingProgressWindow
        {
            Owner = this
        };
        progressWindow.CancelRequested += (_, _) => _processingCancellation?.Cancel();
        progressWindow.Show();

        var outputs = new List<string>();
        var comparisonTempDirectory = EnableDeepFilterNet
            ? Path.Combine(comparisonFolder, $".deepfilter_compare_{Guid.NewGuid():N}")
            : null;

        try
        {
            var processingInputPath = GetProcessingInputPath(item);
            if (item.Duration is null)
            {
                item.Duration = await _processor.ProbeDurationAsync(
                    ffmpegPath,
                    processingInputPath,
                    _processingCancellation.Token);
            }

            if (!TryGetTrimRange(item, out var trimStart, out var trimEnd, out var trimError))
            {
                throw new InvalidOperationException($"{item.FileName}：{trimError}");
            }

            var effectiveDuration = GetEffectiveDuration(item.Duration, trimStart, trimEnd);
            if (EnableDeepFilterNet)
            {
                try
                {
                    processingInputPath = await ProcessWithDeepFilterNetAsync(
                        ffmpegPath,
                        processingInputPath,
                        item.FileName,
                        comparisonTempDirectory!,
                        progressWindow,
                        0,
                        25,
                        effectiveDuration,
                        trimStart,
                        trimEnd,
                        _processingCancellation.Token);

                    trimStart = null;
                    trimEnd = null;
                    effectiveDuration = await _processor.ProbeDurationAsync(
                        ffmpegPath,
                        processingInputPath,
                        _processingCancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                {
                    item.Status = "AI 失敗，改用 FFmpeg";
                    StatusText = $"DeepFilterNet 失敗，A/B 改用 FFmpeg：{item.FileName}";
                    progressWindow.SetProgress($"AI 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                }
            }

            var presets = CreateComparisonPresets();

            for (var index = 0; index < presets.Count; index++)
            {
                _processingCancellation.Token.ThrowIfCancellationRequested();

                var preset = presets[index];
                item.Status = $"比較 {index + 1}/{presets.Count}";
                StatusText = $"A/B 比較：{preset.DisplayName}";
                CurrentProcessingFileText = $"{item.FileName} - {preset.DisplayName}";

                var outputPath = FfmpegAudioProcessor.MakeOutputPath(
                    item.FilePath,
                    comparisonFolder,
                    $"{Path.GetFileNameWithoutExtension(item.FileName)}_{preset.FileSuffix}",
                    SelectedOutputFormat.Extension);
                var itemIndex = index;
                var progress = new Progress<AudioProcessingProgress>(current =>
                {
                    var presetBase = EnableDeepFilterNet ? 0.25d : 0d;
                    var presetRange = EnableDeepFilterNet ? 0.75d : 1d;
                    var overallPercent = Math.Clamp(
                        (presetBase + (((itemIndex + current.Percent) / presets.Count) * presetRange)) * 100,
                        0,
                        100);
                    ProcessingProgress = overallPercent;
                    ProcessingProgressText = $"{overallPercent:0}%";
                    progressWindow.SetProgress($"{item.FileName} - {preset.DisplayName}", overallPercent);
                    item.Status = $"{overallPercent:0}%";
                });

                await _processor.ProcessAsync(
                    ffmpegPath,
                    processingInputPath,
                    outputPath,
                    preset.Options with { OutputFolder = comparisonFolder },
                    effectiveDuration,
                    progress,
                    cancellationToken: _processingCancellation.Token,
                    audioCodec: SelectedOutputFormat.AudioCodec,
                    trimStart: trimStart,
                    trimEnd: trimEnd);

                outputs.Add(outputPath);
                item.ProcessedPath = outputPath;
                var completedBase = EnableDeepFilterNet ? 0.25d : 0d;
                var completedRange = EnableDeepFilterNet ? 0.75d : 1d;
                ProcessingProgress = Math.Clamp(
                    (completedBase + (((index + 1d) / presets.Count) * completedRange)) * 100,
                    0,
                    100);
                ProcessingProgressText = $"{ProcessingProgress:0}%";
            }

            item.Status = "比較完成";
            StatusText = $"A/B 比較完成：{comparisonFolder}";
            ShowComparisonSummary(comparisonFolder, outputs);
        }
        catch (OperationCanceledException)
        {
            item.Status = "已取消";
            StatusText = "A/B 比較已取消。";
        }
        catch (Exception ex)
        {
            item.Status = "比較失敗";
            StatusText = $"A/B 比較失敗：{ex.Message}";
            System.Windows.MessageBox.Show(this, ex.Message, "A/B 比較失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isProcessing = false;
            _processingCancellation?.Dispose();
            _processingCancellation = null;
            CurrentProcessingFileText = string.Empty;
            IsProgressVisible = false;
            progressWindow.Close();

            if (comparisonTempDirectory is not null)
            {
                TryDeleteDirectory(comparisonTempDirectory);
            }
        }
    }

    private async Task ProcessPreviewAsync(AudioFileItem item)
    {
        if (_isProcessing)
        {
            StatusText = "目前已經在處理中。";
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

        var previewFolder = Path.Combine(_outputFolder, "preview");
        var preflightErrors = ValidateProcessingInputs([item], ffmpegPath, previewFolder);
        if (preflightErrors.Count > 0)
        {
            ShowPreflightErrors(preflightErrors);
            StatusText = "試聽前檢查未通過。";
            return;
        }

        Directory.CreateDirectory(previewFolder);

        _isProcessing = true;
        _processingCancellation = new CancellationTokenSource();
        IsProgressVisible = true;
        ProcessingProgress = 0;
        ProcessingProgressText = "0%";
        CurrentProcessingFileText = item.FileName;

        var progressWindow = new ProcessingProgressWindow
        {
            Owner = this
        };
        progressWindow.CancelRequested += (_, _) => _processingCancellation?.Cancel();
        progressWindow.Show();

        var previewTempDirectory = EnableDeepFilterNet
            ? Path.Combine(previewFolder, $".preview_stage_{Guid.NewGuid():N}")
            : null;

        try
        {
            item.IsProcessing = true;
            item.Status = "產生試聽";
            StatusText = $"產生 30 秒試聽：{item.FileName}";

            var processingInputPath = GetProcessingInputPath(item);
            if (item.Duration is null)
            {
                item.Duration = await _processor.ProbeDurationAsync(
                    ffmpegPath,
                    processingInputPath,
                    _processingCancellation.Token);
            }

            if (!TryGetTrimRange(item, out var trimStart, out var trimEnd, out var trimError))
            {
                item.Status = "剪輯時間錯誤";
                throw new InvalidOperationException(trimError);
            }

            var previewStart = trimStart ?? TimeSpan.Zero;
            var previewEnd = previewStart + PreviewDuration;
            if (trimEnd is not null && trimEnd < previewEnd)
            {
                previewEnd = trimEnd.Value;
            }

            if (item.Duration is not null && item.Duration < previewEnd)
            {
                previewEnd = item.Duration.Value;
            }

            trimEnd = previewEnd > previewStart ? previewEnd : trimEnd;
            var effectiveDuration = GetEffectiveDuration(item.Duration, trimStart, trimEnd);

            if (EnableDeepFilterNet)
            {
                try
                {
                    processingInputPath = await ProcessWithDeepFilterNetAsync(
                        ffmpegPath,
                        processingInputPath,
                        item.FileName,
                        previewTempDirectory!,
                        progressWindow,
                        0,
                        45,
                        effectiveDuration,
                        trimStart,
                        trimEnd,
                        _processingCancellation.Token);

                    trimStart = null;
                    trimEnd = null;
                    effectiveDuration = await _processor.ProbeDurationAsync(
                        ffmpegPath,
                        processingInputPath,
                        _processingCancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                {
                    item.Status = "AI 失敗，改用 FFmpeg";
                    StatusText = $"DeepFilterNet 失敗，試聽改用 FFmpeg：{item.FileName}";
                    progressWindow.SetProgress($"AI 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                }
            }

            var options = new AudioProcessingOptions(
                SilenceSeconds,
                SilenceThresholdDb,
                EnableDenoise,
                ReduceRoomTone,
                EnhanceVoiceEq,
                NormalizeLoudness,
                EnableLimiter,
                VolumeGainDb,
                previewFolder);

            var outputPath = FfmpegAudioProcessor.MakeOutputPath(
                item.FilePath,
                previewFolder,
                $"{Path.GetFileNameWithoutExtension(item.FileName)}_preview",
                SelectedOutputFormat.Extension);

            var progress = new Progress<AudioProcessingProgress>(current =>
            {
                var percent = EnableDeepFilterNet ? 45 + (current.Percent * 55) : current.Percent * 100;
                ProcessingProgress = Math.Clamp(percent, 0, 100);
                ProcessingProgressText = $"{ProcessingProgress:0}%";
                progressWindow.SetProgress($"FFmpeg 試聽輸出：{item.FileName}", ProcessingProgress);
                item.Status = $"{ProcessingProgress:0}%";
                StatusText = $"FFmpeg 試聽輸出：{item.FileName}";
            });

            await _processor.ProcessAsync(
                ffmpegPath,
                processingInputPath,
                outputPath,
                options,
                effectiveDuration,
                progress,
                cancellationToken: _processingCancellation.Token,
                audioCodec: SelectedOutputFormat.AudioCodec,
                trimStart: trimStart,
                trimEnd: trimEnd);

            item.ProcessedPath = outputPath;
            item.Status = "試聽完成";
            ProcessingProgress = 100;
            ProcessingProgressText = "100%";
            progressWindow.SetProgress($"試聽完成：{item.FileName}", 100);
            StatusText = $"試聽完成：{Path.GetFileName(outputPath)}";

            _selectedFile = item;
            PlaybackSourceIndex = 1;
            OpenSelectedForPlayback(autoPlay: true);
        }
        catch (OperationCanceledException)
        {
            item.Status = "已取消";
            StatusText = "試聽已取消。";
        }
        catch (Exception ex)
        {
            item.Status = "試聽失敗";
            StatusText = $"試聽失敗：{ex.Message}";
            System.Windows.MessageBox.Show(this, ex.Message, "試聽失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            item.IsProcessing = false;
            _isProcessing = false;
            _processingCancellation?.Dispose();
            _processingCancellation = null;
            CurrentProcessingFileText = string.Empty;
            IsProgressVisible = false;
            progressWindow.Close();

            if (previewTempDirectory is not null)
            {
                TryDeleteDirectory(previewTempDirectory);
            }
        }
    }

    private void UpdateMergeStepProgress(
        string displayName,
        double completedSteps,
        double stepPercent,
        double totalSteps,
        ProcessingProgressWindow progressWindow)
    {
        var overallPercent = Math.Clamp(((completedSteps + stepPercent) / totalSteps) * 100, 0, 100);
        ProcessingProgress = overallPercent;
        ProcessingProgressText = $"{overallPercent:0}%";
        progressWindow.SetProgress(displayName, overallPercent);
    }

    private void SetOverallMergeProgress(double completedSteps, double totalSteps)
    {
        ProcessingProgress = Math.Clamp((completedSteps / totalSteps) * 100, 0, 100);
        ProcessingProgressText = $"{ProcessingProgress:0}%";
    }

    private static bool TryGetTrimRange(
        AudioFileItem item,
        out TimeSpan? trimStart,
        out TimeSpan? trimEnd,
        out string? error)
    {
        trimStart = null;
        trimEnd = null;
        error = null;

        if (!TryParseOptionalTime(item.TrimStartText, out trimStart))
        {
            error = "開始時間格式不正確，請輸入秒數或 mm:ss。";
            return false;
        }

        if (!TryParseOptionalTime(item.TrimEndText, out trimEnd))
        {
            error = "結束時間格式不正確，請輸入秒數或 mm:ss。";
            return false;
        }

        if (trimStart is { TotalSeconds: < 0 } || trimEnd is { TotalSeconds: < 0 })
        {
            error = "剪輯時間不能小於 0。";
            return false;
        }

        if (trimStart is not null && trimEnd is not null && trimEnd <= trimStart)
        {
            error = "結束時間必須大於開始時間。";
            return false;
        }

        if (item.Duration is not null && trimStart is not null && trimStart >= item.Duration)
        {
            error = "開始時間不能大於或等於音檔長度。";
            return false;
        }

        if (item.Duration is not null && trimEnd is not null && trimEnd > item.Duration)
        {
            error = "結束時間不能大於音檔長度。";
            return false;
        }

        return true;
    }

    private static bool TryParseOptionalTime(string? text, out TimeSpan? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        var parts = trimmed.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds)
            && !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.CurrentCulture, out parsedSeconds))
        {
            return false;
        }

        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        var hours = 0;
        if (parts.Length == 3
            && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
        {
            return false;
        }

        value = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(parsedSeconds);
        return true;
    }

    private static TimeSpan? GetEffectiveDuration(TimeSpan? duration, TimeSpan? trimStart, TimeSpan? trimEnd)
    {
        var start = trimStart ?? TimeSpan.Zero;

        if (trimEnd is not null)
        {
            return trimEnd > start ? trimEnd - start : duration;
        }

        if (duration is not null && start > TimeSpan.Zero && duration > start)
        {
            return duration - start;
        }

        return duration;
    }

    private async Task<string> ProcessWithDeepFilterNetAsync(
        string ffmpegPath,
        string inputPath,
        string displayName,
        string tempDirectory,
        ProcessingProgressWindow progressWindow,
        double startOverallPercent,
        double completeOverallPercent,
        TimeSpan? duration,
        TimeSpan? trimStart,
        TimeSpan? trimEnd,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_deepFilterNetPath) || !File.Exists(_deepFilterNetPath))
        {
            throw new InvalidOperationException("已勾選 DeepFilterNet AI 降噪，但找不到 deep-filter.exe。");
        }

        var inputDirectory = Path.Combine(tempDirectory, "dfn_input");
        var outputDirectory = Path.Combine(tempDirectory, "dfn_output");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var baseName = Path.GetFileNameWithoutExtension(displayName);
        var preparedInputPath = Path.Combine(inputDirectory, $"{baseName}_{Guid.NewGuid():N}.wav");
        var totalRange = Math.Max(completeOverallPercent - startOverallPercent, 0);

        StatusText = $"DeepFilterNet 準備：{displayName}";
        CurrentProcessingFileText = displayName;

        var prepareProgress = new Progress<AudioProcessingProgress>(current =>
        {
            var overallPercent = Math.Clamp(startOverallPercent + (totalRange * 0.35d * current.Percent), 0, 100);
            ProcessingProgress = overallPercent;
            ProcessingProgressText = $"{overallPercent:0}%";
            progressWindow.SetProgress($"準備 AI 音檔：{displayName}", overallPercent);
        });

        await _processor.PrepareDeepFilterNetInputAsync(
            ffmpegPath,
            inputPath,
            preparedInputPath,
            duration,
            prepareProgress,
            cancellationToken,
            trimStart,
            trimEnd);

        StatusText = $"DeepFilterNet AI 降噪：{displayName}";
        progressWindow.SetProgress($"AI 降噪：{displayName}", Math.Clamp(startOverallPercent + (totalRange * 0.35d), 0, 100));

        var deepFilteredPath = await _deepFilterNetProcessor.ProcessAsync(
            _deepFilterNetPath,
            preparedInputPath,
            outputDirectory,
            EnableDeepFilterNetPostFilter,
            cancellationToken);

        ProcessingProgress = Math.Clamp(completeOverallPercent, 0, 100);
        ProcessingProgressText = $"{ProcessingProgress:0}%";
        progressWindow.SetProgress($"AI 降噪完成：{displayName}", ProcessingProgress);
        return deepFilteredPath;
    }

    private IReadOnlyList<ComparisonPreset> CreateComparisonPresets()
    {
        return
        [
            new(
                "一般 Podcast",
                "ab_podcast",
                CreateProcessingOptions(
                    silenceSeconds: 0.3,
                    silenceThresholdDb: -35,
                    enableDenoise: true,
                    reduceRoomTone: true,
                    enhanceVoiceEq: true,
                    normalizeLoudness: true,
                    enableLimiter: true,
                    volumeGainDb: 0)),
            new(
                "空間音重",
                "ab_room",
                CreateProcessingOptions(
                    silenceSeconds: 0.3,
                    silenceThresholdDb: -34,
                    enableDenoise: true,
                    reduceRoomTone: true,
                    enhanceVoiceEq: false,
                    normalizeLoudness: true,
                    enableLimiter: true,
                    volumeGainDb: 0)),
            new(
                "聲音偏薄",
                "ab_thin_voice",
                CreateProcessingOptions(
                    silenceSeconds: 0.2,
                    silenceThresholdDb: -36,
                    enableDenoise: true,
                    reduceRoomTone: true,
                    enhanceVoiceEq: true,
                    normalizeLoudness: true,
                    enableLimiter: true,
                    volumeGainDb: 1.5)),
            new(
                "只剪輯",
                "ab_trim_only",
                CreateProcessingOptions(
                    silenceSeconds: 0.1,
                    silenceThresholdDb: -35,
                    enableDenoise: false,
                    reduceRoomTone: false,
                    enhanceVoiceEq: false,
                    normalizeLoudness: false,
                    enableLimiter: false,
                    volumeGainDb: 0))
        ];
    }

    private AudioProcessingOptions CreateProcessingOptions(
        double silenceSeconds,
        double silenceThresholdDb,
        bool enableDenoise,
        bool reduceRoomTone,
        bool enhanceVoiceEq,
        bool normalizeLoudness,
        bool enableLimiter,
        double volumeGainDb)
    {
        return new AudioProcessingOptions(
            silenceSeconds,
            silenceThresholdDb,
            enableDenoise,
            reduceRoomTone,
            enhanceVoiceEq,
            normalizeLoudness,
            enableLimiter,
            volumeGainDb,
            _outputFolder ?? string.Empty);
    }

    private void ShowComparisonSummary(string comparisonFolder, IReadOnlyList<string> outputs)
    {
        var message = new StringBuilder()
            .AppendLine("A/B 比較完成。")
            .AppendLine()
            .AppendLine($"輸出資料夾：{comparisonFolder}")
            .AppendLine()
            .AppendLine("輸出檔案：");

        foreach (var output in outputs)
        {
            message.AppendLine($"- {Path.GetFileName(output)}");
        }

        System.Windows.MessageBox.Show(this, message.ToString(), "A/B 比較", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private List<string> ValidateProcessingInputs(
        IReadOnlyList<AudioFileItem> items,
        string ffmpegPath,
        string outputFolder)
    {
        var errors = new List<string>();

        if (!File.Exists(ffmpegPath))
        {
            errors.Add("找不到 FFmpeg，請重新指定 ffmpeg.exe。");
        }

        try
        {
            Directory.CreateDirectory(outputFolder);
            var testFile = Path.Combine(outputFolder, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, string.Empty);
            File.Delete(testFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errors.Add($"輸出資料夾無法寫入：{ex.Message}");
        }

        if (UseAiProcessedAudio && (string.IsNullOrWhiteSpace(_aiAudioFolder) || !Directory.Exists(_aiAudioFolder)))
        {
            errors.Add("已勾選使用 AI 處理後音檔，但 AI 音檔資料夾不存在。");
        }

        if (EnableDeepFilterNet && (string.IsNullOrWhiteSpace(_deepFilterNetPath) || !File.Exists(_deepFilterNetPath)))
        {
            errors.Add("已勾選 DeepFilterNet AI 降噪，但找不到 deep-filter.exe。");
        }

        if (!string.IsNullOrWhiteSpace(_introAudioPath) && !File.Exists(_introAudioPath))
        {
            errors.Add("片頭音檔不存在，請重新選擇或清除片頭。");
        }

        if (!string.IsNullOrWhiteSpace(_outroAudioPath) && !File.Exists(_outroAudioPath))
        {
            errors.Add("片尾音檔不存在，請重新選擇或清除片尾。");
        }

        if (!string.IsNullOrWhiteSpace(_coverImagePath) && !File.Exists(_coverImagePath))
        {
            errors.Add("封面圖片不存在，請重新選擇或清除封面。");
        }

        foreach (var item in items)
        {
            if (!File.Exists(item.FilePath))
            {
                errors.Add($"{item.FileName}：找不到原始音檔。");
                continue;
            }

            if (!TryGetTrimRange(item, out _, out _, out var trimError))
            {
                errors.Add($"{item.FileName}：{trimError}");
            }
        }

        return errors;
    }

    private void ShowPreflightErrors(IReadOnlyList<string> errors)
    {
        var message = new StringBuilder()
            .AppendLine("處理前檢查發現以下問題：")
            .AppendLine();

        foreach (var error in errors.Take(10))
        {
            message.AppendLine($"- {error}");
        }

        if (errors.Count > 10)
        {
            message.AppendLine($"...另有 {errors.Count - 10} 個問題");
        }

        System.Windows.MessageBox.Show(
            this,
            message.ToString(),
            "處理前檢查",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ShowProcessingSummary(ProcessingRunSummary summary)
    {
        var failedCount = summary.Failures.Count;
        var canceledCount = summary.WasCanceled
            ? Math.Max(summary.TotalCount - summary.SuccessCount - failedCount, 0)
            : 0;

        var message = new StringBuilder()
            .AppendLine(summary.WasCanceled ? "處理已取消。" : "處理完成。")
            .AppendLine()
            .AppendLine($"成功：{summary.SuccessCount}")
            .AppendLine($"失敗：{failedCount}")
            .AppendLine($"取消：{canceledCount}")
            .AppendLine($"輸出資料夾：{summary.OutputFolder}");

        if (!string.IsNullOrWhiteSpace(summary.OutputPath))
        {
            message.AppendLine($"最後輸出：{summary.OutputPath}");
        }

        if (failedCount > 0)
        {
            message.AppendLine();
            message.AppendLine("失敗檔案：");
            foreach (var failure in summary.Failures.Take(8))
            {
                message.AppendLine($"- {failure.FileName}: {failure.ErrorMessage}");
            }

            if (failedCount > 8)
            {
                message.AppendLine($"...另有 {failedCount - 8} 個失敗檔案");
            }
        }

        var icon = failedCount > 0
            ? MessageBoxImage.Warning
            : summary.WasCanceled
                ? MessageBoxImage.Information
                : MessageBoxImage.Information;

        System.Windows.MessageBox.Show(this, message.ToString(), "處理摘要", MessageBoxButton.OK, icon);
    }

    private void AudioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedFile = AudioList.SelectedItem as AudioFileItem;
        OnPropertyChanged(nameof(SelectedFileText));
        OpenSelectedForPlayback(autoPlay: false);
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

    private void LoadSettings()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            if (!File.Exists(settingsPath))
            {
                return;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(json);
            _loadedDeepFilterNetPreference = document.RootElement.TryGetProperty(nameof(AppSettings.EnableDeepFilterNet), out _);

            _currentFolder = settings.CurrentFolder;
            _outputFolder = settings.OutputFolder;
            _aiAudioFolder = settings.AiAudioFolder;
            _ffmpegPath = settings.FfmpegPath;
            _introAudioPath = settings.IntroAudioPath;
            _outroAudioPath = settings.OutroAudioPath;
            _coverImagePath = settings.CoverImagePath;
            _deepFilterNetPath = settings.DeepFilterNetPath;

            SilenceSeconds = settings.SilenceSeconds;
            SilenceThresholdDb = settings.SilenceThresholdDb;
            EnableDenoise = settings.EnableDenoise;
            UseAiProcessedAudio = settings.UseAiProcessedAudio;
            EnableDeepFilterNet = settings.EnableDeepFilterNet;
            EnableDeepFilterNetPostFilter = settings.EnableDeepFilterNetPostFilter;
            FallbackToFfmpegWhenAiFails = settings.FallbackToFfmpegWhenAiFails;
            ReduceRoomTone = settings.ReduceRoomTone;
            EnhanceVoiceEq = settings.EnhanceVoiceEq;
            NormalizeLoudness = settings.NormalizeLoudness;
            EnableLimiter = settings.EnableLimiter;
            MergeSegments = settings.MergeSegments;
            MergeGapSeconds = settings.MergeGapSeconds;
            MergedOutputFileName = string.IsNullOrWhiteSpace(settings.MergedOutputFileName)
                ? "podcast_merged.m4a"
                : settings.MergedOutputFileName;
            IntroFadeSeconds = settings.IntroFadeSeconds;
            OutroFadeSeconds = settings.OutroFadeSeconds;
            PodcastTitle = settings.PodcastTitle ?? string.Empty;
            PodcastArtist = settings.PodcastArtist ?? string.Empty;
            PodcastAlbum = settings.PodcastAlbum ?? string.Empty;
            VolumeGainDb = settings.VolumeGainDb;

            SelectedOutputFormat = FfmpegAudioProcessor.OutputFormats.FirstOrDefault(format =>
                    string.Equals(format.Extension, settings.OutputFormatExtension, StringComparison.OrdinalIgnoreCase))
                ?? FfmpegAudioProcessor.OutputFormats[0];

            OnPropertyChanged(nameof(CurrentFolderText));
            OnPropertyChanged(nameof(OutputFolderText));
            OnPropertyChanged(nameof(AiAudioFolderText));
            OnPropertyChanged(nameof(FfmpegPathText));
            OnPropertyChanged(nameof(IntroAudioText));
            OnPropertyChanged(nameof(OutroAudioText));
            OnPropertyChanged(nameof(CoverImageText));
            OnPropertyChanged(nameof(DeepFilterNetPathText));
            OnPropertyChanged(nameof(DeepFilterNetStatusText));
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

            var settings = new AppSettings
            {
                CurrentFolder = _currentFolder,
                OutputFolder = _outputFolder,
                AiAudioFolder = _aiAudioFolder,
                FfmpegPath = _ffmpegPath,
                IntroAudioPath = _introAudioPath,
                OutroAudioPath = _outroAudioPath,
                CoverImagePath = _coverImagePath,
                DeepFilterNetPath = _deepFilterNetPath,
                SilenceSeconds = SilenceSeconds,
                SilenceThresholdDb = SilenceThresholdDb,
                EnableDenoise = EnableDenoise,
                UseAiProcessedAudio = UseAiProcessedAudio,
                EnableDeepFilterNet = EnableDeepFilterNet,
                EnableDeepFilterNetPostFilter = EnableDeepFilterNetPostFilter,
                FallbackToFfmpegWhenAiFails = FallbackToFfmpegWhenAiFails,
                ReduceRoomTone = ReduceRoomTone,
                EnhanceVoiceEq = EnhanceVoiceEq,
                NormalizeLoudness = NormalizeLoudness,
                EnableLimiter = EnableLimiter,
                MergeSegments = MergeSegments,
                MergeGapSeconds = MergeGapSeconds,
                MergedOutputFileName = MergedOutputFileName,
                IntroFadeSeconds = IntroFadeSeconds,
                OutroFadeSeconds = OutroFadeSeconds,
                PodcastTitle = PodcastTitle,
                PodcastArtist = PodcastArtist,
                PodcastAlbum = PodcastAlbum,
                OutputFormatExtension = SelectedOutputFormat.Extension,
                VolumeGainDb = VolumeGainDb
            };

            var json = JsonSerializer.Serialize(settings, SettingsJsonOptions);
            File.WriteAllText(settingsPath, json);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PodcastBatchCleaner",
            "settings.json");
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void AudioList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedForPlayback(autoPlay: true);
    }

    private void AudioList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _draggedAudioFile = null;

        if (FindAncestor<System.Windows.Controls.TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var listViewItem = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject);
        _draggedAudioFile = listViewItem?.DataContext as AudioFileItem;
    }

    private void AudioList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedAudioFile is null)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(AudioList, _draggedAudioFile, System.Windows.DragDropEffects.Move);
    }

    private void AudioList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(AudioFileItem)))
        {
            return;
        }

        var sourceItem = e.Data.GetData(typeof(AudioFileItem)) as AudioFileItem;
        var targetItem = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as AudioFileItem;
        if (sourceItem is null || targetItem is null || ReferenceEquals(sourceItem, targetItem))
        {
            return;
        }

        var sourceIndex = AudioFiles.IndexOf(sourceItem);
        var targetIndex = AudioFiles.IndexOf(targetItem);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        AudioFiles.Move(sourceIndex, targetIndex);
        AudioList.SelectedItem = sourceItem;
        AudioList.ScrollIntoView(sourceItem);
        StatusText = "已拖曳調整合併順序。";
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

    private void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { folder },
                UseShellExecute = true
            });
            StatusText = $"已開啟資料夾：{folder}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            StatusText = $"無法開啟資料夾：{ex.Message}";
        }
    }

    private void OpenFileLocation(string filePath)
    {
        if (!File.Exists(filePath))
        {
            StatusText = "找不到檔案。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { "/select,", filePath },
                UseShellExecute = true
            });
            StatusText = $"已開啟檔案位置：{Path.GetFileName(filePath)}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            StatusText = $"無法開啟檔案位置：{ex.Message}";
        }
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
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

    private sealed class ProcessingRunSummary(int totalCount, string outputFolder, bool isMergedOutput)
    {
        public int TotalCount { get; } = totalCount;

        public string OutputFolder { get; } = outputFolder;

        public bool IsMergedOutput { get; } = isMergedOutput;

        public int SuccessCount { get; set; }

        public string? OutputPath { get; set; }

        public bool WasCanceled { get; set; }

        public List<ProcessingFailure> Failures { get; } = [];
    }

    private sealed record ProcessingFailure(string FileName, string ErrorMessage);

    private sealed record ComparisonPreset(
        string DisplayName,
        string FileSuffix,
        AudioProcessingOptions Options);

    private sealed class AppSettings
    {
        public string? CurrentFolder { get; set; }

        public string? OutputFolder { get; set; }

        public string? AiAudioFolder { get; set; }

        public string? FfmpegPath { get; set; }

        public string? IntroAudioPath { get; set; }

        public string? OutroAudioPath { get; set; }

        public string? CoverImagePath { get; set; }

        public string? DeepFilterNetPath { get; set; }

        public double SilenceSeconds { get; set; } = 0.1;

        public double SilenceThresholdDb { get; set; } = -35;

        public bool EnableDenoise { get; set; } = true;

        public bool UseAiProcessedAudio { get; set; }

        public bool EnableDeepFilterNet { get; set; }

        public bool EnableDeepFilterNetPostFilter { get; set; } = true;

        public bool FallbackToFfmpegWhenAiFails { get; set; } = true;

        public bool ReduceRoomTone { get; set; }

        public bool EnhanceVoiceEq { get; set; }

        public bool NormalizeLoudness { get; set; } = true;

        public bool EnableLimiter { get; set; } = true;

        public bool MergeSegments { get; set; }

        public double MergeGapSeconds { get; set; } = 0.5;

        public string MergedOutputFileName { get; set; } = "podcast_merged.m4a";

        public double IntroFadeSeconds { get; set; } = 1;

        public double OutroFadeSeconds { get; set; } = 1;

        public string? PodcastTitle { get; set; }

        public string? PodcastArtist { get; set; }

        public string? PodcastAlbum { get; set; }

        public string OutputFormatExtension { get; set; } = ".m4a";

        public double VolumeGainDb { get; set; }
    }
}
