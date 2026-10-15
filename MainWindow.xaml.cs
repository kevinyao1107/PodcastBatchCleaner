using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
#if CROSS_PLATFORM
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PodcastBatchCleaner.CrossPlatform.Services;
using MediaPlayer = PodcastBatchCleaner.CrossPlatform.Services.FfplayAudioPlayer;
using PlaybackPositionChangedEventArgs = Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs;
using ExceptionEventArgs = PodcastBatchCleaner.CrossPlatform.Services.AudioPlaybackErrorEventArgs;
using DesktopMessageBox = PodcastBatchCleaner.CrossPlatform.Services.DesktopMessageBox;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using DesktopMessageBox = System.Windows.MessageBox;
using PlaybackPositionChangedEventArgs = System.Windows.RoutedPropertyChangedEventArgs<double>;
using Button = System.Windows.Controls.Button;
#endif
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;
#if !CROSS_PLATFORM
using Forms = System.Windows.Forms;
#endif

#if CROSS_PLATFORM
namespace PodcastBatchCleaner.CrossPlatform;
#else
namespace PodcastBatchCleanerWpf;
#endif

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FfmpegAudioProcessor _processor = new();
    private readonly DeepFilterNetAudioProcessor _deepFilterNetProcessor = new();
    private readonly VoiceFixerAudioProcessor _voiceFixerProcessor = new();
    private readonly AudioBlendProcessor _audioBlendProcessor = new();
    private int _denoiseStrengthIndex = 1;
    private double _voiceFixerMix = 0.6;
    private bool _matchComparisonLoudness = true;
    private bool _aiDenoiseApplied;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _playbackTimer;
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _processingCancellation;
    private CancellationTokenSource? _analysisCancellation;
    private CancellationTokenSource? _folderLoadingCancellation;
    private CancellationTokenSource? _durationLoadingCancellation;
    private bool _isLoadingFiles;
    private AudioFileItem? _selectedFile;
#if !CROSS_PLATFORM
    private AudioFileItem? _draggedAudioFile;
#endif
#if !CROSS_PLATFORM
    private System.Windows.Point _dragStartPoint;
#endif
    private string? _currentFolder;
    private string? _outputFolder;
    private string? _aiAudioFolder;
    private string? _ffmpegPath;
    private string _waveformText = "選取音檔後，按「讀取原音波形」。暗色區域不會保留。";
    private double _waveformPosition;
    private CancellationTokenSource? _waveformCancellation;
    private double? _pendingWaveformSeek;
    public string WaveformText { get => _waveformText; private set => SetField(ref _waveformText, value); }
    private bool _isDraggingPosition;
    private bool _isProcessing;
    private bool _closeAfterWork;
    private readonly List<string> _processingWarnings = [];
    private readonly List<string> _completedProcessingTools = [];
    private int _processingWarningCount;
    private AudioFileItem? _processingReportItem;
    private bool _loadedDeepFilterNetPreference;
    private int _loudnessTarget = -16;
    private bool _removeSilence = true;
    private double _silencePaddingSeconds;
    private PortablePreset? _selectedPortablePreset;
    private string _presetName = "我的設定";
    private double _silenceSeconds = 0.1;
    private double _silenceThresholdDb = -35;
    private bool _enableDenoise = true;
    private bool _useAiProcessedAudio;
    private bool _enableDeepFilterNet;
    private bool _enableDeepFilterNetPostFilter = true;
    private bool _fallbackToFfmpegWhenAiFails = true;
    private string? _deepFilterNetPath;
    private bool _enableVoiceFixerDereverb;
    private int _voiceFixerMode;
    private string? _voiceFixerPath;
    private bool _reduceRoomTone;
    private bool _reduceReverb;
    private double _reverbReductionAmount = FfmpegAudioProcessor.DefaultReverbReductionAmount;
    private bool _enableThreeBandEq;
    private double _lowEqDb, _midEqDb, _highEqDb;
    private bool _enableDeEsser;
    private double _deEsserAmount = 45;
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
    private AudioChannelMode _selectedChannelMode = FfmpegAudioProcessor.DefaultPodcastChannelMode;
    private double _volumeGainDb;
    private double _stereoBalance;
    private int _playbackSourceIndex;
    private string _currentProcessingFileText = string.Empty;
    private string _processingProgressText = string.Empty;
    private double _processingProgress;
    private bool _isProgressVisible;
    private bool _isAnalyzing;
    private string _analysisText = "尚未分析。";
    private string _statusText = "請先選取資料夾。";
    private string _playbackPositionText = "00:00";
    private string _playbackDurationText = "00:00";

#if !CROSS_PLATFORM
    public MainWindow() : this(loadSettings: true)
    {
    }

#endif
#if !CROSS_PLATFORM
    internal MainWindow(bool loadSettings)
    {
        InitializeComponent();
        InitializeWorkspaceUi();
        OriginalWaveform.SeekRequested += SeekOriginalWaveform;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.MediaFailed += Player_MediaFailed;

        DataContext = this;
        if (loadSettings) LoadSettings();
    }

