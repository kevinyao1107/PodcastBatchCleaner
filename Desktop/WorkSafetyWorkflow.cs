using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;
#if CROSS_PLATFORM
using Avalonia.Interactivity;
using Avalonia.Threading;
namespace PodcastBatchCleaner.CrossPlatform;
#else
using System.Windows;
using System.Windows.Threading;
namespace PodcastBatchCleanerWpf;
#endif

public partial class MainWindow
{
    private bool _safetyReady, _interactiveSafety, _hasWork, _safetyModalOpen, _safetyDecisionPending, _allowSafetyClose, _discardOnSafetyClose;
    private string _savedWorkState = "", _lastDraftState = "", _draftStatus = "";
    private string? _knownWorkPath;
    private WorkDraftStore? _draftStore;
    private DispatcherTimer? _safetyTimer;
    private Func<string, string, IReadOnlyList<(string Id, string Text)>, Task<string?>>? _safetyPromptOverride = null;
    private Func<Task<string?>>? _saveWorkPathOverride = null;
    private static readonly JsonSerializerOptions WorkFingerprintJson = new()
    { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private sealed record RenderReceipt(string Key, string Path, string OutputStamp, string Kind);
    private readonly Dictionary<AudioFileItem, RenderReceipt> _renderReceipts = new();
    private RenderReceipt? _episodeReceipt;
    private List<AudioFileItem> _episodeReceiptItems = [];
    private string _renderAiDirectoryStamp = "";
    private readonly Dictionary<string, string?> _renderAiMatches = new();

    public bool HasUnsavedWork => _safetyReady && (_hasWork || AudioFiles.Count > 0) && CaptureWorkState() != _savedWorkState;
    public string WorkSaveStateText => !HasUnsavedWork ? (_knownWorkPath is null ? "尚未保存工作檔" : "工作已保存")
        : "工作未保存" + (string.IsNullOrEmpty(_draftStatus) ? "" : $" · {_draftStatus}");
    public string ProcessedResultText => GetProcessedResultText();
    public bool IsWorkStatusVisible => !IsProgressVisible;
    public bool HasResultWarning => ProcessedResultText.Contains("已變更") || ProcessedResultText.Contains("未記錄");

    private void InitializeWorkSafety(bool interactive)
    {
        _interactiveSafety = interactive; _savedWorkState = CaptureWorkState(); _safetyReady = true;
        if (!interactive) return;
        try { _draftStore = new WorkDraftStore(Path.Combine(Path.GetDirectoryName(GetSettingsPath())!, "drafts")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _draftStatus = $"草稿無法使用：{ex.Message}"; }
        _safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _safetyTimer.Tick += (_, _) =>
        {
            RefreshSafetyUi();
            if (CanStartWork && HasUnsavedWork) FlushWorkDraft();
        };
        _safetyTimer.Start();
    }
    private AppSettings CapturePortableWorkSettings()
    {
        var settings = CaptureAppSettings();
        settings.FfmpegPath = settings.DeepFilterNetPath = settings.VoiceFixerPath = null;
        settings.PortablePresets = null;
        return settings;
    }
    private string CaptureWorkState() => JsonSerializer.Serialize(new
    {
        Settings = CapturePortableWorkSettings(),
        Files = AudioFiles.Select(item => new WorkAudioFile(item.FilePath, item.CustomOutputFileName, item.TrimStartText, item.TrimEndText))
    }, WorkFingerprintJson);
    private AudioWorkSession CreateWorkDocument(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var settings = CapturePortableWorkSettings(); ConvertWorkPaths(settings, folder, true);
        return new AudioWorkSession(AudioWorkSessionFile.Format, 1, JsonSerializer.SerializeToElement(settings),
            AudioFiles.Select(item => new WorkAudioFile(AudioWorkSessionFile.RelativePath(item.FilePath, folder),
                item.CustomOutputFileName, item.TrimStartText, item.TrimEndText)).ToList());
    }
    private void MarkWorkSaved(string path)
    {
        _knownWorkPath = Path.GetFullPath(path); _savedWorkState = CaptureWorkState();
        _hasWork = true; DeleteOwnedDraft(); RefreshSafetyUi();
    }
    private void MarkImportedFolder()
    {
        _knownWorkPath = null; _savedWorkState = ""; _hasWork = AudioFiles.Count > 0;
        _renderReceipts.Clear(); DeleteOwnedDraft(); RefreshSafetyUi();
    }
    private void DeleteOwnedDraft()
    {
        try { _draftStore?.Delete(); _lastDraftState = ""; if (_draftStore is not null) _draftStatus = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _draftStatus = $"草稿清理失敗：{ex.Message}"; }
    }
    private bool FlushWorkDraft()
    {
        if (_draftStore is null) return false;
        try
        {
            var state = CaptureWorkState();
            if (state == _lastDraftState && File.Exists(_draftStore.DraftPath)) return true;
            _draftStore.Write(CreateWorkDocument(_draftStore.DraftPath)); _lastDraftState = state;
            _draftStatus = $"草稿已保存 {DateTime.Now:HH:mm:ss}";
            RefreshSafetyUi(); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        { _draftStatus = $"草稿保存失敗：{ex.Message}"; RefreshSafetyUi(); return false; }
    }
    private void RefreshSafetyUi()
    {
        if (!_safetyReady) return;
        Title = HasUnsavedWork ? "音訊工作台 *" : "音訊工作台";
        foreach (var name in new[] { nameof(HasUnsavedWork), nameof(WorkSaveStateText), nameof(ProcessedResultText), nameof(HasResultWarning), nameof(IsWorkStatusVisible) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    private async Task<string?> AskSafetyAsync(string title, string message, params (string Id, string Text)[] choices)
    {
        _safetyModalOpen = true; OnPropertyChanged(nameof(CanStartWork));
        try { return await (_safetyPromptOverride?.Invoke(title, message, choices) ?? SafetyDecisionDialog.ShowAsync(this, title, message, choices)); }
        finally { _safetyModalOpen = false; OnPropertyChanged(nameof(CanStartWork)); }
    }
    private async Task<string?> PickSafetySavePathAsync()
    {
        if (_saveWorkPathOverride is not null) return await _saveWorkPathOverride();
        if (_knownWorkPath is not null) return _knownWorkPath;
#if CROSS_PLATFORM
        return await PickFileAsync("保存工作檔", ["*.podcast-work.json"], "episode.podcast-work.json");
#else
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "音訊工作檔|*.podcast-work.json", FileName = "episode.podcast-work.json" };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
#endif
    }
    private async Task<bool> ConfirmLeaveWorkAsync(bool closing)
    {
        if ((!_interactiveSafety && _safetyPromptOverride is null) || !HasUnsavedWork) return true;
        if (_safetyDecisionPending) return false;
        _safetyDecisionPending = true; OnPropertyChanged(nameof(CanStartWork));
        try
        {
            var choices = new List<(string, string)> { ("save", "保存工作") };
            if (closing && _draftStore is not null) choices.Add(("draft", "保留草稿並關閉"));
            choices.Add(("discard", "放棄變更")); choices.Add(("cancel", "取消"));
            var result = await AskSafetyAsync("工作尚未保存", "剪輯、排序或處理設定有未保存的變更。", choices.ToArray());
            if (result == "discard") { if (closing) { _discardOnSafetyClose = true; DeleteOwnedDraft(); } return true; }
            if (result == "draft") return FlushWorkDraft();
            if (result != "save") return false;
            var path = await PickSafetySavePathAsync();
            if (path is null) return false;
            try { SaveWorkSession(path); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
            { WorkSessionText = StatusText = $"保存失敗，工作仍保留：{ex.Message}"; return false; }
        }
        finally { _safetyDecisionPending = false; OnPropertyChanged(nameof(CanStartWork)); }
    }
    private async Task HandleDirtyCloseAsync()
    {
        try
        {
            var keep = await ConfirmLeaveWorkAsync(true);
            if (!keep) { _closeAfterWork = false; return; }
            _allowSafetyClose = true;
#if CROSS_PLATFORM
            Dispatcher.UIThread.Post(Close);
#else
            _ = Dispatcher.BeginInvoke(new Action(Close));
#endif
        }
        catch (Exception ex) { _closeAfterWork = false; StatusText = $"工作仍保留，關閉未完成：{ex.Message}"; }
    }
    private void DisposeWorkSafety()
    {
        _safetyTimer?.Stop();
        if (!HasUnsavedWork || _discardOnSafetyClose) DeleteOwnedDraft();
        _draftStore?.Dispose(); _draftStore = null;
    }
    private async Task<bool> TryRecoverWorkDraftAsync()
    {
        if (_draftStore is null) return false;
        try
        {
            using var recovery = _draftStore.FindLatestRecoverable();
            if (recovery is null) return false;
            var decision = await AskSafetyAsync("發現未保存的工作草稿", $"草稿時間：{File.GetLastWriteTime(recovery.Path):yyyy-MM-dd HH:mm:ss}\n恢復剪輯、排序與設定；不包含處理後音檔。",
                ("recover", "恢復草稿"), ("later", "稍後處理"));
            if (decision != "recover") return false;
            LoadWorkSession(recovery.Path);
            _knownWorkPath = null; _savedWorkState = ""; _hasWork = true;
            if (FlushWorkDraft()) recovery.Delete();
            WorkSessionText = "已恢復草稿，請保存為工作檔。"; RefreshSafetyUi(); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        { StatusText = $"草稿未恢復，原檔仍保留：{ex.Message}"; return false; }
    }
    private async Task<bool> ConfirmExportAsync(IReadOnlyList<AudioFileItem> items)
    {
        try { JsonSerializer.Serialize(CapturePortableWorkSettings()); }
        catch (ArgumentException) { StatusText = "設定含無效數值，請修正後再輸出。"; return false; }
        if (!_interactiveSafety && _safetyPromptOverride is null) return true;
        var state = CaptureWorkState();
        var inputs = string.Join("\n", items.Select(item => FileStamp(item.FilePath)));
        var enabled = new List<string>();
        if (RemoveSilence) enabled.Add($"剪空白 > {SilenceSeconds:0.0} 秒，門檻 {SilenceThresholdDb:0} dBFS");
        if (EnableDenoise) enabled.Add("FFmpeg 降噪");
        if (ReduceRoomTone) enabled.Add("空間音抑制");
        if (ReduceReverb) enabled.Add("尾音抑制");
        if (EnhanceVoiceEq) enabled.Add("人聲增厚");
        if (EnableThreeBandEq) enabled.Add("三頻 EQ");
        if (EnableDeEsser) enabled.Add("齒音抑制");
        if (EnableDeepFilterNet) enabled.Add("DeepFilterNet");
        if (EnableVoiceFixerDereverb) enabled.Add($"VoiceFixer 混合 {VoiceFixerMix:P0}");
        if (UseAiProcessedAudio) enabled.Add("外部 AI 音檔");
        if (NormalizeLoudness) enabled.Add($"響度 {LoudnessTarget} LUFS");
        if (EnableLimiter) enabled.Add("Limiter");
        enabled.Add($"音量增益 {VolumeGainDb:+0.0;-0.0;0.0} dB");
        var plannedMaster = MergeSegments ? FfmpegAudioProcessor.MakeMergedOutputPath(_outputFolder!, MergedOutputFileName, SelectedOutputFormat.Extension) : "";
        var message = $"音檔：{items.Count} 個（清單共 {AudioFiles.Count} 個）\n方式：{(MergeSegments ? "合併成單一檔案" : "每個音檔分別輸出")}\n格式：{SelectedOutputFormat.DisplayName}\n聲道：{SelectedChannelMode.DisplayName}\n位置：{_outputFolder}\n"
            + (MergeSegments ? $"預計整集檔名：{Path.GetFileName(plannedMaster)}\n段落間隔：{MergeGapSeconds:0.0} 秒\n{IntroAudioText}\n{OutroAudioText}\n封面：{CoverImageText}\n標題：{PodcastTitle}\n" : "")
            + $"\n效果：\n{string.Join("\n", enabled)}\n\n同名輸出會自動加序號，不覆蓋既有成品。\n\n音檔清單：\n{string.Join("\n", items.Take(20).Select(item => item.FileName))}"
            + (items.Count > 20 ? $"\n另有 {items.Count - 20} 個音檔" : "");
        var result = await AskSafetyAsync("確認輸出", message, ("export", "開始輸出"), ("cancel", "返回調整"));
        if (result != "export") return false;
        if (state != CaptureWorkState() || inputs != string.Join("\n", items.Select(item => FileStamp(item.FilePath))))
        { StatusText = "設定或來源已變更，請重新確認輸出。"; return false; }
        return true;
    }

    private static string FileStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { var info = new FileInfo(path); return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path + "|missing"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path + "|unavailable"; }
    }
    private string CaptureRenderKey(AudioFileItem item, string kind = "完整輸出")
    {
        string? ai = null;
        if (UseAiProcessedAudio)
        {
            try
            {
                var folder = string.IsNullOrWhiteSpace(_aiAudioFolder) ? null : new DirectoryInfo(_aiAudioFolder);
                var stamp = folder is { Exists: true } ? $"{folder.FullName}|{folder.LastWriteTimeUtc.Ticks}" : _aiAudioFolder ?? "";
                if (stamp != _renderAiDirectoryStamp) { _renderAiMatches.Clear(); _renderAiDirectoryStamp = stamp; }
                if (!_renderAiMatches.TryGetValue(item.FilePath, out ai))
                    _renderAiMatches[item.FilePath] = ai = FfmpegAudioProcessor.FindAiProcessedAudio(item.FilePath, _aiAudioFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { ai = "unavailable"; }
        }
        return JsonSerializer.Serialize(new
        {
            Options = CreateComparisonPresets()[1].Options with { OutputFolder = "" },
            Format = SelectedOutputFormat,
            Source = FileStamp(item.FilePath), item.TrimStartText, item.TrimEndText,
            UseAiProcessedAudio, Ai = FileStamp(ai), EnableDeepFilterNet, EnableDeepFilterNetPostFilter,
            EnableVoiceFixerDereverb, VoiceFixerMode, VoiceFixerMix, FallbackToFfmpegWhenAiFails,
            Ffmpeg = FileStamp(_ffmpegPath), Dfn = EnableDeepFilterNet ? FileStamp(_deepFilterNetPath) : "",
            VoiceFixer = EnableVoiceFixerDereverb ? FileStamp(_voiceFixerPath) : "",
            ComparisonLoudness = kind.StartsWith("A/B") && MatchComparisonLoudness
        }, WorkFingerprintJson);
    }
    private void RememberRenderedResult(AudioFileItem item, string key, string kind)
    {
        if (item.ProcessedPath is not { } path) return;
        _renderReceipts[item] = new(key, path, FileStamp(path), kind); RefreshSafetyUi();
    }
    private void RememberEpisodeResult(IReadOnlyList<AudioFileItem> items, string key, string path)
    {
        _episodeReceiptItems = items.ToList();
        _episodeReceipt = new(key, path, FileStamp(path), "整集成品"); RefreshSafetyUi();
    }
    private string CaptureEpisodeKey(IReadOnlyList<AudioFileItem> items) => JsonSerializer.Serialize(new
    {
        Items = items.Select(item => CaptureRenderKey(item)),
        Order = AudioFiles.Where(items.Contains).Select(item => new { item.FilePath, item.TrimStartText, item.TrimEndText }),
        Intro = FileStamp(_introAudioPath), Outro = FileStamp(_outroAudioPath), Cover = FileStamp(_coverImagePath),
        MergeGapSeconds, IntroFadeSeconds, OutroFadeSeconds, PodcastTitle, PodcastArtist, PodcastAlbum
    }, WorkFingerprintJson);
    private string GetProcessedResultText()
    {
        if (PlaybackSourceIndex == 3) return EffectPreviewText;
        if (PlaybackSourceIndex == 2 && _lastMergedOutputPath is { } episode)
        {
            if (_episodeReceipt is null || _episodeReceipt.Path != episode) return "整集成品的來源設定未記錄。";
            return _episodeReceipt.Key == CaptureEpisodeKey(_episodeReceiptItems) && _episodeReceipt.OutputStamp == FileStamp(episode)
                ? "整集成品符合目前設定。" : "設定或來源已變更；目前播放的是先前的整集成品。";
        }
        if (_selectedFile is not { HasProcessedFile: true } item) return "";
        if (!_renderReceipts.TryGetValue(item, out var receipt) || receipt.Path != item.ProcessedPath) return "處理後音檔的來源設定未記錄，請重新試聽或輸出確認。";
        return receipt.Key == CaptureRenderKey(item, receipt.Kind) && receipt.OutputStamp == FileStamp(receipt.Path)
            ? $"{receipt.Kind} · 符合目前設定" : $"設定或來源已變更；處理後版本是先前的{receipt.Kind}，仍可播放。";
    }
}
