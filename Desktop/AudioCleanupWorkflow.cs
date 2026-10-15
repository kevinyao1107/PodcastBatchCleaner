using System.ComponentModel;
using System.IO;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;
#if CROSS_PLATFORM
using Avalonia.Interactivity;
namespace PodcastBatchCleaner.CrossPlatform;
#else
using System.Windows;
namespace PodcastBatchCleanerWpf;
#endif

public partial class MainWindow
{
    private bool _enableAdvancedCleanup;
    private AudioCleanupTuning _cleanupTuning = new();
    private int _selectedCleanupEffect;
    private VoiceToneTuning _voiceTuning = new();
    public double VoiceThicknessDb { get => _voiceTuning.ThicknessDb; set { if (double.IsFinite(value)) SetField(ref _voiceTuning, _voiceTuning with { ThicknessDb = Math.Clamp(value, 0, 6) }, nameof(VoiceThicknessDb)); } }
    public double VoiceClarityDb { get => _voiceTuning.ClarityDb; set { if (double.IsFinite(value)) SetField(ref _voiceTuning, _voiceTuning with { ClarityDb = Math.Clamp(value, 0, 4) }, nameof(VoiceClarityDb)); } }
    public bool UseVoiceCompression { get => _voiceTuning.UseCompression; set => SetField(ref _voiceTuning, _voiceTuning with { UseCompression = value }, nameof(UseVoiceCompression)); }
    private void ApplyVoiceTuning(VoiceToneTuning tuning)
    {
        tuning.Validate();
        VoiceThicknessDb = tuning.ThicknessDb; VoiceClarityDb = tuning.ClarityDb; UseVoiceCompression = tuning.UseCompression;
    }
    private void ResetVoiceTuning_Click(object sender, RoutedEventArgs e) => ApplyVoiceTuning(new());
    private string? _effectBypassPath, _effectProcessedPath, _effectPlaybackPath;
    private double? _pendingEffectSeek;
    private EffectPreviewKey? _effectPreviewKey;
    public bool EnableAdvancedCleanup { get => _enableAdvancedCleanup; set => SetField(ref _enableAdvancedCleanup, value); }
    public bool TrackNoise { get => _cleanupTuning.TrackNoise; set => SetField(ref _cleanupTuning, _cleanupTuning with { TrackNoise = value }, nameof(TrackNoise)); }
    public double FineDenoiseDb { get => _cleanupTuning.DenoiseReductionDb; set { if (double.IsFinite(value)) SetField(ref _cleanupTuning, _cleanupTuning with { DenoiseReductionDb = Math.Clamp(value, 1, 30) }, nameof(FineDenoiseDb)); } }
    public double NoiseFloorDb { get => _cleanupTuning.NoiseFloorDb; set { if (double.IsFinite(value)) SetField(ref _cleanupTuning, _cleanupTuning with { NoiseFloorDb = Math.Clamp(value, -70, -20) }, nameof(NoiseFloorDb)); } }
    public double RoomHighpassHz { get => _cleanupTuning.RoomHighpassHz; set { if (double.IsFinite(value)) SetField(ref _cleanupTuning, _cleanupTuning with { RoomHighpassHz = Math.Clamp(value, 40, 120) }, nameof(RoomHighpassHz)); } }
    public double RoomFrequencyHz { get => _cleanupTuning.RoomFrequencyHz; set { if (double.IsFinite(value)) SetField(ref _cleanupTuning, _cleanupTuning with { RoomFrequencyHz = Math.Clamp(value, 150, 600) }, nameof(RoomFrequencyHz)); } }
    public double RoomReductionDb { get => _cleanupTuning.RoomReductionDb; set { if (double.IsFinite(value)) SetField(ref _cleanupTuning, _cleanupTuning with { RoomReductionDb = Math.Clamp(value, 2, 12) }, nameof(RoomReductionDb)); } }
    public double TailThresholdDb { get => _cleanupTuning.TailThresholdDb; set { if (double.IsFinite(value)) SetField(ref _cleanupTuning, _cleanupTuning with { TailThresholdDb = Math.Clamp(value, -60, -25) }, nameof(TailThresholdDb)); } }
    public double TailReleaseMs { get => _cleanupTuning.TailReleaseMs; set { if (double.IsFinite(value)) SetField(ref _cleanupTuning, _cleanupTuning with { TailReleaseMs = Math.Clamp(value, 100, 800) }, nameof(TailReleaseMs)); } }
    public int SelectedCleanupEffect { get => _selectedCleanupEffect; set => SetField(ref _selectedCleanupEffect, Math.Clamp(value, 0, 3)); }
    public string EffectPreviewText => _effectPreviewKey is null ? "尚未產生單項比較。"
        : IsEffectPreviewCurrent ? "比較已就緒 · 等響度" : "單項比較已過期。";
    public bool CanPlayEffectPreview => CanStartWork && IsEffectPreviewCurrent;
    private bool IsEffectPreviewCurrent => _effectPreviewKey is not null && _effectPreviewKey == CaptureEffectPreviewKey()
        && File.Exists(_effectBypassPath) && File.Exists(_effectProcessedPath);
    private sealed record EffectPreviewKey(string Source, long Length, DateTime Modified, string TrimStart, string TrimEnd,
        int Effect, AudioCleanupTuning? Tuning, double DenoiseDb, double ReverbAmount, VoiceToneTuning Voice);
    private EffectPreviewKey? CaptureEffectPreviewKey()
    {
        if (_selectedFile is null) return null;
        try
        {
            var info = new FileInfo(_selectedFile.FilePath);
            return !info.Exists ? null : new(info.FullName, info.Length, info.LastWriteTimeUtc,
                _selectedFile.TrimStartText, _selectedFile.TrimEndText, SelectedCleanupEffect,
                EnableAdvancedCleanup ? _cleanupTuning : null, DenoiseReductionDb, ReverbReductionAmount, _voiceTuning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private void ApplyCleanupTuning(AudioCleanupTuning tuning)
    {
        tuning.Validate();
        TrackNoise = tuning.TrackNoise;
        FineDenoiseDb = tuning.DenoiseReductionDb; NoiseFloorDb = tuning.NoiseFloorDb;
        RoomHighpassHz = tuning.RoomHighpassHz; RoomFrequencyHz = tuning.RoomFrequencyHz;
        RoomReductionDb = tuning.RoomReductionDb; TailThresholdDb = tuning.TailThresholdDb; TailReleaseMs = tuning.TailReleaseMs;
    }
    private void ResetCleanupTuning_Click(object sender, RoutedEventArgs e) => ApplyCleanupTuning(new());
    private void RefreshEffectPreview()
    {
        if (PlaybackSourceIndex == 3 && !IsEffectPreviewCurrent)
        { StopPlayback(); _player.Close(); _pendingEffectSeek = null; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPlayEffectPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectPreviewText)));
    }
    private async void CompareCleanupEffect_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedAudioFile() is not { } item) { StatusText = "請先選取音檔。"; return; }
        await ProcessCleanupComparisonAsync(item);
    }
    private async Task ProcessCleanupComparisonAsync(AudioFileItem item)
    {
        if (!CanStartWork) return;
        if (_ffmpegPath is not { } ffmpeg || !File.Exists(ffmpeg)) { StatusText = "請先指定 FFmpeg。"; return; }
        if (!File.Exists(item.FilePath)) { StatusText = "找不到原始音檔。"; return; }
        var key = CaptureEffectPreviewKey();
        if (key is null || key.Source != Path.GetFullPath(item.FilePath)) return;
        var outputFolder = _outputFolder ?? Path.Combine(_currentFolder ?? item.Folder, "processed");
        var runDirectory = Path.Combine(outputFolder, "effect_preview", Guid.NewGuid().ToString("N"));
        var committed = false;
        IsProcessing = true;
        using var cancellation = new CancellationTokenSource(); _processingCancellation = cancellation;
        var progressWindow = new ProcessingProgressWindow { Owner = this };
        progressWindow.CancelRequested += (_, _) => cancellation.Cancel(); progressWindow.Show();
        try
        {
            Directory.CreateDirectory(runDirectory);
            var duration = await _processor.ProbeDurationAsync(ffmpeg, item.FilePath, cancellation.Token)
                ?? throw new InvalidDataException("無法取得原音長度。");
            if (!TryGetTrimRangeForDuration(item, duration, out var start, out var end, out var error)) throw new InvalidDataException(error);
            start ??= TimeSpan.Zero;
            end = new[] { end ?? duration, duration, start.Value + PreviewDuration }.Min();
            if (end <= start) throw new InvalidDataException("試聽範圍沒有音訊。");
            var options = AudioCleanupPreview.CreateOptions((AudioCleanupEffect)key.Effect, key.Tuning, key.DenoiseDb, key.ReverbAmount, runDirectory, key.Voice);
            var paths = new[] { Path.Combine(runDirectory, "bypass.wav"), Path.Combine(runDirectory, "effect.wav") };
            for (var index = 0; index < paths.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var label = index == 0 ? "旁通" : "單項效果";
                var raw = Path.Combine(runDirectory, "render.wav");
                progressWindow.SetIndeterminate($"產生{label}", item.FileName);
                await _processor.ProcessAsync(ffmpeg, item.FilePath, raw,
                    index == 0 ? AudioCleanupPreview.Bypass(options) : options,
                    end - start, cancellationToken: cancellation.Token, audioCodec: "pcm_f32le", trimStart: start, trimEnd: end);
                progressWindow.SetIndeterminate($"匹配{label}響度", item.FileName);
                await _processor.MatchComparisonLoudnessAsync(ffmpeg, raw, paths[index], "pcm_s16le", cancellation.Token);
                File.Delete(raw);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (key != CaptureEffectPreviewKey()) throw new InvalidOperationException("音檔或設定已變更，比較結果未套用。");
            _effectPreviewKey = key; _effectBypassPath = paths[0]; _effectProcessedPath = paths[1];
            committed = true; StatusText = "單項效果比較完成。";
        }
        catch (OperationCanceledException) { StatusText = "單項比較已取消。"; }
        catch (Exception ex) { StatusText = $"單項比較失敗：{ex.Message}"; }
        finally
        {
            _processingCancellation = null; IsProcessing = false; progressWindow.CompleteAndClose();
            if (!committed) TryDeleteDirectory(runDirectory);
            RefreshEffectPreview(); FinishPendingClose();
        }
    }
    private void PlayCleanupBypass_Click(object sender, RoutedEventArgs e) => PlayCleanupVersion(false);
    private void PlayCleanupEffect_Click(object sender, RoutedEventArgs e) => PlayCleanupVersion(true);
    private void PlayCleanupVersion(bool effect)
    {
        if (!CanPlayEffectPreview) { RefreshEffectPreview(); StatusText = "單項比較尚未就緒或已過期。"; return; }
        var position = PlaybackSourceIndex == 3 ? _player.Position.TotalSeconds : 0;
        _effectPlaybackPath = effect ? _effectProcessedPath : _effectBypassPath;
        RefreshWorkspaceUi();
        _pendingEffectSeek = position; PlaybackSourceIndex = 3; OpenSelectedForPlayback(true);
    }
}