#endif
    public ObservableCollection<AudioFileItem> AudioFiles { get; } = [];

    public IReadOnlyList<AudioOutputFormat> OutputFormats => FfmpegAudioProcessor.OutputFormats;

    public IReadOnlyList<AudioChannelMode> ChannelModes => FfmpegAudioProcessor.ChannelModes;

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

    public string VoiceFixerPathText => _voiceFixerPath is null
        ? "VoiceFixer：尚未設定 voicefixer.exe / voicefixer.cmd"
        : $"VoiceFixer：{_voiceFixerPath}";

    public string VoiceFixerStatusText
    {
        get
        {
            if (!EnableVoiceFixerDereverb)
            {
                return "免費去殘響模型：已停用";
            }

            if (VoiceFixerMix <= 0) return "VoiceFixer：混合比例 0%，略過模型";

            return string.IsNullOrWhiteSpace(_voiceFixerPath) || !File.Exists(_voiceFixerPath)
                ? "免費去殘響模型：未找到 VoiceFixer"
                : $"免費去殘響模型：VoiceFixer 已就緒，模式 {VoiceFixerMode}";
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

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set
        {
            if (SetField(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(CanStartWork));
            OnPropertyChanged(nameof(CanReviewEpisode));
            }
        }
    }

    public bool IsLoadingFiles
    {
        get => _isLoadingFiles;
        private set
        {
            if (!SetField(ref _isLoadingFiles, value)) return;
            OnPropertyChanged(nameof(CanStartWork));
            OnPropertyChanged(nameof(CanReviewEpisode));
        }
    }
    public bool CanStartWork => !_isProcessing && !_isAnalyzing && !IsLoadingFiles;

    public string ProcessingWarningsText => string.Join(Environment.NewLine, _processingWarnings)
        + (_processingWarningCount > _processingWarnings.Count
            ? $"{Environment.NewLine}其餘 {_processingWarningCount - _processingWarnings.Count} 則警告未列出。" : string.Empty);
    public bool HasProcessingWarnings => _processingWarningCount > 0;
    public string ProcessingWarningSummary => $"處理警告：{_processingWarningCount} 則";
    public string ProcessingToolsText => _processingReportItem is null
        ? "尚無處理紀錄"
        : $"{_processingReportItem.FileName}：{string.Join(" → ", _completedProcessingTools)}";
    public bool IsDenoiseStrengthEnabled => EnableDenoise || EnableDeepFilterNet;
    public bool IsAiFallbackEnabled => EnableDeepFilterNet || (EnableVoiceFixerDereverb && VoiceFixerMix > 0);
    public bool IsVoiceFixerModeEnabled => EnableVoiceFixerDereverb && VoiceFixerMix > 0;

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetField(ref _isProcessing, value))
            {
                if (value)
                {
                    _processingWarnings.Clear();
                    _processingWarningCount = 0;
                    _completedProcessingTools.Clear();
                    _processingReportItem = null;
                    NotifyProcessingReportChanged();
                }
                OnPropertyChanged(nameof(CanStartWork));
            OnPropertyChanged(nameof(CanReviewEpisode));
            }
        }
    }

    public string AnalysisText
    {
        get => _analysisText;
        set => SetField(ref _analysisText, value);
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

    public bool EnableThreeBandEq { get => _enableThreeBandEq; set => SetField(ref _enableThreeBandEq, value); }
    public double LowEqDb { get => _lowEqDb; set { if (double.IsFinite(value)) SetField(ref _lowEqDb, Math.Clamp(value, -12, 12)); } }
    public double MidEqDb { get => _midEqDb; set { if (double.IsFinite(value)) SetField(ref _midEqDb, Math.Clamp(value, -12, 12)); } }
    public double HighEqDb { get => _highEqDb; set { if (double.IsFinite(value)) SetField(ref _highEqDb, Math.Clamp(value, -12, 12)); } }
    public bool EnableDeEsser { get => _enableDeEsser; set => SetField(ref _enableDeEsser, value); }
    public double DeEsserAmount { get => _deEsserAmount; set { if (double.IsFinite(value)) SetField(ref _deEsserAmount, Math.Clamp(value, 0, 100)); } }

    public IReadOnlyList<int> LoudnessTargets { get; } = [-14, -16, -18, -23];
    public int LoudnessTarget
    {
        get => _loudnessTarget;
        set { if (LoudnessTargets.Contains(value)) SetField(ref _loudnessTarget, value); }
    }
    public bool RemoveSilence { get => _removeSilence; set => SetField(ref _removeSilence, value); }
    public double SilencePaddingSeconds { get => _silencePaddingSeconds; set => SetField(ref _silencePaddingSeconds, Math.Clamp(value, 0, .5)); }
    public ObservableCollection<PortablePreset> PortablePresets { get; } = [];
    public PortablePreset? SelectedPortablePreset { get => _selectedPortablePreset; set => SetField(ref _selectedPortablePreset, value); }
    public string PresetName { get => _presetName; set => SetField(ref _presetName, value); }

    public double SilenceSeconds
    {
        get => _silenceSeconds;
        set => SetField(ref _silenceSeconds, Math.Clamp(Math.Round(value, 1), 0.1, 10.0));
    }

    public double SilenceThresholdDb
    {
        get => _silenceThresholdDb;
        set => SetField(ref _silenceThresholdDb, Math.Clamp(Math.Round(value), -80, -5));
    }

    public bool EnableDenoise
    {
        get => _enableDenoise;
        set
        {
            if (SetField(ref _enableDenoise, value)) OnPropertyChanged(nameof(IsDenoiseStrengthEnabled));
        }
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
                OnPropertyChanged(nameof(IsDenoiseStrengthEnabled));
                OnPropertyChanged(nameof(IsAiFallbackEnabled));
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

    public bool EnableVoiceFixerDereverb
    {
        get => _enableVoiceFixerDereverb;
        set
        {
            if (SetField(ref _enableVoiceFixerDereverb, value))
            {
                OnPropertyChanged(nameof(VoiceFixerStatusText));
                OnPropertyChanged(nameof(IsVoiceFixerModeEnabled));
                OnPropertyChanged(nameof(IsAiFallbackEnabled));
            }
        }
    }

    public int VoiceFixerMode
    {
        get => _voiceFixerMode;
        set
        {
            if (SetField(ref _voiceFixerMode, Math.Clamp(value, 0, 2)))
            {
                OnPropertyChanged(nameof(VoiceFixerStatusText));
            }
        }
    }

    public bool ReduceRoomTone
    {
        get => _reduceRoomTone;
        set => SetField(ref _reduceRoomTone, value);
    }

    public int DenoiseStrengthIndex
    {
        get => _denoiseStrengthIndex;
        set => SetField(ref _denoiseStrengthIndex, Math.Clamp(value, 0, 2));
    }

    private double DenoiseReductionDb => new[] { 6d, 12d, 24d }[DenoiseStrengthIndex];
    private double AiAttenuationLimitDb => new[] { 10d, 20d, 100d }[DenoiseStrengthIndex];

    public double VoiceFixerMix
    {
        get => _voiceFixerMix;
        set
        {
            if (SetField(ref _voiceFixerMix, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.6))
            {
                OnPropertyChanged(nameof(IsVoiceFixerModeEnabled));
                OnPropertyChanged(nameof(IsAiFallbackEnabled));
                OnPropertyChanged(nameof(VoiceFixerStatusText));
            }
        }
    }

    public bool MatchComparisonLoudness
    {
        get => _matchComparisonLoudness;
        set => SetField(ref _matchComparisonLoudness, value);
    }

    public bool ReduceReverb
    {
        get => _reduceReverb;
        set
        {
            if (SetField(ref _reduceReverb, value))
            {
                OnPropertyChanged(nameof(IsReverbReductionEnabled));
            }
        }
    }

    public double ReverbReductionAmount
    {
        get => _reverbReductionAmount;
        set => SetField(ref _reverbReductionAmount, Math.Clamp(Math.Round(value, 2), 0.2, 1.0));
    }

    public bool IsReverbReductionEnabled => ReduceReverb;

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

    public AudioChannelMode SelectedChannelMode
    {
        get => _selectedChannelMode;
        set
        {
            if (SetField(ref _selectedChannelMode, value))
            {
                OnPropertyChanged(nameof(IsStereoBalanceEnabled));
            }
        }
    }

    public double VolumeGainDb
    {
        get => _volumeGainDb;
        set => SetField(ref _volumeGainDb, Math.Clamp(Math.Round(value, 1), -24, 24));
    }

    public double StereoBalance
    {
        get => _stereoBalance;
        set => SetField(ref _stereoBalance, Math.Clamp(Math.Round(value, 2), -1, 1));
    }

    public bool IsStereoBalanceEnabled => string.Equals(SelectedChannelMode.Value, "balance", StringComparison.OrdinalIgnoreCase);

    public int PlaybackSourceIndex
    {
        get => _playbackSourceIndex;
        set => SetField(ref _playbackSourceIndex, value);
    }

    #if CROSS_PLATFORM
    public new event PropertyChangedEventHandler? PropertyChanged;
#else
    public event PropertyChangedEventHandler? PropertyChanged;
#endif

    private void ApplyPodcastPreset_Click(object sender, RoutedEventArgs e)
    {
        ApplyProcessingPreset(
            silenceSeconds: 0.3,
            silenceThresholdDb: -35,
            enableDenoise: true,
            reduceRoomTone: true,
            reduceReverb: false,
            reverbReductionAmount: FfmpegAudioProcessor.DefaultReverbReductionAmount,
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
            reduceReverb: true,
            reverbReductionAmount: 0.8,
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
            reduceReverb: false,
            reverbReductionAmount: FfmpegAudioProcessor.DefaultReverbReductionAmount,
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
            reduceReverb: false,
            reverbReductionAmount: FfmpegAudioProcessor.DefaultReverbReductionAmount,
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
        bool reduceReverb,
        double reverbReductionAmount,
        bool enhanceVoiceEq,
        bool normalizeLoudness,
        bool enableLimiter,
        double volumeGainDb,
        string statusText)
    {
        SilenceSeconds = silenceSeconds;
        EnableAdvancedCleanup = false;
        ApplyVoiceTuning(new());
        SilenceThresholdDb = silenceThresholdDb;
        EnableDenoise = enableDenoise;
        ReduceRoomTone = reduceRoomTone;
        ReduceReverb = reduceReverb;
        ReverbReductionAmount = reverbReductionAmount;
        EnhanceVoiceEq = enhanceVoiceEq;
        EnableThreeBandEq = EnableDeEsser = false;
        LowEqDb = MidEqDb = HighEqDb = 0;
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

        if (VoiceFixerMix > 0 && (string.IsNullOrWhiteSpace(_voiceFixerPath) || !File.Exists(_voiceFixerPath)))
        {
            _voiceFixerPath = VoiceFixerAudioProcessor.TryFindVoiceFixer();
        }

        OnPropertyChanged(nameof(FfmpegPathText));
        OnPropertyChanged(nameof(DeepFilterNetPathText));
        OnPropertyChanged(nameof(DeepFilterNetStatusText));
        OnPropertyChanged(nameof(VoiceFixerPathText));
        OnPropertyChanged(nameof(VoiceFixerStatusText));

        StatusText = _ffmpegPath is null
            ? "找不到 FFmpeg。播放可使用；輸出前請指定 ffmpeg.exe。"
            : "已找到 FFmpeg，可以選取資料夾開始。";

        if (!string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
        {
            try { await LoadAudioFilesAsync(_currentFolder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { StatusText = $"無法載入上次資料夾：{ex.Message}"; }
        }

        await Task.CompletedTask;
    }

#if !CROSS_PLATFORM
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!CanStartWork || _durationLoadingCancellation is not null)
        {
            e.Cancel = true;
            _closeAfterWork = true;
            _processingCancellation?.Cancel();
            _analysisCancellation?.Cancel();
            _folderLoadingCancellation?.Cancel();
            _durationLoadingCancellation?.Cancel();
            StatusText = "正在停止工作並關閉...";
            return;
        }
        SaveSettings();
        _processingCancellation?.Cancel();
        _analysisCancellation?.Cancel();
        _durationLoadingCancellation?.Cancel();
        _playbackTimer.Stop();
        _player.Close();
    }

#endif
#if !CROSS_PLATFORM
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

        await LoadAudioFilesAsync(dialog.SelectedPath);
    }

#endif
#if !CROSS_PLATFORM
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

#endif
#if !CROSS_PLATFORM
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

#endif
#if !CROSS_PLATFORM
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

#endif
    private void ClearDeepFilterNet_Click(object sender, RoutedEventArgs e)
    {
        _deepFilterNetPath = null;
        EnableDeepFilterNet = false;
        OnPropertyChanged(nameof(DeepFilterNetPathText));
        OnPropertyChanged(nameof(DeepFilterNetStatusText));
        StatusText = "已清除 DeepFilterNet。";
    }

#if !CROSS_PLATFORM
    private void ChooseVoiceFixer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選取 voicefixer.exe / voicefixer.cmd",
            Filter = "VoiceFixer|voicefixer.exe;voicefixer.cmd;voicefixer.bat|執行檔|*.exe;*.cmd;*.bat|所有檔案|*.*",
            InitialDirectory = _currentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _voiceFixerPath = dialog.FileName;
        EnableVoiceFixerDereverb = true;
        OnPropertyChanged(nameof(VoiceFixerPathText));
        OnPropertyChanged(nameof(VoiceFixerStatusText));
        StatusText = "已設定 VoiceFixer。";
    }

#endif
    private void ClearVoiceFixer_Click(object sender, RoutedEventArgs e)
    {
        _voiceFixerPath = null;
        EnableVoiceFixerDereverb = false;
        OnPropertyChanged(nameof(VoiceFixerPathText));
        OnPropertyChanged(nameof(VoiceFixerStatusText));
        StatusText = "已清除 VoiceFixer。";
    }

#if !CROSS_PLATFORM
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

#endif
#if !CROSS_PLATFORM
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

#endif
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

#if !CROSS_PLATFORM
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

#endif
    private void ClearCoverImage_Click(object sender, RoutedEventArgs e)
    {
        _coverImagePath = null;
        OnPropertyChanged(nameof(CoverImageText));
        StatusText = "已清除封面圖片。";
    }

#if !CROSS_PLATFORM
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

#endif
#if !CROSS_PLATFORM
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

#endif
    private async Task LoadAudioFilesAsync(string folder)
    {
        _folderLoadingCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _folderLoadingCancellation = cancellation;
        _durationLoadingCancellation?.Cancel();
        IsLoadingFiles = true;
        StatusText = "正在掃描資料夾...";
        try
        {
            var files = await Task.Run(() => Directory
                .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    return FfmpegAudioProcessor.IsSupportedAudioFile(path);
                })
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToList(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_folderLoadingCancellation != cancellation) return;

            var previousDefault = _currentFolder is null ? null : Path.Combine(_currentFolder, "processed");
            if (_outputFolder is null || string.Equals(_outputFolder, previousDefault,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                _outputFolder = Path.Combine(folder, "processed");
            _currentFolder = folder;
            StopPlayback(); _player.Close();
            AudioFiles.Clear();
            foreach (var file in files) AudioFiles.Add(new AudioFileItem { FilePath = file });
            OnPropertyChanged(nameof(CurrentFolderText)); OnPropertyChanged(nameof(OutputFolderText));
            OnPropertyChanged(nameof(FileCountText));
            StatusText = files.Count == 0 ? "這個資料夾沒有支援的音檔。" : $"已載入 {files.Count} 個音檔。";
            _ = LoadDurationsAsync(AudioFiles.ToList());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_folderLoadingCancellation == cancellation) StatusText = $"載入資料夾失敗：{ex.Message}";
        }
        finally
        {
            if (_folderLoadingCancellation == cancellation)
            {
                _folderLoadingCancellation = null; IsLoadingFiles = false; FinishPendingClose();
            }
        }
    }

    private async Task LoadDurationsAsync(IReadOnlyList<AudioFileItem> items)
    {
        _durationLoadingCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _durationLoadingCancellation = cancellation;
        var ffmpegPath = _ffmpegPath;
        try
        {
            if (ffmpegPath is null) return;
            foreach (var item in items)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try { item.Duration = await _processor.ProbeDurationAsync(ffmpegPath, item.FilePath, cancellation.Token); }
                catch (OperationCanceledException) { throw; }
                catch { item.Duration = null; }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_durationLoadingCancellation == cancellation)
            { _durationLoadingCancellation = null; FinishPendingClose(); }
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

    private async void AnalyzeOriginal_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedAudioFile();
        if (item is null)
        {
            StatusText = "請先選取要分析的音檔。";
            return;
        }

        await AnalyzeAudioAsync(item, item.FilePath, "原始音檔");
    }

    private async void AnalyzeProcessed_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedAudioFile();
        if (item is null)
        {
            StatusText = "請先選取要分析的音檔。";
            return;
        }

        if (!item.HasProcessedFile || item.ProcessedPath is null)
        {
            StatusText = PlaybackSourceIndex == 2 ? "尚未有可播放的整集成品。" : "這個音檔還沒有處理後版本。";
            return;
        }

        await AnalyzeAudioAsync(item, item.ProcessedPath, "處理後音檔");
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

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e)
    {
        _analysisCancellation?.Cancel();
        StatusText = "正在取消分析...";
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
#if CROSS_PLATFORM
        AudioList.ScrollIntoView(selected, null);
#else
        AudioList.ScrollIntoView(selected);
#endif
        StatusText = "已調整合併順序。";
    }

    private AudioFileItem? GetSelectedAudioFile()
    {
        return AudioList.SelectedItem as AudioFileItem ?? _selectedFile;
    }

    private async Task ProcessItemsAsync(IReadOnlyList<AudioFileItem> items)
    {
        if (!CanStartWork)
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

        IsProcessing = true;
        _processingCancellation = new CancellationTokenSource();
        IsProgressVisible = true;
        ProcessingProgress = 0;
        ProcessingProgressText = "0%";
        CurrentProcessingFileText = string.Empty;
        var summary = new ProcessingRunSummary(items.Count, _outputFolder, MergeSegments);
        var showSummary = false;
        var externalStageCount = CountExternalPreprocessStages();
        var aiTempDirectory = externalStageCount > 0
            ? Path.Combine(_outputFolder, $".ai_stage_{Guid.NewGuid():N}")
            : null;

        var options = new AudioProcessingOptions(
            SilenceSeconds,
            SilenceThresholdDb,
            EnableDenoise,
            ReduceRoomTone,
            ReduceReverb,
            ReverbReductionAmount,
            EnhanceVoiceEq,
            NormalizeLoudness,
            EnableLimiter,
            VolumeGainDb,
            SelectedChannelMode,
            StereoBalance,
            _outputFolder,
            DenoiseReductionDb, RemoveSilence, LoudnessTarget, SilencePaddingSeconds, EnableThreeBandEq, LowEqDb, MidEqDb, HighEqDb, EnableDeEsser, DeEsserAmount, EnableAdvancedCleanup ? _cleanupTuning : null, _voiceTuning);

        var progressWindow = new ProcessingProgressWindow
        {
            Owner = this
        };
        progressWindow.CancelRequested += (_, _) => _processingCancellation?.Cancel();
        progressWindow.Show();

        try
        {
            ffmpegPath = await EnsureOutputEncoderAsync(ffmpegPath, progressWindow, _processingCancellation.Token);
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
                    var sourceDuration = await GetProcessingDurationAsync(ffmpegPath, item, processingInputPath,
                        _processingCancellation.Token);
                    if (!TryGetTrimRangeForDuration(item, sourceDuration, out var trimStart, out var trimEnd, out var trimError))
                    {
                        item.Status = "剪輯時間錯誤";
                        throw new InvalidOperationException(trimError);
                    }

                    var effectiveDuration = GetEffectiveDuration(sourceDuration, trimStart, trimEnd);
                    var completedExternalStages = 0;
                    if (EnableDeepFilterNet)
                    {
                        try
                        {
                            var stageStart = GetExternalStageFraction(completedExternalStages, externalStageCount);
                            var stageEnd = GetExternalStageFraction(completedExternalStages + 1, externalStageCount);
                            processingInputPath = await ProcessWithDeepFilterNetAsync(
                                ffmpegPath,
                                processingInputPath,
                                item.FileName,
                                aiTempDirectory!,
                                progressWindow,
                                Math.Clamp(((itemIndex + stageStart) / total) * 100, 0, 100),
                                Math.Clamp(((itemIndex + stageEnd) / total) * 100, 0, 100),
                                effectiveDuration,
                                trimStart,
                                trimEnd,
                                _processingCancellation.Token);

                            completedExternalStages++;
                            trimStart = null;
                            trimEnd = null;
                            effectiveDuration = await _processor.ProbeDurationAsync(
                                ffmpegPath,
                                processingInputPath,
                                _processingCancellation.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                        {
                            completedExternalStages++;
                            item.Status = "AI 失敗，改用 FFmpeg";
                            RecordAiFallback("DeepFilterNet", item.FileName, ex);
                            StatusText = $"DeepFilterNet 失敗，改用 FFmpeg：{item.FileName}";
                            progressWindow.SetProgress($"AI 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                        }
                    }

                    if (EnableVoiceFixerDereverb)
                    {
                        try
                        {
                            var stageStart = GetExternalStageFraction(completedExternalStages, externalStageCount);
                            var stageEnd = GetExternalStageFraction(completedExternalStages + 1, externalStageCount);
                            processingInputPath = await ProcessWithVoiceFixerAsync(
                                ffmpegPath,
                                processingInputPath,
                                item.FileName,
                                aiTempDirectory!,
                                progressWindow,
                                Math.Clamp(((itemIndex + stageStart) / total) * 100, 0, 100),
                                Math.Clamp(((itemIndex + stageEnd) / total) * 100, 0, 100),
                                effectiveDuration,
                                trimStart,
                                trimEnd,
                                _processingCancellation.Token);

                            completedExternalStages++;
                            trimStart = null;
                            trimEnd = null;
                            effectiveDuration = await _processor.ProbeDurationAsync(
                                ffmpegPath,
                                processingInputPath,
                                _processingCancellation.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                        {
                            completedExternalStages++;
                            item.Status = "VoiceFixer 失敗，改用 FFmpeg";
                            RecordAiFallback("VoiceFixer", item.FileName, ex);
                            StatusText = $"VoiceFixer 失敗，改用 FFmpeg：{item.FileName}";
                            progressWindow.SetProgress($"VoiceFixer 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                        }
                    }

                    var progress = new Progress<AudioProcessingProgress>(current =>
                    {
                        var ffmpegStart = GetFfmpegStartFraction(externalStageCount);
                        var itemPercent = ffmpegStart + (current.Percent * (1 - ffmpegStart));
                        var overallPercent = Math.Clamp(((itemIndex + itemPercent) / total) * 100, 0, 100);
                        ProcessingProgress = overallPercent;
                        ProcessingProgressText = $"{overallPercent:0}%";
                        progressWindow.SetProgress($"{current.Stage ?? "處理中"}：{item.FileName}", overallPercent);
                        item.Status = $"{itemPercent * 100:0}%";
                        StatusText = $"{current.Stage ?? "處理中"}：{item.FileName}";
                    });

                    await _processor.ProcessAsync(
                        ffmpegPath,
                        processingInputPath,
                        outputPath,
                        AvoidRepeatedDenoise(options),
                        effectiveDuration,
                        progress,
                        cancellationToken: _processingCancellation.Token,
                        audioCodec: SelectedOutputFormat.AudioCodec,
                        trimStart: trimStart,
                        trimEnd: trimEnd);

                    RecordCompletedProcessingTool("FFmpeg");
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
                    if (!_processingCancellation.IsCancellationRequested)
                    {
                        ProcessingProgress = Math.Clamp(((itemIndex + 1d) / total) * 100, 0, 100);
                        ProcessingProgressText = $"{ProcessingProgress:0}%";
                        progressWindow.SetProgress(item.FileName, ProcessingProgress);
                    }
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
            IsProcessing = false;
            _processingCancellation?.Dispose();
            _processingCancellation = null;
            CurrentProcessingFileText = string.Empty;
            IsProgressVisible = false;
            progressWindow.CompleteAndClose();

            if (showSummary && !_closeAfterWork)
            {
                ShowProcessingSummary(summary);
            }

            if (aiTempDirectory is not null)
            {
                TryDeleteDirectory(aiTempDirectory);
            }
            FinishPendingClose();
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
        var segmentNames = new List<string>();
        if (PlaybackSourceIndex == 2 || (_lastMergedOutputPath is not null && _player.Source?.LocalPath == _lastMergedOutputPath)) { _playbackTimer.Stop(); _player.Close(); }
        var hasIntro = !string.IsNullOrWhiteSpace(_introAudioPath) && File.Exists(_introAudioPath);
        var hasOutro = !string.IsNullOrWhiteSpace(_outroAudioPath) && File.Exists(_outroAudioPath);
        var totalSteps = items.Count + 1d + (hasIntro ? 1 : 0) + (hasOutro ? 1 : 0);
        var completedSteps = 0d;
        var externalStageCount = CountExternalPreprocessStages();

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

                segmentPaths.Add(introPath); segmentNames.Add($"片頭 · {introFileName}");
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

                var duration = await GetProcessingDurationAsync(
                    ffmpegPath, item, processingInputPath,
                    _processingCancellation?.Token ?? CancellationToken.None);

                if (!TryGetTrimRangeForDuration(item, duration, out var trimStart, out var trimEnd, out var trimError))
                {
                    item.Status = "剪輯時間錯誤";
                    throw new InvalidOperationException($"{item.FileName}：{trimError}");
                }

                var segmentPath = Path.Combine(tempDirectory, $"{index + 1:0000}.wav");
                var stepStart = completedSteps;
                var effectiveDuration = GetEffectiveDuration(duration, trimStart, trimEnd);
                var completedExternalStages = 0;

                if (EnableDeepFilterNet)
                {
                    try
                    {
                        var stageStart = GetExternalStageFraction(completedExternalStages, externalStageCount);
                        var stageEnd = GetExternalStageFraction(completedExternalStages + 1, externalStageCount);
                        processingInputPath = await ProcessWithDeepFilterNetAsync(
                            ffmpegPath,
                            processingInputPath,
                            item.FileName,
                            tempDirectory,
                            progressWindow,
                            Math.Clamp(((stepStart + stageStart) / totalSteps) * 100, 0, 100),
                            Math.Clamp(((stepStart + stageEnd) / totalSteps) * 100, 0, 100),
                            effectiveDuration,
                            trimStart,
                            trimEnd,
                            _processingCancellation?.Token ?? CancellationToken.None);

                        completedExternalStages++;
                        trimStart = null;
                        trimEnd = null;
                        effectiveDuration = await _processor.ProbeDurationAsync(
                            ffmpegPath,
                            processingInputPath,
                            _processingCancellation?.Token ?? CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                    {
                        completedExternalStages++;
                        item.Status = "AI 失敗，改用 FFmpeg";
                        RecordAiFallback("DeepFilterNet", item.FileName, ex);
                        StatusText = $"DeepFilterNet 失敗，改用 FFmpeg：{item.FileName}";
                        progressWindow.SetProgress($"AI 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                    }
                }

                if (EnableVoiceFixerDereverb)
                {
                    try
                    {
                        var stageStart = GetExternalStageFraction(completedExternalStages, externalStageCount);
                        var stageEnd = GetExternalStageFraction(completedExternalStages + 1, externalStageCount);
                        processingInputPath = await ProcessWithVoiceFixerAsync(
                            ffmpegPath,
                            processingInputPath,
                            item.FileName,
                            tempDirectory,
                            progressWindow,
                            Math.Clamp(((stepStart + stageStart) / totalSteps) * 100, 0, 100),
                            Math.Clamp(((stepStart + stageEnd) / totalSteps) * 100, 0, 100),
                            effectiveDuration,
                            trimStart,
                            trimEnd,
                            _processingCancellation?.Token ?? CancellationToken.None);

                        completedExternalStages++;
                        trimStart = null;
                        trimEnd = null;
                        effectiveDuration = await _processor.ProbeDurationAsync(
                            ffmpegPath,
                            processingInputPath,
                            _processingCancellation?.Token ?? CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                    {
                        completedExternalStages++;
                        item.Status = "VoiceFixer 失敗，改用 FFmpeg";
                        RecordAiFallback("VoiceFixer", item.FileName, ex);
                        StatusText = $"VoiceFixer 失敗，改用 FFmpeg：{item.FileName}";
                        progressWindow.SetProgress($"VoiceFixer 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                    }
                }

                var progress = new Progress<AudioProcessingProgress>(current =>
                {
                    var ffmpegStart = GetFfmpegStartFraction(externalStageCount);
                    var itemPercent = ffmpegStart + (current.Percent * (1 - ffmpegStart));
                    UpdateMergeStepProgress($"{current.Stage ?? "處理分段"}：{item.FileName}", stepStart, itemPercent, totalSteps, progressWindow);
                    item.Status = $"{itemPercent * 100:0}%";
                });

                await _processor.ProcessAsync(
                    ffmpegPath,
                    processingInputPath,
                    segmentPath,
                    AvoidRepeatedDenoise(options),
                    effectiveDuration,
                    progress,
                    _processingCancellation?.Token ?? CancellationToken.None,
                    audioCodec: "pcm_s16le",
                    sampleRate: 44100,
                    channels: 2,
                    trimStart: trimStart,
                    trimEnd: trimEnd);

                RecordCompletedProcessingTool("FFmpeg");
                segmentPaths.Add(segmentPath); segmentNames.Add(item.FileName);
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

                segmentPaths.Add(outroPath); segmentNames.Add($"片尾 · {outroFileName}");
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
                    $"{current.Stage ?? "合併輸出"}：{Path.GetFileName(mergedOutputPath)}",
                    completedSteps,
                    current.Percent,
                    totalSteps,
                    progressWindow);
                StatusText = $"{current.Stage ?? "合併輸出"}：{Path.GetFileName(mergedOutputPath)}";
            });

            if (options.NormalizeLoudness)
            {
                StatusText = $"整集響度分析：{Path.GetFileName(mergedOutputPath)}";
                progressWindow.SetIndeterminate("整集響度分析", Path.GetFileName(mergedOutputPath));
            }
            var durations = new List<(string Name, TimeSpan Duration)>();
            for (var index = 0; index < segmentPaths.Count; index++)
                durations.Add((segmentNames[index], await _processor.ProbeDurationAsync(ffmpegPath, segmentPaths[index],
                    _processingCancellation?.Token ?? CancellationToken.None) ?? throw new InvalidDataException("無法判定整集段落長度。")));
            var chapters = AudioWorkSessionFile.BuildChapters(durations, MergeGapSeconds);
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
                SelectedOutputFormat,
                normalizeLoudness: options.NormalizeLoudness,
                enableLimiter: options.EnableLimiter, loudnessTarget: options.LoudnessTarget);

            SetEpisodePreview(mergedOutputPath, chapters);
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
        if (!CanStartWork)
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

        IsProcessing = true;
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
        var externalStageCount = CountExternalPreprocessStages();
        var comparisonTempDirectory = Path.Combine(comparisonFolder, $".ai_compare_{Guid.NewGuid():N}");
        Directory.CreateDirectory(comparisonTempDirectory);

        try
        {
            var processingInputPath = GetProcessingInputPath(item);
            ffmpegPath = await EnsureOutputEncoderAsync(ffmpegPath, progressWindow, _processingCancellation.Token);
            var sourceDuration = await GetProcessingDurationAsync(ffmpegPath, item, processingInputPath,
                _processingCancellation.Token);

            if (!TryGetTrimRangeForDuration(item, sourceDuration, out var trimStart, out var trimEnd, out var trimError))
            {
                throw new InvalidOperationException($"{item.FileName}：{trimError}");
            }

            var originalDuration = await GetProcessingDurationAsync(ffmpegPath, item, item.FilePath,
                _processingCancellation.Token);
            if (!TryGetTrimRangeForDuration(item, originalDuration, out var originalTrimStart, out var originalTrimEnd, out var originalError))
                throw new InvalidOperationException($"原音比較：{originalError}");
            var effectiveDuration = GetEffectiveDuration(sourceDuration, trimStart, trimEnd);
            var completedExternalStages = 0;
            if (EnableDeepFilterNet)
            {
                try
                {
                    var stageStart = GetExternalStageFraction(completedExternalStages, externalStageCount);
                    var stageEnd = GetExternalStageFraction(completedExternalStages + 1, externalStageCount);
                    processingInputPath = await ProcessWithDeepFilterNetAsync(
                        ffmpegPath,
                        processingInputPath,
                        item.FileName,
                        comparisonTempDirectory!,
                        progressWindow,
                        stageStart * 100,
                        stageEnd * 100,
                        effectiveDuration,
                        trimStart,
                        trimEnd,
                        _processingCancellation.Token);

                    completedExternalStages++;
                    trimStart = null;
                    trimEnd = null;
                    effectiveDuration = await _processor.ProbeDurationAsync(
                        ffmpegPath,
                        processingInputPath,
                        _processingCancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                {
                    completedExternalStages++;
                    item.Status = "AI 失敗，改用 FFmpeg";
                    RecordAiFallback("DeepFilterNet", item.FileName, ex);
                    StatusText = $"DeepFilterNet 失敗，A/B 改用 FFmpeg：{item.FileName}";
                    progressWindow.SetProgress($"AI 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                }
            }

            if (EnableVoiceFixerDereverb)
            {
                try
                {
                    var stageStart = GetExternalStageFraction(completedExternalStages, externalStageCount);
                    var stageEnd = GetExternalStageFraction(completedExternalStages + 1, externalStageCount);
                    processingInputPath = await ProcessWithVoiceFixerAsync(
                        ffmpegPath,
                        processingInputPath,
                        item.FileName,
                        comparisonTempDirectory!,
                        progressWindow,
                        stageStart * 100,
                        stageEnd * 100,
                        effectiveDuration,
                        trimStart,
                        trimEnd,
                        _processingCancellation.Token);

                    completedExternalStages++;
                    trimStart = null;
                    trimEnd = null;
                    effectiveDuration = await _processor.ProbeDurationAsync(
                        ffmpegPath,
                        processingInputPath,
                        _processingCancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                {
                    completedExternalStages++;
                    item.Status = "VoiceFixer 失敗，改用 FFmpeg";
                    RecordAiFallback("VoiceFixer", item.FileName, ex);
                    StatusText = $"VoiceFixer 失敗，A/B 改用 FFmpeg：{item.FileName}";
                    progressWindow.SetProgress($"VoiceFixer 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                }
            }

            var presets = CreateComparisonPresets();

            for (var index = 0; index < presets.Count; index++)
            {
                _processingCancellation.Token.ThrowIfCancellationRequested();

                var preset = presets[index];
                var presetInput = preset.UseOriginalInput ? item.FilePath : processingInputPath;
                var presetStart = preset.UseOriginalInput ? originalTrimStart : trimStart;
                var presetEnd = preset.UseOriginalInput ? originalTrimEnd : trimEnd;
                var presetDuration = preset.UseOriginalInput
                    ? GetEffectiveDuration(originalDuration, originalTrimStart, originalTrimEnd) : effectiveDuration;
                var presetOptions = preset.UseOriginalInput ? preset.Options : AvoidRepeatedDenoise(preset.Options);
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
                    var presetBase = GetFfmpegStartFraction(externalStageCount);
                    var presetRange = 1 - presetBase;
                    var overallPercent = Math.Clamp(
                        (presetBase + (((itemIndex + current.Percent) / presets.Count) * presetRange)) * 100,
                        0,
                        100);
                    ProcessingProgress = overallPercent;
                    ProcessingProgressText = $"{overallPercent:0}%";
                    progressWindow.SetProgress($"{current.Stage ?? "A/B 比較"}：{item.FileName} - {preset.DisplayName}", overallPercent);
                    item.Status = $"{current.Percent * 100:0}%";
                });

                var renderPath = MatchComparisonLoudness
                    ? Path.Combine(comparisonTempDirectory, $"comparison_{index}.wav") : outputPath;
                await _processor.ProcessAsync(
                    ffmpegPath,
                    presetInput,
                    renderPath,
                    presetOptions with
                    {
                        OutputFolder = comparisonFolder,
                        NormalizeLoudness = !MatchComparisonLoudness && preset.Options.NormalizeLoudness
                    },
                    presetDuration,
                    progress,
                    cancellationToken: _processingCancellation.Token,
                    audioCodec: MatchComparisonLoudness ? "pcm_s16le" : SelectedOutputFormat.AudioCodec,
                    trimStart: presetStart,
                    trimEnd: presetEnd);

                if (MatchComparisonLoudness)
                {
                    progressWindow.SetIndeterminate("匹配 A/B 響度", item.FileName);
                    var matchingProgress = new Progress<AudioProcessingProgress>(current =>
                        progressWindow.SetIndeterminate(current.Stage ?? "匹配 A/B 響度", item.FileName));
                    await _processor.MatchComparisonLoudnessAsync(ffmpegPath, renderPath, outputPath,
                        SelectedOutputFormat.AudioCodec, _processingCancellation.Token, matchingProgress);
                }

                outputs.Add(outputPath);
                item.ProcessedPath = outputPath;
                var completedBase = GetFfmpegStartFraction(externalStageCount);
                var completedRange = 1 - completedBase;
                ProcessingProgress = Math.Clamp(
                    (completedBase + (((index + 1d) / presets.Count) * completedRange)) * 100,
                    0,
                    100);
                ProcessingProgressText = $"{ProcessingProgress:0}%";
            }

            RecordCompletedProcessingTool("FFmpeg");
            item.Status = "比較完成";
            item.ProcessedPath = outputs[1];
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
            DesktopMessageBox.Show(this, ex.Message, "A/B 比較失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsProcessing = false;
            _processingCancellation?.Dispose();
            _processingCancellation = null;
            CurrentProcessingFileText = string.Empty;
            IsProgressVisible = false;
            progressWindow.CompleteAndClose();

            if (comparisonTempDirectory is not null)
            {
                TryDeleteDirectory(comparisonTempDirectory);
            }
            FinishPendingClose();
        }
    }

    private async Task ProcessPreviewAsync(AudioFileItem item)
    {
        if (!CanStartWork)
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

        IsProcessing = true;
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

        var externalStageCount = CountExternalPreprocessStages();
        var previewTempDirectory = externalStageCount > 0
            ? Path.Combine(previewFolder, $".preview_stage_{Guid.NewGuid():N}")
            : null;

        try
        {
            item.IsProcessing = true;
            item.Status = "產生試聽";
            ffmpegPath = await EnsureOutputEncoderAsync(ffmpegPath, progressWindow, _processingCancellation.Token);
            StatusText = $"產生 30 秒試聽：{item.FileName}";

            var processingInputPath = GetProcessingInputPath(item);
            var sourceDuration = await GetProcessingDurationAsync(ffmpegPath, item, processingInputPath,
                _processingCancellation.Token);

            if (!TryGetTrimRangeForDuration(item, sourceDuration, out var trimStart, out var trimEnd, out var trimError))
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

            if (sourceDuration is not null && sourceDuration < previewEnd)
            {
                previewEnd = sourceDuration.Value;
            }

            trimEnd = previewEnd > previewStart ? previewEnd : trimEnd;
            var effectiveDuration = GetEffectiveDuration(sourceDuration, trimStart, trimEnd);
            var completedExternalStages = 0;

            if (EnableDeepFilterNet)
            {
                try
                {
                    var stageStart = GetExternalStageFraction(completedExternalStages, externalStageCount);
                    var stageEnd = GetExternalStageFraction(completedExternalStages + 1, externalStageCount);
                    processingInputPath = await ProcessWithDeepFilterNetAsync(
                        ffmpegPath,
                        processingInputPath,
                        item.FileName,
                        previewTempDirectory!,
                        progressWindow,
                        stageStart * 100,
                        stageEnd * 100,
                        effectiveDuration,
                        trimStart,
                        trimEnd,
                        _processingCancellation.Token);

                    completedExternalStages++;
                    trimStart = null;
                    trimEnd = null;
                    effectiveDuration = await _processor.ProbeDurationAsync(
                        ffmpegPath,
                        processingInputPath,
                        _processingCancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                {
                    completedExternalStages++;
                    item.Status = "AI 失敗，改用 FFmpeg";
                    RecordAiFallback("DeepFilterNet", item.FileName, ex);
                    StatusText = $"DeepFilterNet 失敗，試聽改用 FFmpeg：{item.FileName}";
                    progressWindow.SetProgress($"AI 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                }
            }

            if (EnableVoiceFixerDereverb)
            {
                try
                {
                    var stageStart = GetExternalStageFraction(completedExternalStages, externalStageCount);
                    var stageEnd = GetExternalStageFraction(completedExternalStages + 1, externalStageCount);
                    processingInputPath = await ProcessWithVoiceFixerAsync(
                        ffmpegPath,
                        processingInputPath,
                        item.FileName,
                        previewTempDirectory!,
                        progressWindow,
                        stageStart * 100,
                        stageEnd * 100,
                        effectiveDuration,
                        trimStart,
                        trimEnd,
                        _processingCancellation.Token);

                    completedExternalStages++;
                    trimStart = null;
                    trimEnd = null;
                    effectiveDuration = await _processor.ProbeDurationAsync(
                        ffmpegPath,
                        processingInputPath,
                        _processingCancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && FallbackToFfmpegWhenAiFails)
                {
                    completedExternalStages++;
                    item.Status = "VoiceFixer 失敗，改用 FFmpeg";
                    RecordAiFallback("VoiceFixer", item.FileName, ex);
                    StatusText = $"VoiceFixer 失敗，試聽改用 FFmpeg：{item.FileName}";
                    progressWindow.SetProgress($"VoiceFixer 失敗，改用 FFmpeg：{item.FileName}", ProcessingProgress);
                }
            }

            var options = new AudioProcessingOptions(
                SilenceSeconds,
                SilenceThresholdDb,
                EnableDenoise,
                ReduceRoomTone,
                ReduceReverb,
                ReverbReductionAmount,
                EnhanceVoiceEq,
                NormalizeLoudness,
                EnableLimiter,
                VolumeGainDb,
                SelectedChannelMode,
                StereoBalance,
                previewFolder,
                DenoiseReductionDb, RemoveSilence, LoudnessTarget, SilencePaddingSeconds, EnableThreeBandEq, LowEqDb, MidEqDb, HighEqDb, EnableDeEsser, DeEsserAmount, EnableAdvancedCleanup ? _cleanupTuning : null, _voiceTuning);

            var outputPath = FfmpegAudioProcessor.MakeOutputPath(
                item.FilePath,
                previewFolder,
                $"{Path.GetFileNameWithoutExtension(item.FileName)}_preview",
                SelectedOutputFormat.Extension);

            var progress = new Progress<AudioProcessingProgress>(current =>
            {
                var ffmpegStart = GetFfmpegStartFraction(externalStageCount);
                var percent = (ffmpegStart + (current.Percent * (1 - ffmpegStart))) * 100;
                ProcessingProgress = Math.Clamp(percent, 0, 100);
                ProcessingProgressText = $"{ProcessingProgress:0}%";
                progressWindow.SetProgress($"{current.Stage ?? "FFmpeg 試聽輸出"}：{item.FileName}", ProcessingProgress);
                item.Status = $"{ProcessingProgress:0}%";
                StatusText = $"{current.Stage ?? "FFmpeg 試聽輸出"}：{item.FileName}";
            });

            await _processor.ProcessAsync(
                ffmpegPath,
                processingInputPath,
                outputPath,
                AvoidRepeatedDenoise(options),
                effectiveDuration,
                progress,
                cancellationToken: _processingCancellation.Token,
                audioCodec: SelectedOutputFormat.AudioCodec,
                trimStart: trimStart,
                trimEnd: trimEnd);

            item.ProcessedPath = outputPath;
            RecordCompletedProcessingTool("FFmpeg");
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
            DesktopMessageBox.Show(this, ex.Message, "試聽失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            item.IsProcessing = false;
            IsProcessing = false;
            _processingCancellation?.Dispose();
            _processingCancellation = null;
            CurrentProcessingFileText = string.Empty;
            IsProgressVisible = false;
            progressWindow.CompleteAndClose();

            if (previewTempDirectory is not null)
            {
                TryDeleteDirectory(previewTempDirectory);
            }
            FinishPendingClose();
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
        => TryGetTrimRangeForDuration(item, item.Duration, out trimStart, out trimEnd, out error);

    private static bool TryGetTrimRangeForDuration(
        AudioFileItem item,
        TimeSpan? sourceDuration,
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

        if (trimEnd is not null && trimEnd <= (trimStart ?? TimeSpan.Zero))
        {
            error = "結束時間必須大於開始時間。";
            return false;
        }

        if (sourceDuration is not null && trimStart is not null && trimStart >= sourceDuration)
        {
            error = "開始時間不能大於或等於音檔長度。";
            return false;
        }

        if (sourceDuration is not null && trimEnd is not null && trimEnd > sourceDuration)
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
            return TryCreateTime(seconds, out value);
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

        if (hours < 0 || minutes < 0 || (parts.Length == 3 && minutes >= 60)
            || !double.IsFinite(parsedSeconds) || parsedSeconds < 0 || parsedSeconds >= 60)
        {
            return false;
        }
        return TryCreateTime(hours * 3600d + minutes * 60d + parsedSeconds, out value);
    }

    private static bool TryCreateTime(double seconds, out TimeSpan? value)
    {
        value = null;
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            return false;
        }
        value = TimeSpan.FromSeconds(seconds);
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
        var result = await ProcessAiChannelsAsync(ffmpegPath, inputPath, tempDirectory,
            startOverallPercent, completeOverallPercent, cancellationToken,
            (channelMode, start, end) => ProcessDeepFilterNetChannelAsync(ffmpegPath, inputPath,
                displayName, tempDirectory, progressWindow, start, end, duration,
                trimStart, trimEnd, cancellationToken, channelMode));
        _aiDenoiseApplied = true;
        RecordCompletedProcessingTool("DeepFilterNet");
        return result;
    }

    private async Task<string> ProcessDeepFilterNetChannelAsync(
        string ffmpegPath, string inputPath, string displayName, string tempDirectory,
        ProcessingProgressWindow progressWindow, double startOverallPercent,
        double completeOverallPercent, TimeSpan? duration, TimeSpan? trimStart,
        TimeSpan? trimEnd, CancellationToken cancellationToken, AudioChannelMode channelMode)
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
            trimEnd,
            channelMode);

        StatusText = $"DeepFilterNet AI 降噪：{displayName}";
        ProcessingProgress = Math.Clamp(startOverallPercent + (totalRange * 0.35d), 0, 100);
        ProcessingProgressText = "AI 處理中";
        progressWindow.SetIndeterminate("AI 降噪中", displayName);

        var deepFilteredPath = await _deepFilterNetProcessor.ProcessAsync(
            _deepFilterNetPath,
            preparedInputPath,
            outputDirectory,
            EnableDeepFilterNetPostFilter,
            cancellationToken,
            AiAttenuationLimitDb);

        ProcessingProgress = Math.Clamp(completeOverallPercent, 0, 100);
        ProcessingProgressText = $"{ProcessingProgress:0}%";
        progressWindow.SetProgress($"AI 降噪完成：{displayName}", ProcessingProgress);
        return deepFilteredPath;
    }

    private async Task<string> ProcessWithVoiceFixerAsync(
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
        var result = await ProcessAiChannelsAsync(ffmpegPath, inputPath, tempDirectory,
            startOverallPercent, completeOverallPercent, cancellationToken,
            (channelMode, start, end) => ProcessVoiceFixerChannelAsync(ffmpegPath, inputPath,
                displayName, tempDirectory, progressWindow, start, end, duration,
                trimStart, trimEnd, cancellationToken, channelMode));
        _aiDenoiseApplied = _aiDenoiseApplied || VoiceFixerMix > 0;
        if (VoiceFixerMix > 0) RecordCompletedProcessingTool("VoiceFixer");
        return result;
    }

    private async Task<string> ProcessVoiceFixerChannelAsync(
        string ffmpegPath, string inputPath, string displayName, string tempDirectory,
        ProcessingProgressWindow progressWindow, double startOverallPercent,
        double completeOverallPercent, TimeSpan? duration, TimeSpan? trimStart,
        TimeSpan? trimEnd, CancellationToken cancellationToken, AudioChannelMode channelMode)
    {
        if (string.IsNullOrWhiteSpace(_voiceFixerPath) || !File.Exists(_voiceFixerPath))
        {
            throw new InvalidOperationException("已勾選 VoiceFixer 免費去殘響，但找不到 voicefixer.exe / voicefixer.cmd。");
        }

        var inputDirectory = Path.Combine(tempDirectory, "voicefixer_input");
        var outputDirectory = Path.Combine(tempDirectory, "voicefixer_output");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var baseName = Path.GetFileNameWithoutExtension(displayName);
        var preparedInputPath = Path.Combine(inputDirectory, $"{baseName}_{Guid.NewGuid():N}.wav");
        var outputPath = Path.Combine(outputDirectory, $"{baseName}_{Guid.NewGuid():N}_voicefixer.wav");
        var totalRange = Math.Max(completeOverallPercent - startOverallPercent, 0);

        StatusText = $"VoiceFixer 準備：{displayName}";
        CurrentProcessingFileText = displayName;

        var prepareProgress = new Progress<AudioProcessingProgress>(current =>
        {
            var overallPercent = Math.Clamp(startOverallPercent + (totalRange * 0.35d * current.Percent), 0, 100);
            ProcessingProgress = overallPercent;
            ProcessingProgressText = $"{overallPercent:0}%";
            progressWindow.SetProgress($"準備 VoiceFixer 音檔：{displayName}", overallPercent);
        });

        await _processor.PrepareVoiceFixerInputAsync(
            ffmpegPath,
            inputPath,
            preparedInputPath,
            duration,
            prepareProgress,
            cancellationToken,
            trimStart,
            trimEnd,
            channelMode);

        if (VoiceFixerMix <= 0) return preparedInputPath;
        StatusText = $"VoiceFixer 人聲修復：{displayName}";
        ProcessingProgress = Math.Clamp(startOverallPercent + (totalRange * 0.35d), 0, 100);
        ProcessingProgressText = "VoiceFixer 處理中";
        progressWindow.SetIndeterminate($"VoiceFixer 模式 {VoiceFixerMode} 處理中", displayName);

        var fixedPath = await _voiceFixerProcessor.ProcessAsync(
            _voiceFixerPath,
            preparedInputPath,
            outputPath,
            VoiceFixerMode,
            cancellationToken);

        if (VoiceFixerMix < 1)
        {
            progressWindow.SetIndeterminate("對齊與混合原音", displayName);
            var mixedPath = Path.Combine(outputDirectory, $"{baseName}_{Guid.NewGuid():N}_blend.wav");
            var blend = await _audioBlendProcessor.BlendAsync(ffmpegPath, preparedInputPath,
                fixedPath, mixedPath, VoiceFixerMix, cancellationToken);
            fixedPath = blend.Path;
            if (blend.Warning is not null)
            {
                RecordProcessingWarning("VoiceFixer", displayName, blend.Warning);
            }
        }

        ProcessingProgress = Math.Clamp(completeOverallPercent, 0, 100);
        ProcessingProgressText = $"{ProcessingProgress:0}%";
        progressWindow.SetProgress($"VoiceFixer 完成：{displayName}", ProcessingProgress);
        return fixedPath;
    }

    private async Task<string> ProcessAiChannelsAsync(
        string ffmpegPath, string inputPath, string tempDirectory,
        double startPercent, double endPercent, CancellationToken cancellationToken,
        Func<AudioChannelMode, double, double, Task<string>> processChannel)
    {
        var mode = SelectedChannelMode;
        if (mode.Value is not ("keep" or "balance" or "swap"))
        {
            return await processChannel(mode, startPercent, endPercent);
        }

        var channels = await _processor.ProbeAudioChannelsAsync(ffmpegPath, inputPath, cancellationToken);
        if (channels == 1)
        {
            return await processChannel(FfmpegAudioProcessor.KeepOriginalChannelMode, startPercent, endPercent);
        }
        if (channels != 2)
        {
            throw new InvalidOperationException("AI 保留聲道目前支援 Mono / Stereo，請先選擇左右混合置中處理多聲道音檔。");
        }

        var middlePercent = (startPercent + endPercent) / 2;
        var left = await processChannel(new AudioChannelMode("左聲道", "left_only"), startPercent, middlePercent);
        var right = await processChannel(new AudioChannelMode("右聲道", "right_only"), middlePercent, endPercent);
        var outputPath = Path.Combine(tempDirectory, $"stereo_{Guid.NewGuid():N}.wav");
        await _processor.RestoreStereoAsync(ffmpegPath, left, right, outputPath, cancellationToken);
        return outputPath;
    }

    private int CountExternalPreprocessStages()
    {
        var count = 0;
        if (EnableDeepFilterNet)
        {
            count++;
        }

        if (EnableVoiceFixerDereverb)
        {
            count++;
        }

        return count;
    }

    private static double GetFfmpegStartFraction(int externalStageCount)
    {
        return externalStageCount switch
        {
            0 => 0,
            1 => 0.45,
            _ => 0.65
        };
    }

    private static double GetExternalStageFraction(int completedStageCount, int externalStageCount)
    {
        if (externalStageCount <= 0)
        {
            return 0;
        }

        return GetFfmpegStartFraction(externalStageCount) * completedStageCount / externalStageCount;
    }

    private IReadOnlyList<ComparisonPreset> CreateComparisonPresets()
        => CreateComparisonPresetsForOptions(new AudioProcessingOptions(
            SilenceSeconds, SilenceThresholdDb, EnableDenoise, ReduceRoomTone, ReduceReverb,
            ReverbReductionAmount, EnhanceVoiceEq, NormalizeLoudness, EnableLimiter,
            VolumeGainDb, SelectedChannelMode, StereoBalance, _outputFolder ?? string.Empty, DenoiseReductionDb, RemoveSilence, LoudnessTarget, SilencePaddingSeconds, EnableThreeBandEq, LowEqDb, MidEqDb, HighEqDb, EnableDeEsser, DeEsserAmount, EnableAdvancedCleanup ? _cleanupTuning : null, _voiceTuning));

    private static IReadOnlyList<ComparisonPreset> CreateComparisonPresetsForOptions(AudioProcessingOptions currentOptions)
    {
        return
        [
            new(
                "原音（只剪輯）",
                "ab_original",
                CreateProcessingOptions(0.1, -35, false, false, false,
                    FfmpegAudioProcessor.DefaultReverbReductionAmount, false, false, false, 0,
                    FfmpegAudioProcessor.KeepOriginalChannelMode) with { RemoveSilence = false },
                UseOriginalInput: true),
            new(
                "目前設定",
                "ab_current",
                currentOptions),
            new(
                "一般 Podcast",
                "ab_podcast",
                CreateProcessingOptions(
                    silenceSeconds: 0.3,
                    silenceThresholdDb: -35,
                    enableDenoise: true,
                    reduceRoomTone: true,
                    reduceReverb: false,
                    reverbReductionAmount: FfmpegAudioProcessor.DefaultReverbReductionAmount,
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
                    reduceReverb: true,
                    reverbReductionAmount: 0.8,
                    enhanceVoiceEq: false,
                    normalizeLoudness: true,
                    enableLimiter: true,
                    volumeGainDb: 0)),
            new(
                "空間感抑制",
                "ab_dereverb",
                CreateProcessingOptions(
                    silenceSeconds: 0.3,
                    silenceThresholdDb: -35,
                    enableDenoise: true,
                    reduceRoomTone: true,
                    reduceReverb: true,
                    reverbReductionAmount: 0.6,
                    enhanceVoiceEq: true,
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
                    reduceReverb: false,
                    reverbReductionAmount: FfmpegAudioProcessor.DefaultReverbReductionAmount,
                    enhanceVoiceEq: true,
                    normalizeLoudness: true,
                    enableLimiter: true,
                    volumeGainDb: 1.5))
        ];

        AudioProcessingOptions CreateProcessingOptions(
            double silenceSeconds,
            double silenceThresholdDb,
            bool enableDenoise,
            bool reduceRoomTone,
            bool reduceReverb,
            double reverbReductionAmount,
            bool enhanceVoiceEq,
            bool normalizeLoudness,
            bool enableLimiter,
            double volumeGainDb,
            AudioChannelMode? channelMode = null)
        {
            return new AudioProcessingOptions(
                silenceSeconds,
                silenceThresholdDb,
                enableDenoise,
                reduceRoomTone,
                reduceReverb,
                reverbReductionAmount,
                enhanceVoiceEq,
                normalizeLoudness,
                enableLimiter,
                volumeGainDb,
                channelMode ?? FfmpegAudioProcessor.DefaultPodcastChannelMode,
                0,
                currentOptions.OutputFolder,
                currentOptions.DenoiseReductionDb);
        }
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

        DesktopMessageBox.Show(this, message.ToString(), "A/B 比較", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task<string> EnsureOutputEncoderAsync(string ffmpegPath, ProcessingProgressWindow progressWindow,
        CancellationToken cancellationToken)
    {
        progressWindow.SetIndeterminate("檢查輸出格式", SelectedOutputFormat.DisplayName);
        var encoder = SelectedOutputFormat.AudioCodec;
        if (await _processor.HasAudioEncoderAsync(ffmpegPath, encoder, cancellationToken)) return ffmpegPath;
        var bundled = FfmpegAudioProcessor.TryFindBundledFfmpeg();
        if (bundled is not null && !string.Equals(Path.GetFullPath(ffmpegPath), Path.GetFullPath(bundled), StringComparison.OrdinalIgnoreCase)
            && await _processor.HasAudioEncoderAsync(bundled, encoder, cancellationToken))
        {
            _ffmpegPath = bundled;
            OnPropertyChanged(nameof(FfmpegPathText));
            RecordProcessingWarning("FFmpeg", "輸出工具", $"原指定工具不支援 {encoder}，本次改用專案 FFmpeg：{bundled}");
            return bundled;
        }
        throw new InvalidOperationException($"目前 FFmpeg 不支援 {SelectedOutputFormat.DisplayName}（{encoder}）。請指定支援此格式的 FFmpeg。");
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

        if (EnableVoiceFixerDereverb && VoiceFixerMix > 0
            && (string.IsNullOrWhiteSpace(_voiceFixerPath) || !File.Exists(_voiceFixerPath)))
        {
            errors.Add("已勾選 VoiceFixer 免費去殘響，但找不到 voicefixer.exe / voicefixer.cmd。");
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

            if (!TryGetTrimRangeForDuration(item, UseAiProcessedAudio ? null : item.Duration,
                out _, out _, out var trimError))
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

        DesktopMessageBox.Show(
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
        if (_processingWarnings.Count > 0)
        {
            message.AppendLine().AppendLine("AI 處理警告（已使用備援流程）：");
            foreach (var warning in _processingWarnings.Take(5))
            {
                message.AppendLine(warning);
            }
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

        DesktopMessageBox.Show(this, message.ToString(), "處理摘要", MessageBoxButton.OK, icon);
    }

    private static string FormatAnalysisText(string label, string path, AudioQualityAnalysis analysis)
    {
        var peakWarning = analysis.MaxVolumeDb is >= -0.2
            ? "，接近爆音"
            : string.Empty;

        return new StringBuilder()
            .AppendLine($"{label}：{Path.GetFileName(path)}")
            .AppendLine($"長度：{FormatNullableDuration(analysis.Duration)}")
            .AppendLine($"平均音量：{FormatDb(analysis.MeanVolumeDb)}")
            .AppendLine($"Peak：{FormatDb(analysis.MaxVolumeDb)}{peakWarning}")
            .AppendLine($"LUFS：{FormatLufs(analysis.IntegratedLufs)}")
            .AppendLine($"True Peak：{FormatDbfs(analysis.TruePeakDbfs)}")
            .ToString()
            .TrimEnd();
    }

    private static string FormatNullableDuration(TimeSpan? duration)
    {
        return duration is null ? "無法判定" : FormatTime(duration.Value);
    }

    private static string FormatDb(double? value)
    {
        return value is null ? "無法判定" : $"{value.Value:0.0} dB";
    }

    private static string FormatDbfs(double? value)
    {
        return value is null ? "無法判定" : $"{value.Value:0.0} dBFS";
    }

    private static string FormatLufs(double? value)
    {
        return value is null ? "無法判定" : $"{value.Value:0.0} LUFS";
    }

    private void AudioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _waveformCancellation?.Cancel();
        if (_selectedFile is not null) _selectedFile.PropertyChanged -= SelectedWaveformFile_Changed;
        _selectedFile = AudioList.SelectedItem as AudioFileItem;
        if (_selectedFile is not null) _selectedFile.PropertyChanged += SelectedWaveformFile_Changed;
        OriginalWaveform.SetWaveform(null);
        _waveformPosition = 0;
        WaveformText = "選取音檔後，按「讀取原音波形」。暗色區域不會保留。";
        OnPropertyChanged(nameof(SelectedFileText));
        if (PlaybackSourceIndex != 2) OpenSelectedForPlayback(autoPlay: false);
    }

    private async Task AnalyzeAudioAsync(AudioFileItem item, string path, string label)
    {
        if (!CanStartWork)
        {
            StatusText = "目前正在分析音檔。";
            return;
        }

        var ffmpegPath = _ffmpegPath;
        if (ffmpegPath is null || !File.Exists(ffmpegPath))
        {
            StatusText = "請先指定 ffmpeg.exe。";
            return;
        }

        if (!File.Exists(path))
        {
            StatusText = "找不到要分析的音檔。";
            return;
        }

        try
        {
            IsAnalyzing = true;
            _analysisCancellation = new CancellationTokenSource();
            AnalysisText = $"正在分析 {label}：{Path.GetFileName(path)}";
            StatusText = $"正在分析：{Path.GetFileName(path)}";

            var progressWindow = new ProcessingProgressWindow
            {
                Owner = this
            };
            progressWindow.CancelRequested += (_, _) => _analysisCancellation?.Cancel();
            progressWindow.Show();

            var progress = new Progress<AudioAnalysisProgress>(current =>
            {
                var percent = Math.Clamp(current.Percent * 100, 0, 100);
                ProcessingProgress = percent;
                ProcessingProgressText = current.Percent is > 0.05 and < 1
                    ? "分析中"
                    : $"{percent:0}%";
                if (current.Percent is > 0.05 and < 1)
                {
                    progressWindow.SetIndeterminate(current.Stage, Path.GetFileName(path));
                }
                else
                {
                    progressWindow.SetProgress($"{current.Stage}：{Path.GetFileName(path)}", percent);
                }

                StatusText = $"{current.Stage}：{Path.GetFileName(path)}";
            });

            var duration = string.Equals(path, item.FilePath, StringComparison.OrdinalIgnoreCase)
                ? item.Duration
                : null;

            try
            {
                IsProgressVisible = true;
                ProcessingProgress = 0;
                ProcessingProgressText = "0%";
                CurrentProcessingFileText = Path.GetFileName(path);

                var analysis = await _processor.AnalyzeQualityAsync(
                    ffmpegPath,
                    path,
                    duration,
                    progress,
                    _analysisCancellation.Token);
                AnalysisText = FormatAnalysisText(label, path, analysis);
                StatusText = $"分析完成：{Path.GetFileName(path)}";
            }
            finally
            {
                progressWindow.CompleteAndClose();
            }
        }
        catch (OperationCanceledException)
        {
            AnalysisText = "分析已取消。";
            StatusText = "分析已取消。";
        }
        catch (Exception ex)
        {
            AnalysisText = $"分析失敗：{ex.Message}";
            StatusText = $"分析失敗：{ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            _analysisCancellation?.Dispose();
            _analysisCancellation = null;
            CurrentProcessingFileText = string.Empty;
            IsProgressVisible = false;
            FinishPendingClose();
        }
    }

    private void FinishPendingClose()
    {
        if (_closeAfterWork && CanStartWork)
        {
            #if CROSS_PLATFORM
            Dispatcher.UIThread.Post(Close);
#else
            Dispatcher.BeginInvoke(new Action(Close));
#endif
        }
    }

    private void RecordAiFallback(string tool, string fileName, Exception error)
        => RecordProcessingWarning(tool, fileName, $"執行失敗，使用目前音源接續 FFmpeg 後製。{error.Message}");

    private void RecordProcessingWarning(string tool, string fileName, string message)
    {
        _processingWarningCount++;
        var warning = $"{tool} / {fileName}：{message}";
        if (_processingWarnings.Count < 20)
        {
            _processingWarnings.Add(warning);
        }
        if (_processingReportItem is not null)
            _processingReportItem.ProcessingWarningsText = warning;
        NotifyProcessingReportChanged();
    }

    private void RecordCompletedProcessingTool(string tool)
    {
        if (!_completedProcessingTools.Contains(tool)) _completedProcessingTools.Add(tool);
        if (_processingReportItem is not null)
            _processingReportItem.ProcessingToolsText = "已完成階段：" + string.Join(" → ", _completedProcessingTools);
        OnPropertyChanged(nameof(ProcessingToolsText));
    }

    private void NotifyProcessingReportChanged()
    {
        OnPropertyChanged(nameof(ProcessingWarningsText));
        OnPropertyChanged(nameof(HasProcessingWarnings));
        OnPropertyChanged(nameof(ProcessingWarningSummary));
        OnPropertyChanged(nameof(ProcessingToolsText));
    }

    private string GetProcessingInputPath(AudioFileItem item)
    {
        _aiDenoiseApplied = false;
        _processingReportItem = item;
        _completedProcessingTools.Clear();
        item.ProcessingWarningsText = string.Empty;
        item.ProcessingToolsText = string.Empty;
        if (!UseAiProcessedAudio)
        {
            RecordCompletedProcessingTool("原始音檔");
            return item.FilePath;
        }

        var aiPath = FfmpegAudioProcessor.FindAiProcessedAudio(item.FilePath, _aiAudioFolder);
        RecordCompletedProcessingTool(aiPath is null ? "原始音檔" : "外部 AI 音檔");
        if (aiPath is null)
            RecordProcessingWarning("外部 AI 配對", item.FileName, "找不到對應 AI 音檔，改用原始音檔。");
        return aiPath ?? item.FilePath;
    }

    private async Task<TimeSpan?> GetProcessingDurationAsync(string ffmpegPath, AudioFileItem item,
        string inputPath, CancellationToken cancellationToken)
    {
        var duration = await _processor.ProbeDurationAsync(ffmpegPath, inputPath, cancellationToken);
        var isOriginal = string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(item.FilePath),
            StringComparison.OrdinalIgnoreCase);
        if (!isOriginal) return duration;
        item.Duration = duration ?? item.Duration;
        return item.Duration;
    }

    private AudioProcessingOptions AvoidRepeatedDenoise(AudioProcessingOptions options)
        => options with { EnableDenoise = options.EnableDenoise && !_aiDenoiseApplied };

    private PortablePreset CapturePortablePreset()
    {
        var channel = SelectedChannelMode.Value switch { "keep" => "source", "mono" => "mono", "left_mono" => "left", "right_mono" => "right", "swap" => "swap", _ => throw new InvalidDataException("共用預設支援保留、Mono、左／右 Mono 或左右互換。請先切換聲道模式；置中與手動平衡無法等效交換。") };
        var core = new PortableCore(VolumeGainDb, NormalizeLoudness, LoudnessTarget, RemoveSilence, SilenceThresholdDb, SilenceSeconds, SilencePaddingSeconds, channel, new PortableTone(EnableThreeBandEq, LowEqDb, MidEqDb, HighEqDb, EnableDeEsser, DeEsserAmount));
        PortablePresetFile.Validate(core);
        var native = JsonSerializer.SerializeToElement(new { EnableDenoise, DenoiseStrengthIndex, ReduceRoomTone, ReduceReverb, ReverbReductionAmount, EnhanceVoiceEq, EnableLimiter, EnableAdvancedCleanup, CleanupTuning = _cleanupTuning, VoiceTuning = _voiceTuning });
        return new PortablePreset(PresetName.Trim(), core, new() { ["desktop"] = native });
    }
    private void SavePortablePreset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PortablePresets.Count >= 100) throw new InvalidDataException("最多保存 100 組預設。");
            var preset = CapturePortablePreset();
            PortablePresetFile.Serialize([preset]);
            PortablePresets.Add(preset); SelectedPortablePreset = preset; SaveSettings();
            StatusText = "已保存共用預設（不包含 AI 工具、路徑與剪輯範圍）。";
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { StatusText = ex.Message; }
    }
    private void ApplyPortablePreset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPortablePreset is not { } p) return;
        var cleanup = new AudioCleanupTuning();
        var voice = new VoiceToneTuning();
        var advancedCleanup = false;
        try
        {
            if (p.Extensions?.TryGetValue("desktop", out var extension) == true)
            {
                if (extension.ValueKind != JsonValueKind.Object) throw new InvalidDataException("桌面預設內容不正確。");
                if (extension.TryGetProperty("CleanupTuning", out var tuning) && tuning.ValueKind != JsonValueKind.Null)
                    cleanup = tuning.Deserialize<AudioCleanupTuning>() ?? throw new InvalidDataException("清理參數缺少內容。");
                cleanup.Validate();
                if (extension.TryGetProperty("VoiceTuning", out var voiceJson) && voiceJson.ValueKind != JsonValueKind.Null)
                    voice = voiceJson.Deserialize<VoiceToneTuning>() ?? throw new InvalidDataException("人聲參數缺少內容。");
                voice.Validate();
                advancedCleanup = extension.TryGetProperty("EnableAdvancedCleanup", out var enabled) && enabled.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { StatusText = $"預設未套用：{ex.Message}"; return; }
        var c = p.Core;
        ApplyCleanupTuning(cleanup); EnableAdvancedCleanup = advancedCleanup; ApplyVoiceTuning(voice);
        VolumeGainDb = c.GainDb; NormalizeLoudness = c.LoudnessEnabled; LoudnessTarget = c.LoudnessTarget;
        RemoveSilence = c.SilenceTrimEnabled; SilenceSeconds = c.MinimumSilenceSeconds; SilenceThresholdDb = c.SilenceThresholdDb; SilencePaddingSeconds = c.SilencePaddingSeconds;
        var mode = c.ChannelMode switch { "source" => "keep", "left" => "left_mono", "right" => "right_mono", _ => c.ChannelMode };
        SelectedChannelMode = ChannelModes.First(m => m.Value == mode);
        EnableDenoise = ReduceRoomTone = ReduceReverb = EnhanceVoiceEq = false;
        UseAiProcessedAudio = EnableDeepFilterNet = EnableVoiceFixerDereverb = false;
        EnableLimiter = true;
        if (p.Extensions?.TryGetValue("desktop", out var native) == true)
        {
            bool Flag(string key) => native.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
            EnableDenoise = Flag("EnableDenoise"); ReduceRoomTone = Flag("ReduceRoomTone"); ReduceReverb = Flag("ReduceReverb"); EnhanceVoiceEq = Flag("EnhanceVoiceEq"); EnableLimiter = Flag("EnableLimiter");
            if (native.TryGetProperty("DenoiseStrengthIndex", out var strength) && strength.TryGetInt32(out var n)) DenoiseStrengthIndex = n;
            if (native.TryGetProperty("ReverbReductionAmount", out var reverb) && reverb.TryGetDouble(out var amount) && double.IsFinite(amount)) ReverbReductionAmount = amount;
        }
        EnableThreeBandEq = c.Tone?.EqEnabled ?? false;
        LowEqDb = c.Tone?.LowEqDb ?? 0; MidEqDb = c.Tone?.MidEqDb ?? 0; HighEqDb = c.Tone?.HighEqDb ?? 0;
        EnableDeEsser = c.Tone?.DeEsserEnabled ?? false; DeEsserAmount = c.Tone?.DeEsserAmount ?? 45;
        PresetName = p.Name;
        StatusText = p.Extensions?.ContainsKey("soundlab") == true
            ? "已套用共用核心設定；SoundLab 人聲 EQ、降噪、修復未套用；共用三頻 EQ／齒音已按預設套用，桌面額外效果及 AI 已停用。防爆音預設開啟。"
            : "已套用預設；AI、工具路徑與剪輯範圍不包含於共用預設。";
    }
    private void DeletePortablePreset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPortablePreset is { } p) { PortablePresets.Remove(p); SelectedPortablePreset = null; SaveSettings(); }
    }
#if !CROSS_PLATFORM
    private void ImportPortablePresets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "共用預設 JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new InvalidDataException("預設檔案不可超過 1 MB。");
            var presets = PortablePresetFile.Parse(File.ReadAllText(dialog.FileName));
            if (PortablePresets.Count + presets.Count > 100) throw new InvalidDataException("匯入後不得超過 100 組預設。");
            foreach (var p in presets) PortablePresets.Add(p);
            SelectedPortablePreset = presets[0]; SaveSettings();
            StatusText = $"已匯入 {presets.Count} 組預設，按「套用」載入；其他工具專用效果不會等效轉換。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { StatusText = ex.Message; }
    }
#endif
#if !CROSS_PLATFORM
    private void ExportPortablePresets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "共用預設 JSON|*.json", FileName = "AudioStudio_預設.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, PortablePresetFile.Serialize(PortablePresets.Count > 0 ? PortablePresets : [CapturePortablePreset()]));
            StatusText = "已匯出共用預設，可在 SoundLab 匯入。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { StatusText = ex.Message; }
    }

#endif
    private string _workSessionText = "尚未保存工作檔。";
    public string WorkSessionText { get => _workSessionText; private set => SetField(ref _workSessionText, value); }
    private string? _lastMergedOutputPath;
    private double? _pendingEpisodeSeek;
    public ObservableCollection<EpisodeChapter> EpisodeChapters { get; } = [];
    public string EpisodePreviewText => _lastMergedOutputPath is null ? "尚未產生整集成品。" : $"上次整集：{Path.GetFileName(_lastMergedOutputPath)}";
    public bool CanPlayEpisode => _lastMergedOutputPath is not null && File.Exists(_lastMergedOutputPath);
    public bool CanReviewEpisode => CanPlayEpisode && CanStartWork;

#if !CROSS_PLATFORM
    private void SaveWork_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWork) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "音訊工作檔|*.podcast-work.json", FileName = "episode.podcast-work.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { SaveWorkSession(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { WorkSessionText = $"保存工作檔失敗：{ex.Message}"; StatusText = WorkSessionText; }
    }
#endif
#if !CROSS_PLATFORM
    private void LoadWork_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWork) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "音訊工作檔|*.podcast-work.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { LoadWorkSession(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { WorkSessionText = $"恢復工作檔失敗：{ex.Message}"; StatusText = WorkSessionText; }
    }
#endif
    private static string? WorkPath(string? path, string folder, bool save) => string.IsNullOrWhiteSpace(path) ? null
        : save ? AudioWorkSessionFile.RelativePath(path, folder) : AudioWorkSessionFile.ResolvePath(path, folder);
    private static void ConvertWorkPaths(AppSettings settings, string folder, bool save)
    {
        settings.CurrentFolder = WorkPath(settings.CurrentFolder, folder, save);
        settings.OutputFolder = WorkPath(settings.OutputFolder, folder, save);
        settings.AiAudioFolder = WorkPath(settings.AiAudioFolder, folder, save);
        settings.IntroAudioPath = WorkPath(settings.IntroAudioPath, folder, save);
        settings.OutroAudioPath = WorkPath(settings.OutroAudioPath, folder, save);
        settings.CoverImagePath = WorkPath(settings.CoverImagePath, folder, save);
    }
    private void SaveWorkSession(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var settings = CaptureAppSettings();
        settings.FfmpegPath = settings.DeepFilterNetPath = settings.VoiceFixerPath = null;
        settings.PortablePresets = null;
        ConvertWorkPaths(settings, folder, true);
        var files = AudioFiles.Select(item => new WorkAudioFile(AudioWorkSessionFile.RelativePath(item.FilePath, folder),
            item.CustomOutputFileName, item.TrimStartText, item.TrimEndText)).ToList();
        AudioWorkSessionFile.Write(path, new AudioWorkSession(AudioWorkSessionFile.Format, 1, JsonSerializer.SerializeToElement(settings), files));
        WorkSessionText = $"已保存工作檔：{Path.GetFileName(path)}（{files.Count} 個音檔；只保存路徑與設定）";
        StatusText = $"工作已保存：{Path.GetFileName(path)}";
    }
    private void LoadWorkSession(string path)
    {
        var work = AudioWorkSessionFile.Read(path);
        var fields = new HashSet<string>();
        foreach (var property in work.Settings.EnumerateObject())
        {
            var definition = typeof(AppSettings).GetProperty(property.Name);
            if (definition is null || !fields.Add(property.Name) || (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length > 32768) || (property.Value.ValueKind == JsonValueKind.Number &&
                (!property.Value.TryGetDouble(out var number) || !double.IsFinite(number))))
                throw new InvalidDataException("工作檔設定欄位不正確。");
            if (property.Name == nameof(AppSettings.PortablePresets) && property.Value.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("工作檔不包含預設資料庫。");
        }
        if (new[] { "OutputFormatExtension", "ChannelModeValue", "VolumeGainDb", "LoudnessTarget", "MergeSegments", "MergeGapSeconds", "SilenceSeconds", "SilenceThresholdDb" }.Any(key => !fields.Contains(key)))
            throw new InvalidDataException("工作檔缺少必要的處理設定。");
        var settings = work.Settings.Deserialize<AppSettings>() ?? throw new InvalidDataException("工作檔設定不正確。");
        settings.CleanupTuning?.Validate();
        settings.VoiceTuning?.Validate();
        if (!FfmpegAudioProcessor.OutputFormats.Any(format => format.Extension == settings.OutputFormatExtension) ||
            !FfmpegAudioProcessor.ChannelModes.Any(mode => mode.Value == settings.ChannelModeValue))
            throw new InvalidDataException("工作檔輸出格式或聲道模式不支援。");
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        ConvertWorkPaths(settings, folder, false);
        var restored = work.Files.Select(file => new AudioFileItem
        {
            FilePath = AudioWorkSessionFile.ResolvePath(file.FilePath, folder), CustomOutputFileName = file.CustomOutputFileName,
            TrimStartText = file.TrimStartText, TrimEndText = file.TrimEndText
        }).ToList();
        // Keep executable paths on this machine; project files only restore editing choices.
        settings.FfmpegPath = _ffmpegPath; settings.DeepFilterNetPath = _deepFilterNetPath; settings.VoiceFixerPath = _voiceFixerPath;
        settings.PortablePresets = null;
        StopPlayback(); _player.Close(); _waveformCancellation?.Cancel();
        _durationLoadingCancellation?.Cancel();
        PlaybackSourceIndex = 0;
        ApplyAppSettings(settings);
        _loadedDeepFilterNetPreference = true;
        _lastMergedOutputPath = null; EpisodeChapters.Clear();
        OnPropertyChanged(nameof(EpisodePreviewText)); OnPropertyChanged(nameof(CanPlayEpisode)); OnPropertyChanged(nameof(CanReviewEpisode));
        AudioFiles.Clear();
        var missing = new List<string>();
        foreach (var item in restored)
        {
            if (!File.Exists(item.FilePath)) { item.Status = "來源不存在"; missing.Add(item.FilePath); }
            AudioFiles.Add(item);
        }
        foreach (var asset in new[] { _introAudioPath, _outroAudioPath, _coverImagePath })
            if (!string.IsNullOrWhiteSpace(asset) && !File.Exists(asset)) missing.Add(asset);
        AudioList.SelectedItem = restored.FirstOrDefault(item => File.Exists(item.FilePath));
        OnPropertyChanged(nameof(FileCountText));
        WorkSessionText = $"已恢復 {restored.Count} 個音檔。" + (missing.Count == 0 ? "來源均存在。" :
            $"找不到 {missing.Count} 個來源／附件，請重新指定：{Environment.NewLine}{string.Join(Environment.NewLine, missing.Take(20))}" + (missing.Count > 20 ? $"{Environment.NewLine}其餘 {missing.Count - 20} 項未列出。" : ""));
        StatusText = $"已恢復工作：{Path.GetFileName(path)}，{restored.Count} 個音檔，{missing.Count} 個缺失來源／附件。";
        if (_ffmpegPath is not null && File.Exists(_ffmpegPath)) _ = LoadDurationsAsync(restored.Where(item => File.Exists(item.FilePath)).ToList());
    }
#if !CROSS_PLATFORM
    private void RelinkWorkAudio_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWork || _selectedFile is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "音檔|*.wav;*.mp3;*.m4a;*.flac;*.aac;*.ogg;*.wma|所有檔案|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try { RelinkWorkAudio(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { WorkSessionText = $"重新指定音檔失敗：{ex.Message}"; StatusText = WorkSessionText; }
    }
#endif
    private void RelinkWorkAudio(string path)
    {
        if (_selectedFile is null || !File.Exists(path)) throw new InvalidDataException("找不到替代音檔。");
        var index = AudioFiles.IndexOf(_selectedFile);
        var replacement = new AudioFileItem { FilePath = Path.GetFullPath(path), CustomOutputFileName = _selectedFile.CustomOutputFileName,
            TrimStartText = _selectedFile.TrimStartText, TrimEndText = _selectedFile.TrimEndText };
        AudioFiles[index] = replacement; AudioList.SelectedItem = replacement;
        WorkSessionText = $"已重新指定：{replacement.FileName}，剪輯時間與輸出名稱已保留；請重新保存工作檔。";
    }
    private void PlayEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReviewEpisode) { StatusText = "找不到整集成品，請重新統整輸出。"; return; }
        PlaybackSourceIndex = 2; OpenSelectedForPlayback(true);
    }
    private void EpisodeChapter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: double seconds }) SeekEpisode(seconds);
    }
    private void SeekEpisode(double seconds)
    {
        if (!CanReviewEpisode) { StatusText = "找不到整集成品。"; return; }
        var open = PlaybackSourceIndex != 2 || _player.Source?.LocalPath != _lastMergedOutputPath;
        if (open) { PlaybackSourceIndex = 2; OpenSelectedForPlayback(false); _pendingEpisodeSeek = seconds; }
        PositionSlider.Maximum = Math.Max(1, EpisodeChapters.LastOrDefault()?.EndSeconds ?? seconds);
        PositionSlider.Value = Math.Min(PositionSlider.Maximum, seconds); SeekToSlider();
    }
    private void SetEpisodePreview(string path, IReadOnlyList<EpisodeChapter> chapters)
    {
        _lastMergedOutputPath = path; EpisodeChapters.Clear();
        foreach (var chapter in chapters) EpisodeChapters.Add(chapter);
        OnPropertyChanged(nameof(EpisodePreviewText)); OnPropertyChanged(nameof(CanPlayEpisode)); OnPropertyChanged(nameof(CanReviewEpisode));
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

            ApplyAppSettings(settings);
        }
        catch (InvalidDataException)
        {
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

    private void ApplyAppSettings(AppSettings settings)
    {
            var cleanup = settings.CleanupTuning ?? new AudioCleanupTuning();
            cleanup.Validate();
            settings.VoiceTuning?.Validate();
            ApplyCleanupTuning(cleanup); EnableAdvancedCleanup = settings.EnableAdvancedCleanup;
            ApplyVoiceTuning(settings.VoiceTuning ?? new());
            _currentFolder = settings.CurrentFolder;
            _outputFolder = settings.OutputFolder;
            _aiAudioFolder = settings.AiAudioFolder;
            _ffmpegPath = settings.FfmpegPath;
            _introAudioPath = settings.IntroAudioPath;
            _outroAudioPath = settings.OutroAudioPath;
            _coverImagePath = settings.CoverImagePath;
            _deepFilterNetPath = settings.DeepFilterNetPath;
            _voiceFixerPath = settings.VoiceFixerPath;

            LoudnessTarget = settings.LoudnessTarget;
            RemoveSilence = settings.RemoveSilence;
            SilencePaddingSeconds = settings.SilencePaddingSeconds;
            if (settings.PortablePresets is { Count: > 0 })
                foreach (var preset in PortablePresetFile.Parse(PortablePresetFile.Serialize(settings.PortablePresets))) PortablePresets.Add(preset);
            SilenceSeconds = settings.SilenceSeconds;
            SilenceThresholdDb = settings.SilenceThresholdDb;
            EnableDenoise = settings.EnableDenoise;
            DenoiseStrengthIndex = settings.DenoiseStrengthIndex;
            VoiceFixerMix = settings.VoiceFixerMix;
            MatchComparisonLoudness = settings.MatchComparisonLoudness;
            UseAiProcessedAudio = settings.UseAiProcessedAudio;
            EnableDeepFilterNet = settings.EnableDeepFilterNet;
            EnableDeepFilterNetPostFilter = settings.EnableDeepFilterNetPostFilter;
            FallbackToFfmpegWhenAiFails = settings.FallbackToFfmpegWhenAiFails;
            EnableVoiceFixerDereverb = settings.EnableVoiceFixerDereverb;
            VoiceFixerMode = settings.VoiceFixerMode;
            ReduceRoomTone = settings.ReduceRoomTone;
            ReduceReverb = settings.ReduceReverb;
            ReverbReductionAmount = settings.ReverbReductionAmount <= 0
                ? FfmpegAudioProcessor.DefaultReverbReductionAmount
                : settings.ReverbReductionAmount;
            EnhanceVoiceEq = settings.EnhanceVoiceEq;
            EnableThreeBandEq = settings.EnableThreeBandEq;
            LowEqDb = settings.LowEqDb; MidEqDb = settings.MidEqDb; HighEqDb = settings.HighEqDb;
            EnableDeEsser = settings.EnableDeEsser; DeEsserAmount = settings.DeEsserAmount;
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
            StereoBalance = settings.StereoBalance;

            SelectedOutputFormat = FfmpegAudioProcessor.OutputFormats.FirstOrDefault(format =>
                    string.Equals(format.Extension, settings.OutputFormatExtension, StringComparison.OrdinalIgnoreCase))
                ?? FfmpegAudioProcessor.OutputFormats[0];
            SelectedChannelMode = FfmpegAudioProcessor.ChannelModes.FirstOrDefault(mode =>
                    string.Equals(mode.Value, settings.ChannelModeValue, StringComparison.OrdinalIgnoreCase))
                ?? FfmpegAudioProcessor.DefaultPodcastChannelMode;

            OnPropertyChanged(nameof(CurrentFolderText));
            OnPropertyChanged(nameof(OutputFolderText));
            OnPropertyChanged(nameof(AiAudioFolderText));
            OnPropertyChanged(nameof(FfmpegPathText));
            OnPropertyChanged(nameof(IntroAudioText));
            OnPropertyChanged(nameof(OutroAudioText));
            OnPropertyChanged(nameof(CoverImageText));
            OnPropertyChanged(nameof(DeepFilterNetPathText));
            OnPropertyChanged(nameof(DeepFilterNetStatusText));
            OnPropertyChanged(nameof(VoiceFixerPathText));
            OnPropertyChanged(nameof(VoiceFixerStatusText));
    }

    private void SaveSettings()
    {
#if CROSS_PLATFORM
        if (!_persistUserSettings) return;
#endif
        try
        {
            var settingsPath = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

            var settings = CaptureAppSettings();

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

    private AppSettings CaptureAppSettings()
    {
        return new AppSettings
            {
                CurrentFolder = _currentFolder,
                OutputFolder = _outputFolder,
                AiAudioFolder = _aiAudioFolder,
                FfmpegPath = _ffmpegPath,
                IntroAudioPath = _introAudioPath,
                OutroAudioPath = _outroAudioPath,
                CoverImagePath = _coverImagePath,
                DeepFilterNetPath = _deepFilterNetPath,
                VoiceFixerPath = _voiceFixerPath,
                LoudnessTarget = LoudnessTarget,
                RemoveSilence = RemoveSilence,
                SilencePaddingSeconds = SilencePaddingSeconds,
                PortablePresets = PortablePresets.ToList(),
                SilenceSeconds = SilenceSeconds,
                SilenceThresholdDb = SilenceThresholdDb,
                EnableDenoise = EnableDenoise,
                DenoiseStrengthIndex = DenoiseStrengthIndex,
                EnableAdvancedCleanup = EnableAdvancedCleanup,
                CleanupTuning = _cleanupTuning,
                VoiceTuning = _voiceTuning,
                VoiceFixerMix = VoiceFixerMix,
                MatchComparisonLoudness = MatchComparisonLoudness,
                UseAiProcessedAudio = UseAiProcessedAudio,
                EnableDeepFilterNet = EnableDeepFilterNet,
                EnableDeepFilterNetPostFilter = EnableDeepFilterNetPostFilter,
                FallbackToFfmpegWhenAiFails = FallbackToFfmpegWhenAiFails,
                EnableVoiceFixerDereverb = EnableVoiceFixerDereverb,
                VoiceFixerMode = VoiceFixerMode,
                ReduceRoomTone = ReduceRoomTone,
                ReduceReverb = ReduceReverb,
                ReverbReductionAmount = ReverbReductionAmount,
                EnhanceVoiceEq = EnhanceVoiceEq,
                EnableThreeBandEq = EnableThreeBandEq, LowEqDb = LowEqDb, MidEqDb = MidEqDb, HighEqDb = HighEqDb,
                EnableDeEsser = EnableDeEsser, DeEsserAmount = DeEsserAmount,
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
                VolumeGainDb = VolumeGainDb,
                ChannelModeValue = SelectedChannelMode.Value,
                StereoBalance = StereoBalance
            };
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

#if !CROSS_PLATFORM
    private void AudioList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedForPlayback(autoPlay: true);
    }

#endif
#if !CROSS_PLATFORM
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

#endif
#if !CROSS_PLATFORM
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

#endif
#if !CROSS_PLATFORM
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

#endif
    private async void LoadWaveform_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWork) return;
        var item = _selectedFile;
        if (item is null) { StatusText = "請先選取音檔。"; return; }
        if (_ffmpegPath is null || !File.Exists(_ffmpegPath)) { StatusText = "請先指定 ffmpeg.exe。"; return; }
        using var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        _waveformCancellation = cancellation;
        IsAnalyzing = true;
        WaveformText = "正在讀取完整原音波形，可按取消讀取。";
        try
        {
            var waveform = await _processor.ReadWaveformAsync(_ffmpegPath, item.FilePath, cancellation.Token);
            if (!ReferenceEquals(_selectedFile, item)) return;
            OriginalWaveform.SetWaveform(waveform);
            item.Duration = TimeSpan.FromSeconds(waveform.DurationSeconds);
            UpdateWaveformRange();
            UpdateWaveformPosition();
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_selectedFile, item)) WaveformText = "波形讀取已取消。";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_selectedFile, item)) WaveformText = $"波形讀取失敗：{ex.Message}";
        }
        finally
        {
            _analysisCancellation = null;
            _waveformCancellation = null;
            IsAnalyzing = false;
            FinishPendingClose();
        }
    }

    private void SelectedWaveformFile_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioFileItem.TrimStartText) or nameof(AudioFileItem.TrimEndText))
        { UpdateWaveformRange(); RefreshEffectPreview(); }
    }

    private void UpdateWaveformRange()
    {
        if (_selectedFile is null || OriginalWaveform.Waveform is not { } waveform) return;
        var valid = TryGetTrimRangeForDuration(_selectedFile, TimeSpan.FromSeconds(waveform.DurationSeconds),
            out var start, out var end, out var error);
        OriginalWaveform.SetRange(start?.TotalSeconds ?? 0, end?.TotalSeconds ?? waveform.DurationSeconds, valid);
        WaveformText = valid
            ? $"原音 {FormatTime(TimeSpan.FromSeconds(waveform.DurationSeconds))} · 點擊定位；金線內為保留範圍。方向鍵移動一秒。"
            : $"剪輯範圍無效：{error}";
    }

    private void SeekOriginalWaveform(double seconds)
    {
        if (_selectedFile is null || OriginalWaveform.Waveform is null) return;
        if (PlaybackSourceIndex != 0 || _player.Source is null)
        {
            PlaybackSourceIndex = 0; OpenSelectedForPlayback(false);
            _pendingWaveformSeek = seconds;
        }
        _waveformPosition = seconds;
        PositionSlider.Maximum = Math.Max(1, OriginalWaveform.Waveform.DurationSeconds);
        PositionSlider.Value = seconds;
        _player.Position = TimeSpan.FromSeconds(seconds);
        PlaybackPositionText = FormatTime(TimeSpan.FromSeconds(seconds));
        OriginalWaveform.SetPosition(seconds);
    }

    private void UpdateWaveformPosition()
    {
        if (OriginalWaveform is null) return;
        if (PlaybackSourceIndex == 0) _waveformPosition = PositionSlider.Value;
        OriginalWaveform.SetPosition(_waveformPosition);
    }

    private void SetTrimStartFromWaveform_Click(object sender, RoutedEventArgs e) => SetTrimFromWaveform(true);
    private void SetTrimEndFromWaveform_Click(object sender, RoutedEventArgs e) => SetTrimFromWaveform(false);
    private void SetTrimFromWaveform(bool start)
    {
        if (_selectedFile is null || OriginalWaveform.Waveform is null) { StatusText = "請先讀取原音波形。"; return; }
        // Floor to milliseconds so a cursor at EOF cannot round past the actual source duration.
        var text = (Math.Floor(_waveformPosition * 1000) / 1000).ToString("0.###", CultureInfo.InvariantCulture);
        if (start) _selectedFile.TrimStartText = text; else _selectedFile.TrimEndText = text;
        UpdateWaveformRange();
    }

    private void PlaybackSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenSelectedForPlayback(autoPlay: false);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (PlaybackSourceIndex == 3 && !CanPlayEffectPreview)
        { OpenSelectedForPlayback(false); StatusText = "單項比較尚未就緒或已過期。"; return; }
        if (PlaybackSourceIndex == 2 && !CanReviewEpisode) { StatusText = "整集尚未就緒，或正在處理音檔。"; return; }
        if (PlaybackSourceIndex == 2 && _player.Source?.LocalPath != _lastMergedOutputPath) { OpenSelectedForPlayback(true); return; }
        if (_selectedFile is null && PlaybackSourceIndex != 2)
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

#if !CROSS_PLATFORM
    private void PositionSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingPosition = true;
    }

#endif
#if !CROSS_PLATFORM
    private void PositionSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingPosition = false;
        SeekToSlider();
    }

#endif
    private void PositionSlider_ValueChanged(object sender, PlaybackPositionChangedEventArgs e)
    {
        UpdateWaveformPosition();
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
        if (PlaybackSourceIndex == 0 && _pendingWaveformSeek is { } seek)
        {
            _pendingWaveformSeek = null;
            PositionSlider.Value = Math.Min(duration.TotalSeconds, seek);
            SeekToSlider();
        }
        if (PlaybackSourceIndex == 2 && _pendingEpisodeSeek is { } chapterSeek)
        {
            _pendingEpisodeSeek = null; PositionSlider.Value = Math.Min(duration.TotalSeconds, chapterSeek); SeekToSlider();
        }
        PlaybackDurationText = FormatTime(duration);
        if (PlaybackSourceIndex == 3 && _pendingEffectSeek is { } effectSeek)
        {
            _pendingEffectSeek = null; PositionSlider.Value = Math.Min(duration.TotalSeconds, effectSeek); SeekToSlider();
        }

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
        if (PlaybackSourceIndex == 2 && !CanStartWork) { StatusText = "處理完成後才能驗收整集。"; return; }
        _playbackTimer.Stop();
        _player.Close();
        _pendingWaveformSeek = null;
        _pendingEpisodeSeek = null;
        PositionSlider.Value = 0;
        PositionSlider.Maximum = 1;
        PlaybackPositionText = PlaybackDurationText = "00:00";
        if (_selectedFile is null && PlaybackSourceIndex != 2) return;
        var path = PlaybackSourceIndex == 2 ? (CanPlayEpisode ? _lastMergedOutputPath : null) : GetPlaybackPath(_selectedFile!);
        if (path is null)
        {
            StatusText = "這個音檔還沒有處理後版本。";
            return;
        }
        if (!File.Exists(path))
        {
            StatusText = "找不到播放檔案，請重新指定音檔或重新輸出。";
            return;
        }

        _pendingWaveformSeek = null;
        _pendingEpisodeSeek = null;
        _player.Open(new Uri(path));
        PositionSlider.Value = 0;
        PlaybackPositionText = "00:00";
        PlaybackDurationText = "00:00";
        StatusText = autoPlay ? $"播放：{Path.GetFileName(path)}" : $"已載入：{Path.GetFileName(path)}";

        if (autoPlay)
        {
            _player.Play();
            _playbackTimer.Start();
        }
    }

    private string? GetPlaybackPath(AudioFileItem item)
    {
        if (PlaybackSourceIndex == 3) return CanPlayEffectPreview ? _effectPlaybackPath : null;
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

#if !CROSS_PLATFORM
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

#endif
#if !CROSS_PLATFORM
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

#endif
    private static string FormatTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

#if !CROSS_PLATFORM
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

#endif
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
        if (propertyName is nameof(SelectedFileText) or nameof(FileCountText) or nameof(CanStartWork)
            or nameof(MergeSegments) or nameof(PlaybackSourceIndex) or nameof(CanReviewEpisode) or nameof(ProcessingToolsText))
            RefreshWorkspaceUi();
        if (propertyName is nameof(SelectedFileText) or nameof(CanStartWork) or nameof(SelectedCleanupEffect)
            or nameof(VoiceThicknessDb) or nameof(VoiceClarityDb) or nameof(UseVoiceCompression)
            or nameof(EnableAdvancedCleanup) or nameof(TrackNoise) or nameof(FineDenoiseDb) or nameof(NoiseFloorDb)
            or nameof(RoomHighpassHz) or nameof(RoomFrequencyHz) or nameof(RoomReductionDb)
            or nameof(TailThresholdDb) or nameof(TailReleaseMs) or nameof(DenoiseStrengthIndex) or nameof(ReverbReductionAmount))
            RefreshEffectPreview();
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
        AudioProcessingOptions Options,
        bool UseOriginalInput = false);

    private sealed class AppSettings
    {
        public bool EnableAdvancedCleanup { get; set; }
        public AudioCleanupTuning? CleanupTuning { get; set; }
        public VoiceToneTuning? VoiceTuning { get; set; }
        public string? CurrentFolder { get; set; }

        public string? OutputFolder { get; set; }

        public string? AiAudioFolder { get; set; }

        public string? FfmpegPath { get; set; }

        public string? IntroAudioPath { get; set; }

        public string? OutroAudioPath { get; set; }

        public string? CoverImagePath { get; set; }

        public string? DeepFilterNetPath { get; set; }

        public string? VoiceFixerPath { get; set; }

        public int LoudnessTarget { get; set; } = -16;
        public bool RemoveSilence { get; set; } = true;
        public double SilencePaddingSeconds { get; set; }
        public List<PortablePreset>? PortablePresets { get; set; }
        public double SilenceSeconds { get; set; } = 0.1;

        public double SilenceThresholdDb { get; set; } = -35;

        public bool EnableDenoise { get; set; } = true;
        public int DenoiseStrengthIndex { get; set; } = 1;
        public double VoiceFixerMix { get; set; } = 0.6;
        public bool MatchComparisonLoudness { get; set; } = true;

        public bool UseAiProcessedAudio { get; set; }

        public bool EnableDeepFilterNet { get; set; }

        public bool EnableDeepFilterNetPostFilter { get; set; } = true;

        public bool FallbackToFfmpegWhenAiFails { get; set; } = true;

        public bool EnableVoiceFixerDereverb { get; set; }

        public int VoiceFixerMode { get; set; }

        public bool ReduceRoomTone { get; set; }

        public bool ReduceReverb { get; set; }

        public double ReverbReductionAmount { get; set; } = 0.6;

        public bool EnableThreeBandEq { get; set; }
        public double LowEqDb { get; set; }
        public double MidEqDb { get; set; }
        public double HighEqDb { get; set; }
        public bool EnableDeEsser { get; set; }
        public double DeEsserAmount { get; set; } = 45;
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

        public string ChannelModeValue { get; set; } = "center_stereo";

        public double StereoBalance { get; set; }
    }
}
