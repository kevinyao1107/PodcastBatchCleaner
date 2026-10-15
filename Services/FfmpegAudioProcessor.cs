using System.Diagnostics;
using System.Text;
using PodcastBatchCleaner.Core.Models;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace PodcastBatchCleaner.Core.Services;

public sealed record AudioProcessingOptions(
    double SilenceSeconds,
    double SilenceThresholdDb,
    bool EnableDenoise,
    bool ReduceRoomTone,
    bool ReduceReverb,
    double ReverbReductionAmount,
    bool EnhanceVoiceEq,
    bool NormalizeLoudness,
    bool EnableLimiter,
    double VolumeGainDb,
    AudioChannelMode ChannelMode,
    double StereoBalance,
    string OutputFolder,
    double DenoiseReductionDb = 12,
    bool RemoveSilence = true,
    int LoudnessTarget = -16,
    double SilencePaddingSeconds = 0,
    bool EnableThreeBandEq = false,
    double LowEqDb = 0, double MidEqDb = 0, double HighEqDb = 0,
    bool EnableDeEsser = false, double DeEsserAmount = 45,
    AudioCleanupTuning? CleanupTuning = null,
    VoiceToneTuning? VoiceTuning = null);

public sealed record AudioProcessingProgress(
    double Percent,
    TimeSpan? Position,
    string? Stage = null);

public sealed record AudioAnalysisProgress(
    double Percent,
    string Stage);

public sealed record AudioMetadataOptions(
    string? Title,
    string? Artist,
    string? Album,
    string? CoverImagePath);

public sealed record AudioOutputFormat(
    string DisplayName,
    string Extension,
    string AudioCodec);

public sealed record AudioChannelMode(
    string DisplayName,
    string Value);

public sealed record AudioQualityAnalysis(
    TimeSpan? Duration,
    double? MeanVolumeDb,
    double? MaxVolumeDb,
    double? IntegratedLufs,
    double? TruePeakDbfs);

public sealed class FfmpegAudioProcessor
{
    public const int DefaultOutputSampleRate = 48000;
    public const double OutputTruePeakCeilingDb = -1.5;
    private const string LossyLimiter = "alimiter=limit=0.7079458:attack=5:release=50:level=false:latency=true";
    private static readonly string[] SupportedExtensions =
    [
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".mp4"
    ];

    public static IReadOnlyList<AudioOutputFormat> OutputFormats { get; } =
    [
        new("M4A / AAC", ".m4a", "aac"),
        new("MP3", ".mp3", "libmp3lame"),
        new("WAV", ".wav", "pcm_s16le")
    ];

    public static IReadOnlyList<AudioChannelMode> ChannelModes { get; } =
    [
        new("左右混合置中", "center_stereo"),
        new("轉單聲道 Mono", "mono"),
        new("保持原聲道", "keep"),
        new("左聲道為主", "left_only"),
        new("右聲道為主", "right_only"),
        new("左右平衡調整", "balance"),
        new("左聲道輸出 Mono", "left_mono"),
        new("右聲道輸出 Mono", "right_mono"),
        new("左右互換", "swap")
    ];

    public static AudioChannelMode DefaultPodcastChannelMode => ChannelModes[0];

    public static AudioChannelMode KeepOriginalChannelMode => ChannelModes[2];

    public static IReadOnlyCollection<string> AudioExtensions => SupportedExtensions;

    public static double DefaultReverbReductionAmount => 0.6;

    public async Task ProcessAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        AudioProcessingOptions options,
        TimeSpan? duration = null,
        IProgress<AudioProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? audioCodec = null,
        int? sampleRate = null,
        int? channels = null,
        TimeSpan? trimStart = null,
        TimeSpan? trimEnd = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var trimDuration = CalculateTrimDuration(trimStart, trimEnd);

        options = options with { ChannelMode = await ResolveCommonInputChannelAsync(ffmpegPath, inputPath, options.ChannelMode, cancellationToken).ConfigureAwait(false) };
        var filters = BuildFilters(options, IsLossyOutput(audioCodec, outputPath));
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-threads");
        startInfo.ArgumentList.Add(GetWorkerThreadCount().ToString(CultureInfo.InvariantCulture));
        if (trimStart is { TotalSeconds: > 0 })
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(trimStart.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        // Limit the source before time-changing filters such as silenceremove.
        if (trimDuration is { TotalSeconds: > 0 })
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(trimDuration.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);

        if (!string.IsNullOrWhiteSpace(filters))
        {
            startInfo.ArgumentList.Add("-af");
            startInfo.ArgumentList.Add(filters);
        }

        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add((sampleRate ?? DefaultOutputSampleRate).ToString(CultureInfo.InvariantCulture));

        var outputChannels = ResolveOutputChannels(options.ChannelMode, channels);
        if (outputChannels is not null)
        {
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add(outputChannels.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(audioCodec))
        {
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add(audioCodec);
        }

        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add(outputPath);

        await RunFfmpegAsync(ffmpegPath, startInfo.ArgumentList.ToArray(),
            trimDuration ?? duration, progress, cancellationToken,
            verifyTruePeak: options.EnableLimiter || options.NormalizeLoudness).ConfigureAwait(false);
    }

    private static TimeSpan? CalculateTrimDuration(TimeSpan? trimStart, TimeSpan? trimEnd)
    {
        var start = trimStart ?? TimeSpan.Zero;
        if (start < TimeSpan.Zero || (trimEnd is not null && trimEnd <= start))
        {
            throw new ArgumentException("剪輯結束時間必須大於開始時間，且時間不能小於 0。");
        }
        if (trimEnd is null)
        {
            return null;
        }

        return trimEnd.Value - start;
    }

    public async Task PrepareClipForMergeAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        double fadeInSeconds,
        double fadeOutSeconds,
        TimeSpan? duration = null,
        IProgress<AudioProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        fadeInSeconds = Math.Clamp(fadeInSeconds, 0, 10);
        fadeOutSeconds = Math.Clamp(fadeOutSeconds, 0, 10);

        if (fadeOutSeconds > 0 && duration is null)
        {
            duration = await ProbeDurationAsync(ffmpegPath, inputPath, cancellationToken).ConfigureAwait(false);
            if (duration is null)
                throw new InvalidOperationException("無法判定片尾音檔長度，不能套用淡出。請轉成 WAV 後重試。");
        }

        var filters = new List<string>();

        if (fadeInSeconds > 0)
        {
            filters.Add($"afade=t=in:st=0:d={fadeInSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (fadeOutSeconds > 0 && duration is { TotalSeconds: > 0 })
        {
            var actualFadeOut = Math.Min(fadeOutSeconds, duration.Value.TotalSeconds);
            var fadeStart = Math.Max(duration.Value.TotalSeconds - actualFadeOut, 0);
            filters.Add(
                $"afade=t=out:st={fadeStart.ToString("0.###", CultureInfo.InvariantCulture)}:d={actualFadeOut.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-nostats",
            "-y",
            "-threads",
            GetWorkerThreadCount().ToString(CultureInfo.InvariantCulture),
            "-i",
            inputPath
        };

        if (filters.Count > 0)
        {
            arguments.Add("-af");
            arguments.Add(string.Join(",", filters));
        }

        arguments.AddRange(
        [
            "-ar",
            "44100",
            "-ac",
            "2",
            "-c:a",
            "pcm_s16le",
            "-progress",
            "pipe:1",
            outputPath
        ]);

        await RunFfmpegAsync(ffmpegPath, arguments, duration, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareDeepFilterNetInputAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        TimeSpan? duration = null,
        IProgress<AudioProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? trimStart = null,
        TimeSpan? trimEnd = null,
        AudioChannelMode? inputChannelMode = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var trimDuration = CalculateTrimDuration(trimStart, trimEnd);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-nostats",
            "-y",
            "-threads",
            GetWorkerThreadCount().ToString(CultureInfo.InvariantCulture)
        };

        if (trimStart is { TotalSeconds: > 0 })
        {
            arguments.Add("-ss");
            arguments.Add(trimStart.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        arguments.Add("-i");
        arguments.Add(inputPath);

        if (trimDuration is { TotalSeconds: > 0 })
        {
            arguments.Add("-t");
            arguments.Add(trimDuration.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (inputChannelMode is not null) inputChannelMode = await ResolveCommonInputChannelAsync(ffmpegPath, inputPath, inputChannelMode, cancellationToken).ConfigureAwait(false);
        AddAiChannelSelection(arguments, inputChannelMode);
        arguments.AddRange(
        [
            "-ar",
            "48000",
            "-ac",
            "1",
            "-c:a",
            "pcm_s16le",
            "-progress",
            "pipe:1",
            outputPath
        ]);

        await RunFfmpegAsync(
            ffmpegPath,
            arguments,
            trimDuration ?? duration,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareVoiceFixerInputAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        TimeSpan? duration = null,
        IProgress<AudioProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? trimStart = null,
        TimeSpan? trimEnd = null,
        AudioChannelMode? inputChannelMode = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var trimDuration = CalculateTrimDuration(trimStart, trimEnd);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-nostats",
            "-y",
            "-threads",
            GetWorkerThreadCount().ToString(CultureInfo.InvariantCulture)
        };

        if (trimStart is { TotalSeconds: > 0 })
        {
            arguments.Add("-ss");
            arguments.Add(trimStart.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        arguments.Add("-i");
        arguments.Add(inputPath);

        if (trimDuration is { TotalSeconds: > 0 })
        {
            arguments.Add("-t");
            arguments.Add(trimDuration.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (inputChannelMode is not null) inputChannelMode = await ResolveCommonInputChannelAsync(ffmpegPath, inputPath, inputChannelMode, cancellationToken).ConfigureAwait(false);
        AddAiChannelSelection(arguments, inputChannelMode);
        arguments.AddRange(
        [
            "-ar",
            "44100",
            "-ac",
            "1",
            "-c:a",
            "pcm_s16le",
            "-progress",
            "pipe:1",
            outputPath
        ]);

        await RunFfmpegAsync(
            ffmpegPath,
            arguments,
            trimDuration ?? duration,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task MergeAsync(
        string ffmpegPath,
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        double gapSeconds,
        IProgress<AudioProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default,
        AudioMetadataOptions? metadata = null,
        AudioOutputFormat? outputFormat = null,
        bool normalizeLoudness = false,
        bool enableLimiter = false,
        int sampleRate = DefaultOutputSampleRate,
        int loudnessTarget = -16)
    {
        if (segmentPaths.Count == 0)
        {
            throw new ArgumentException("沒有可合併的音檔。", nameof(segmentPaths));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempDirectory = Path.Combine(Path.GetDirectoryName(outputPath)!, $".merge_temp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var concatInputs = new List<string>();
            var totalDuration = TimeSpan.Zero;
            var gapDuration = TimeSpan.FromSeconds(Math.Clamp(gapSeconds, 0, 10));
            string? silencePath = null;

            if (gapDuration > TimeSpan.Zero && segmentPaths.Count > 1)
            {
                silencePath = Path.Combine(tempDirectory, "silence.wav");
                await CreateSilenceAsync(ffmpegPath, silencePath, gapDuration, cancellationToken).ConfigureAwait(false);
            }

            for (var index = 0; index < segmentPaths.Count; index++)
            {
                concatInputs.Add(segmentPaths[index]);
                totalDuration += await ProbeDurationAsync(ffmpegPath, segmentPaths[index], cancellationToken).ConfigureAwait(false)
                    ?? TimeSpan.Zero;

                if (silencePath is not null && index < segmentPaths.Count - 1)
                {
                    concatInputs.Add(silencePath);
                    totalDuration += gapDuration;
                }
            }

            var concatListPath = Path.Combine(tempDirectory, "concat.txt");
            await File.WriteAllLinesAsync(
                concatListPath,
                concatInputs.Select(path => $"file '{EscapeConcatPath(path)}'"),
                cancellationToken).ConfigureAwait(false);

            var finalFilters = new List<string>();
            if (normalizeLoudness)
            {
                finalFilters.Add(await BuildMatchedLoudnessFilterAsync(ffmpegPath,
                    ["-f", "concat", "-safe", "0", "-i", concatListPath], cancellationToken, loudnessTarget).ConfigureAwait(false));
            }
            if (enableLimiter)
                finalFilters.Add(IsLossyOutput(outputFormat?.AudioCodec, outputPath)
                    ? LossyLimiter : "alimiter=limit=0.84:attack=5:release=50:level=false:latency=true");

            await RunFfmpegAsync(
                ffmpegPath,
                BuildMergeArguments(concatListPath, outputPath, metadata, outputFormat, string.Join(",", finalFilters), sampleRate),
                totalDuration,
                progress,
                cancellationToken, verifyTruePeak: normalizeLoudness || enableLimiter).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static void AddAiChannelSelection(List<string> arguments, AudioChannelMode? mode)
    {
        if (mode?.Value is "left_only" or "right_only" or "left_mono" or "right_mono")
        {
            var channel = mode.Value is "left_only" or "left_mono" ? "c0" : "c1";
            arguments.Add("-af");
            arguments.Add($"aformat=channel_layouts=stereo,pan=mono|c0={channel}");
        }
    }

    private async Task<AudioChannelMode> ResolveCommonInputChannelAsync(string ffmpegPath, string inputPath, AudioChannelMode mode, CancellationToken cancellationToken)
    {
        if (mode.Value is not ("mono" or "left_mono" or "right_mono" or "swap")) return mode;
        var count = await ProbeAudioChannelsAsync(ffmpegPath, inputPath, cancellationToken).ConfigureAwait(false);
        return count == 1 ? KeepOriginalChannelMode : mode;
    }

    public async Task<int> ProbeAudioChannelsAsync(string ffmpegPath, string inputPath,
        CancellationToken cancellationToken = default)
    {
        var ffprobePath = FindSiblingFfprobe(ffmpegPath);
        if (ffprobePath is null)
        {
            var info = await RunAnalysisAsync(ffmpegPath,
                ["-hide_banner", "-nostdin", "-i", inputPath, "-map", "0:a:0",
                    "-af", "ashowinfo", "-frames:a", "1", "-f", "null", "-"],
                cancellationToken).ConfigureAwait(false);
            var match = Regex.Match(info, @"\bchannels:(?<channels>\d+)");
            return match.Success && int.TryParse(match.Groups["channels"].Value, out var count)
                ? count : throw new InvalidOperationException("無法讀取音檔聲道數。");
        }
        var output = await RunAnalysisAsync(ffprobePath,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=channels",
                "-of", "default=noprint_wrappers=1:nokey=1", inputPath],
            cancellationToken).ConfigureAwait(false);
        return int.TryParse(output.Trim(), out var channels) && channels > 0
            ? channels : throw new InvalidOperationException("無法讀取音檔聲道數。");
    }

    public async Task RestoreStereoAsync(string ffmpegPath, string leftPath, string rightPath,
        string outputPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await RunFfmpegAsync(ffmpegPath,
            ["-hide_banner", "-nostdin", "-nostats", "-y", "-i", leftPath, "-i", rightPath,
                "-filter_complex", "[0:a]pan=stereo|c0=c0|c1=0*c0[l];[1:a]pan=stereo|c0=0*c0|c1=c0[r];"
                    + "[l][r]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[a]",
                "-map", "[a]", "-c:a", "pcm_s16le", "-progress", "pipe:1", outputPath],
            null, null, cancellationToken).ConfigureAwait(false);
    }

    public Task PrepareAlignmentProbeAsync(string ffmpegPath, string inputPath, string outputPath,
        double startSeconds, CancellationToken cancellationToken)
    {
        return RunFfmpegAsync(ffmpegPath,
            ["-hide_banner", "-nostdin", "-nostats", "-y", "-ss", startSeconds.ToString(CultureInfo.InvariantCulture),
                "-i", inputPath, "-t", "12", "-ar", "8000", "-ac", "1", "-c:a", "pcm_s16le", outputPath],
            null, null, cancellationToken);
    }

    public Task MixAlignedAudioAsync(string ffmpegPath, string originalPath, string restoredPath,
        string outputPath, double restoredMix, double delaySeconds, double gainDb,
        double durationSeconds, CancellationToken cancellationToken)
    {
        var mix = Math.Clamp(restoredMix, 0, 1);
        var alignment = delaySeconds >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"atrim=start={delaySeconds:0.######},asetpts=PTS-STARTPTS")
            : string.Create(CultureInfo.InvariantCulture, $"adelay={-delaySeconds * 1000:0.###}:all=1");
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"[1:a]{alignment},volume={gainDb:0.###}dB[wet];[0:a][wet]amix=inputs=2:weights='{1 - mix:0.###} {mix:0.###}':normalize=0:duration=first,alimiter=limit=0.98:level=false:latency=true[a]");
        return RunFfmpegAsync(ffmpegPath,
            ["-hide_banner", "-nostdin", "-nostats", "-y", "-i", originalPath, "-i", restoredPath,
                "-filter_complex", filter, "-map", "[a]", "-t", durationSeconds.ToString(CultureInfo.InvariantCulture),
                "-c:a", "pcm_s16le", outputPath],
            null, null, cancellationToken);
    }

    public async Task MatchComparisonLoudnessAsync(string ffmpegPath, string inputPath,
        string outputPath, string audioCodec, CancellationToken cancellationToken,
        IProgress<AudioProcessingProgress>? progress = null)
    {
        var filter = await BuildMatchedLoudnessFilterAsync(ffmpegPath, ["-i", inputPath], cancellationToken)
            .ConfigureAwait(false);
        if (IsLossyOutput(audioCodec, outputPath)) filter += "," + LossyLimiter;
        await RunFfmpegAsync(ffmpegPath,
            ["-hide_banner", "-nostdin", "-nostats", "-y", "-i", inputPath, "-af", filter,
                "-ar", "48000", "-c:a", audioCodec, outputPath],
            null, progress, cancellationToken, verifyTruePeak: true).ConfigureAwait(false);
    }

    private async Task<string> BuildMatchedLoudnessFilterAsync(string ffmpegPath,
        string[] inputArguments, CancellationToken cancellationToken, int loudnessTarget = -16)
    {
        ValidateLoudnessTarget(loudnessTarget);
        var analysis = await RunAnalysisAsync(ffmpegPath,
            ["-hide_banner", "-nostdin", "-nostats", .. inputArguments, "-map", "0:a:0", "-af",
                $"loudnorm=I={loudnessTarget}:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"],
            cancellationToken).ConfigureAwait(false);
        var start = analysis.LastIndexOf('{');
        var end = analysis.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("無法讀取音檔響度分析。");
        using var json = System.Text.Json.JsonDocument.Parse(analysis[start..(end + 1)]);
        if (json.RootElement.GetProperty("input_i").GetString() == "-inf") return "anull";
        double Metric(string name)
        {
            var value = double.Parse(json.RootElement.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
            if (!double.IsFinite(value)) throw new InvalidOperationException("音檔響度分析結果無效，無法匹配響度。");
            return value;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"loudnorm=I={loudnessTarget}:TP=-1.5:LRA=11:measured_I={Metric("input_i")}:measured_TP={Metric("input_tp")}:measured_LRA={Metric("input_lra")}:measured_thresh={Metric("input_thresh")}:offset={Metric("target_offset")}:linear=true");
    }

    public async Task<TimeSpan?> ProbeDurationAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var ffprobePath = FindSiblingFfprobe(ffmpegPath);
        if (ffprobePath is null)
        {
            return await ProbeDurationWithFfmpegAsync(ffmpegPath, inputPath, cancellationToken).ConfigureAwait(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(inputPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);

        return process.ExitCode == 0
            && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : await ProbeDurationWithFfmpegAsync(ffmpegPath, inputPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TimeSpan?> ProbeDurationWithFfmpegAsync(string ffmpegPath, string inputPath,
        CancellationToken cancellationToken)
    {
        var output = await RunAnalysisAsync(ffmpegPath,
            ["-hide_banner", "-nostdin", "-nostats", "-i", inputPath, "-map", "0:a:0", "-t", "0", "-f", "null", "-"],
            cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(output, @"Duration:\s*(?<hours>\d+):(?<minutes>\d{2}):(?<seconds>\d{2}(?:\.\d+)?)",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var seconds = double.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture) * 3600
            + double.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture);
        return double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds) : null;
    }

    public async Task<AudioWaveform> ReadWaveformAsync(string ffmpegPath, string inputPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channels = await ProbeAudioChannelsAsync(ffmpegPath, inputPath, cancellationToken).ConfigureAwait(false);
        if (channels is < 1 or > 64) throw new InvalidOperationException("波形不支援此音檔的聲道數。");
        const int sampleRate = 48000;
        var info = new ProcessStartInfo
        {
            FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error", "-threads", "2", "-i", inputPath,
            "-map", "0:a:0", "-vn", "-ar", "48000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("無法啟動 FFmpeg。");
        SetBackgroundPriority(process);
        // Drain errors concurrently so a corrupt input cannot block the PCM pipe; retain only a short tail.
        var errorTask = Task.Run(async () =>
        {
            var tail = new StringBuilder();
            var chars = new char[2048];
            int read;
            while ((read = await process.StandardError.ReadAsync(chars).ConfigureAwait(false)) > 0)
            {
                tail.Append(chars, 0, read);
                if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
            }
            return tail.ToString();
        });
        try
        {
            var builder = new AudioWaveformBuilder();
            var frameBytes = channels * sizeof(float);
            var buffer = new byte[frameBytes * 4096];
            var pending = 0;
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(pending), cancellationToken)
                .ConfigureAwait(false)) > 0)
            {
                var total = pending + count;
                var complete = total - total % frameBytes;
                for (var offset = 0; offset < complete; offset += frameBytes)
                {
                    float peak = 0;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var value = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                            buffer.AsSpan(offset + channel * sizeof(float), sizeof(float))));
                        if (!float.IsFinite(value)) throw new InvalidDataException("音檔含有無效的 PCM 數值。");
                        peak = Math.Max(peak, Math.Abs(value));
                    }
                    builder.AddFrame(peak);
                }
                pending = total - complete;
                buffer.AsSpan(complete, pending).CopyTo(buffer);
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"波形讀取失敗：{error}");
            if (pending != 0) throw new InvalidDataException("音訊資料未包含完整的 PCM frame。");
            return builder.Build(sampleRate);
        }
        catch
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AudioQualityAnalysis> AnalyzeQualityAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan? duration = null,
        IProgress<AudioAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new AudioAnalysisProgress(0.05, "讀取音檔長度"));
        duration ??= await ProbeDurationAsync(ffmpegPath, inputPath, cancellationToken).ConfigureAwait(false);

        progress?.Report(new AudioAnalysisProgress(0.15, "分析平均音量與峰值"));
        var volumeOutput = await RunAnalysisAsync(
            ffmpegPath,
            [
                "-hide_banner",
                "-nostdin",
                "-nostats",
                "-i",
                inputPath,
                "-af",
                "volumedetect",
                "-f",
                "null",
                "-"
            ],
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new AudioAnalysisProgress(0.55, "分析 LUFS 與 True Peak"));
        var loudnessOutput = await RunAnalysisAsync(
            ffmpegPath,
            [
                "-hide_banner",
                "-nostdin",
                "-nostats",
                "-i",
                inputPath,
                "-filter_complex",
                "ebur128=peak=true",
                "-f",
                "null",
                "-"
            ],
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new AudioAnalysisProgress(1, "分析完成"));
        return new AudioQualityAnalysis(
            duration,
            ParseMetric(volumeOutput, @"mean_volume:\s*(?<value>-?\d+(?:\.\d+)?)\s*dB"),
            ParseMetric(volumeOutput, @"max_volume:\s*(?<value>-?\d+(?:\.\d+)?)\s*dB"),
            ParseLastMetric(loudnessOutput, @"I:\s*(?<value>-?\d+(?:\.\d+)?)\s*LUFS"),
            ParseLastMetric(loudnessOutput, @"Peak:\s*(?<value>-?\d+(?:\.\d+)?)\s*dBFS"));
    }

    public static string MakeOutputPath(
        string inputPath,
        string outputFolder,
        string? customFileName = null,
        string? outputExtension = null)
    {
        var outputFileName = NormalizeOutputFileName(customFileName, inputPath, outputExtension);
        var baseName = Path.GetFileNameWithoutExtension(outputFileName);
        var extension = Path.GetExtension(outputFileName);
        var candidate = Path.Combine(outputFolder, outputFileName);
        var index = 2;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(outputFolder, $"{baseName}_{index}{extension}");
            index++;
        }

        return candidate;
    }

    public static bool IsSupportedAudioFile(string path)
    {
        return SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    public static string? FindAiProcessedAudio(string inputPath, string? aiAudioFolder)
    {
        if (string.IsNullOrWhiteSpace(aiAudioFolder) || !Directory.Exists(aiAudioFolder))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var candidateNames = new[]
        {
            baseName,
            $"{baseName}_enhanced",
            $"{baseName}-enhanced",
            $"{baseName}_cleaned",
            $"{baseName}-cleaned",
            $"{baseName}_ai",
            $"{baseName}-ai"
        };

        var matches = Directory
            .EnumerateFiles(aiAudioFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedAudioFile)
            .Where(path => candidateNames.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(inputPath)} 對應到多個 AI 音檔，請只保留一個對應檔案："
                + string.Join("、", matches.Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));
        }
        return matches.SingleOrDefault();
    }

    public static string MakeMergedOutputPath(
        string outputFolder,
        string? customFileName = null,
        string? outputExtension = null)
    {
        var sourcePath = Path.Combine(outputFolder, "podcast_merged.m4a");
        return MakeOutputPath(sourcePath, outputFolder, customFileName, outputExtension);
    }

    public static string? TryFindBundledFfmpeg(string? baseDirectory = null)
        => BundledAudioToolLocator.Find("FFmpeg", [OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"], baseDirectory);

    public async Task<bool> HasAudioEncoderAsync(string ffmpegPath, string encoder,
        CancellationToken cancellationToken = default)
    {
        var listing = await RunAnalysisAsync(ffmpegPath, ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);
        return Regex.IsMatch(listing, @"^\s*A\S*\s+" + Regex.Escape(encoder) + @"\s",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }

    public static string? TryFindFfmpeg(string? baseDirectory = null)
    {
        var bundled = TryFindBundledFfmpeg(baseDirectory);
        if (bundled is not null) return bundled;
        var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var fromPath = FindInPath(fileName);
        if (fromPath is not null)
        {
            return fromPath;
        }

        var candidates = new[]
        {
            "/opt/homebrew/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
            Path.Combine(baseDirectory ?? AppContext.BaseDirectory, fileName),
            Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "tools", fileName),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JianyingPro", "Apps"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LINE", "Data", "plugin", "ffmpeg")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (Directory.Exists(candidate))
            {
                var found = Directory.EnumerateFiles(candidate, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static void ValidateLoudnessTarget(int target)
    {
        if (!new[] { -14, -16, -18, -23 }.Contains(target)) throw new ArgumentOutOfRangeException(nameof(target));
    }

    private static string BuildFilters(AudioProcessingOptions options, bool lossyOutput = false)
    {
        ValidateLoudnessTarget(options.LoudnessTarget);
        var tuning = options.CleanupTuning;
        tuning?.Validate();
        var silenceSeconds = Math.Clamp(Math.Round(options.SilenceSeconds, 1), 0.1, 10.0);
        var padding = Math.Clamp(options.SilencePaddingSeconds, 0, .5);
        var threshold = Math.Clamp(options.SilenceThresholdDb, -80, -5);
        var volumeGainDb = Math.Clamp(Math.Round(options.VolumeGainDb, 1), -24, 24);

        var filters = new List<string>();
        if (options.RemoveSilence)
        {
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"silenceremove=start_periods=1:start_duration=0:start_threshold={threshold:0.#}dB:stop_periods=-1:stop_duration={silenceSeconds:0.0}:stop_threshold={threshold:0.#}dB:start_silence={padding:0.##}:stop_silence={padding:0.##}"));
        }

        AddChannelFilters(filters, options.ChannelMode, options.StereoBalance);

        if (options.EnableDenoise)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture,
                $"afftdn=nf={tuning?.NoiseFloorDb ?? -35:0.###}:nr={tuning?.DenoiseReductionDb ?? Math.Clamp(options.DenoiseReductionDb, 1, 30):0.###}:tn={(tuning?.TrackNoise ?? true ? 1 : 0)}"));
        }

        if (options.ReduceRoomTone)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"highpass=f={tuning?.RoomHighpassHz ?? 65:0.###}"));
            // A bell's center gain is squared by adynamicequalizer, so its range is half the center dB cut.
            filters.Add(string.Create(CultureInfo.InvariantCulture,
                $"adynamicequalizer=dfrequency={tuning?.RoomFrequencyHz ?? 300:0.###}:tfrequency={tuning?.RoomFrequencyHz ?? 300:0.###}:dqfactor=1:tqfactor=1:threshold=0.06:ratio=1.5:range={tuning?.RoomReductionDb / 2 ?? 2:0.###}:mode=cutabove:attack=30:release=250"));
        }

        if (options.ReduceReverb)
        {
            var amount = Math.Clamp(options.ReverbReductionAmount, 0.2, 1.0);
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"adynamicequalizer=dfrequency=420:tfrequency=420:dqfactor=1:tqfactor=1:threshold=0.04:ratio={1 + amount:0.##}:range={1 + 3 * amount:0.##}:mode=cutabove:attack=25:release=220"));
            var tailThreshold = tuning is null ? .012 : Math.Pow(10, tuning.TailThresholdDb / 20);
            filters.Add(string.Create(CultureInfo.InvariantCulture,
                $"agate=threshold={tailThreshold:0.########}:ratio={1 + amount:0.##}:range=0.35:attack=15:release={tuning?.TailReleaseMs ?? 250:0.###}:makeup=1"));
        }

        if (options.EnhanceVoiceEq)
        {
            var voice = options.VoiceTuning ?? new VoiceToneTuning();
            voice.Validate();
            if (voice.ThicknessDb > 0)
            {
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"equalizer=f=160:t=q:w=0.9:g={voice.ThicknessDb:0.###}"));
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"equalizer=f=320:t=q:w=1.0:g={-voice.ThicknessDb / 2:0.###}"));
            }
            if (voice.ClarityDb > 0)
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"equalizer=f=3200:t=q:w=1.0:g={voice.ClarityDb:0.###}"));
            if (voice.UseCompression)
                filters.Add("acompressor=threshold=-18dB:ratio=2.2:attack=8:release=120:makeup=1.5dB");
        }

        if (options.EnableThreeBandEq || options.EnableDeEsser)
        {
            // Use one defined working rate so 8 kHz sources can safely pass the 8.5/7.2 kHz filters.
            filters.Add("aresample=48000");
            if (options.EnableThreeBandEq)
            {
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"bass=f=140:t=q:w=0.707:g={Math.Clamp(options.LowEqDb, -12, 12):0.###}"));
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"equalizer=f=1100:t=q:w=0.8:g={Math.Clamp(options.MidEqDb, -12, 12):0.###}"));
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"treble=f=8500:t=q:w=0.707:g={Math.Clamp(options.HighEqDb, -12, 12):0.###}"));
            }
            if (options.EnableDeEsser)
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"equalizer=f=7200:t=q:w=1.8:g={-Math.Clamp(options.DeEsserAmount, 0, 100) * .09:0.###}"));
        }

        if (Math.Abs(volumeGainDb) >= 0.05)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"volume={volumeGainDb:0.#}dB"));
        }

        if (options.NormalizeLoudness)
        {
            filters.Add($"loudnorm=I={options.LoudnessTarget}:TP=-1.5:LRA=11");
        }

        if (options.EnableLimiter)
        {
            filters.Add(lossyOutput ? LossyLimiter : "alimiter=limit=0.84:attack=5:release=50:level=false:latency=true");
        }

        return string.Join(",", filters);
    }

    private static void AddChannelFilters(List<string> filters, AudioChannelMode channelMode, double stereoBalance)
    {
        switch (channelMode.Value)
        {
            case "mono":
                filters.Add("aformat=channel_layouts=stereo");
                filters.Add("pan=mono|c0=0.5*c0+0.5*c1");
                break;
            case "left_mono":
                filters.Add("aformat=channel_layouts=stereo,pan=mono|c0=c0");
                break;
            case "right_mono":
                filters.Add("aformat=channel_layouts=stereo,pan=mono|c0=c1");
                break;
            case "swap":
                filters.Add("aformat=channel_layouts=stereo,pan=stereo|c0=c1|c1=c0");
                break;
            case "center_stereo":
                filters.Add("aformat=channel_layouts=stereo");
                filters.Add("pan=stereo|c0=0.5*c0+0.5*c1|c1=0.5*c0+0.5*c1");
                break;
            case "left_only":
                filters.Add("aformat=channel_layouts=stereo");
                filters.Add("pan=stereo|c0=c0|c1=c0");
                break;
            case "right_only":
                filters.Add("aformat=channel_layouts=stereo");
                filters.Add("pan=stereo|c0=c1|c1=c1");
                break;
            case "balance":
                filters.Add("aformat=channel_layouts=stereo");
                var balance = Math.Clamp(stereoBalance, -1, 1);
                var leftGain = balance <= 0 ? 1 : 1 - balance;
                var rightGain = balance >= 0 ? 1 : 1 + balance;
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"pan=stereo|c0={leftGain:0.###}*c0|c1={rightGain:0.###}*c1"));
                break;
        }
    }

    private static int? ResolveOutputChannels(AudioChannelMode channelMode, int? requestedChannels)
    {
        if (requestedChannels is not null)
        {
            return requestedChannels;
        }

        return channelMode.Value switch
        {
            "mono" or "left_mono" or "right_mono" => 1,
            "center_stereo" or "left_only" or "right_only" or "balance" or "swap" => 2,
            _ => null
        };
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var tail = new Queue<string>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
            tail.Enqueue(line);
            if (tail.Count > 30)
            {
                tail.Dequeue();
            }
        }
        return string.Join(Environment.NewLine, tail);
    }

    private static async Task CreateSilenceAsync(
        string ffmpegPath,
        string outputPath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-hide_banner",
            "-nostdin",
            "-nostats",
            "-y",
            "-f",
            "lavfi",
            "-i",
            "anullsrc=r=44100:cl=stereo",
            "-t",
            duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-c:a",
            "pcm_s16le",
            outputPath
        };

        await RunFfmpegAsync(ffmpegPath, arguments, null, null, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> BuildMergeArguments(
        string concatListPath,
        string outputPath,
        AudioMetadataOptions? metadata,
        AudioOutputFormat? outputFormat,
        string? finalFilter = null,
        int sampleRate = DefaultOutputSampleRate)
    {
        outputFormat ??= OutputFormats[0];
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-nostats",
            "-y",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            concatListPath
        };

        var hasCover = !string.IsNullOrWhiteSpace(metadata?.CoverImagePath)
            && File.Exists(metadata.CoverImagePath)
            && !string.Equals(outputFormat.Extension, ".wav", StringComparison.OrdinalIgnoreCase);

        if (hasCover)
        {
            arguments.Add("-i");
            arguments.Add(metadata!.CoverImagePath!);
            arguments.Add("-map");
            arguments.Add("0:a");
            arguments.Add("-map");
            arguments.Add("1:v");
            arguments.Add("-c:v");
            arguments.Add("mjpeg");
            arguments.Add("-disposition:v");
            arguments.Add("attached_pic");
        }

        if (!string.IsNullOrWhiteSpace(finalFilter))
        {
            arguments.Add("-af");
            arguments.Add(finalFilter);
        }
        arguments.Add("-ar");
        arguments.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        arguments.Add("-c:a");
        arguments.Add(outputFormat.AudioCodec);

        AddMetadataArgument(arguments, "title", metadata?.Title);
        AddMetadataArgument(arguments, "artist", metadata?.Artist);
        AddMetadataArgument(arguments, "album", metadata?.Album);

        if (hasCover)
        {
            arguments.Add("-metadata:s:v");
            arguments.Add("title=Album cover");
            arguments.Add("-metadata:s:v");
            arguments.Add("comment=Cover (front)");
        }

        arguments.AddRange(
        [
            "-progress",
            "pipe:1",
            outputPath
        ]);

        return arguments;
    }

    private static void AddMetadataArgument(List<string> arguments, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        arguments.Add("-metadata");
        arguments.Add($"{key}={value.Trim()}");
    }

    private static async Task RunFfmpegAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        TimeSpan? duration,
        IProgress<AudioProcessingProgress>? progress,
        CancellationToken cancellationToken,
        bool verifyTruePeak = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputPath = arguments[^1];
        var temporaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            $".processing_{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
        try
        {
            var attenuationDb = 0d;
            for (var attempt = 0; ; attempt++)
            {
                var renderArguments = arguments.Take(arguments.Count - 1).Append(temporaryPath).ToList();
                if (attenuationDb < 0)
                {
                    var gain = string.Create(CultureInfo.InvariantCulture, $"volume={attenuationDb:0.###}dB");
                    var filterIndex = renderArguments.IndexOf("-af");
                    if (filterIndex >= 0) renderArguments[filterIndex + 1] += "," + gain;
                    else renderArguments.InsertRange(renderArguments.Count - 1, ["-af", gain]);
                }
                var start = attempt == 0 ? 0d : 0.8 + attempt * 0.05;
                var range = attempt == 0 ? 0.8 : 0.04;
                var renderProgress = verifyTruePeak && progress is not null
                    ? new ScaledProgress(progress, start, range, attempt == 0 ? null : "修正編碼峰值") : progress;
                await RunFfmpegToFileAsync(ffmpegPath, renderArguments, duration, renderProgress, cancellationToken)
                    .ConfigureAwait(false);
                if (!verifyTruePeak) break;
                progress?.Report(new AudioProcessingProgress(start + range, null, "驗證輸出真峰值"));
                var peak = await MeasureTruePeakAsync(ffmpegPath, temporaryPath, cancellationToken).ConfigureAwait(false);
                // FFmpeg prints tenths of a dB, so leave room for measurement rounding.
                if (peak <= OutputTruePeakCeilingDb - 0.1) break;
                if (attempt >= 3)
                    throw new InvalidOperationException($"輸出真峰值仍超過 {OutputTruePeakCeilingDb} dBTP，已保留舊輸出。測得 {peak} dBTP。");
                attenuationDb += OutputTruePeakCeilingDb - peak - 0.3;
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
            if (verifyTruePeak) progress?.Report(new AudioProcessingProgress(1, null, "完成"));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsLossyOutput(string? audioCodec, string path)
        => audioCodec is "aac" or "libmp3lame" or "libvorbis" or "libopus" or "wmav2"
            || (string.IsNullOrWhiteSpace(audioCodec)
                && Path.GetExtension(path).ToLowerInvariant() is ".m4a" or ".mp4" or ".aac" or ".mp3" or ".ogg" or ".wma");

    private static async Task<double> MeasureTruePeakAsync(string ffmpegPath, string inputPath,
        CancellationToken cancellationToken)
    {
        var analysis = await RunAnalysisAsync(ffmpegPath,
            ["-hide_banner", "-nostdin", "-nostats", "-threads", GetWorkerThreadCount().ToString(CultureInfo.InvariantCulture),
                "-i", inputPath, "-map", "0:a:0", "-af", "ebur128=peak=true:framelog=verbose", "-f", "null", "-"],
            cancellationToken).ConfigureAwait(false);
        var matches = Regex.Matches(analysis, @"Peak:\s*(?<value>-?\d+(?:\.\d+)?|-inf)\s*dBFS", RegexOptions.IgnoreCase);
        if (matches.Count == 0) throw new InvalidOperationException("無法驗證輸出音檔的真峰值，已保留舊輸出。");
        var value = matches[^1].Groups["value"].Value;
        if (value.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return double.NegativeInfinity;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var peak) || !double.IsFinite(peak))
            throw new InvalidOperationException("輸出真峰值分析結果無效，已保留舊輸出。");
        return peak;
    }

    private sealed class ScaledProgress(IProgress<AudioProcessingProgress> target, double start, double range,
        string? stage) : IProgress<AudioProcessingProgress>
    {
        public void Report(AudioProcessingProgress value)
            => target.Report(value with { Percent = start + value.Percent * range, Stage = stage ?? value.Stage });
    }

    private static async Task RunFfmpegToFileAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        TimeSpan? duration,
        IProgress<AudioProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 FFmpeg。");

        SetBackgroundPriority(process);

        var errorTask = ReadOutputAsync(process.StandardError, CancellationToken.None);
        var outputTask = ReadProgressAsync(process.StandardOutput, duration, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(errorTask, outputTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            try { await Task.WhenAll(errorTask, outputTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg 處理失敗，ExitCode={process.ExitCode}。{await errorTask.ConfigureAwait(false)}");
        }
    }

    private static async Task<string> RunAnalysisAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 FFmpeg。");

        SetBackgroundPriority(process);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var combinedOutput = $"{output}{Environment.NewLine}{error}";

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg 分析失敗，ExitCode={process.ExitCode}。{error}");
        }

        return combinedOutput;
    }

    private static double? ParseMetric(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success
            && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static double? ParseLastMetric(string text, string pattern)
    {
        var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
        return matches.Count > 0
            && double.TryParse(matches[matches.Count - 1].Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static async Task ReadProgressAsync(
        StreamReader reader,
        TimeSpan? duration,
        IProgress<AudioProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lastReport = DateTimeOffset.MinValue;
        double? latestPercent = null;
        TimeSpan? latestPosition = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("progress=end", StringComparison.Ordinal))
            {
                progress?.Report(new AudioProcessingProgress(1, latestPosition));
                continue;
            }

            if (!line.StartsWith("out_time_", StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var value = line[(separatorIndex + 1)..].Trim();
            var position = ParseProgressTime(value);
            if (position is null)
            {
                continue;
            }

            var percent = duration is { TotalSeconds: > 0 }
                ? Math.Clamp(position.Value.TotalSeconds / duration.Value.TotalSeconds, 0, 1)
                : 0;

            latestPercent = percent;
            latestPosition = position;

            var now = DateTimeOffset.UtcNow;
            if (now - lastReport < TimeSpan.FromMilliseconds(250))
            {
                continue;
            }

            lastReport = now;
            progress?.Report(new AudioProcessingProgress(latestPercent.Value, latestPosition));
        }
    }

    private static TimeSpan? ParseProgressTime(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            return TimeSpan.FromSeconds(microseconds / 1_000_000d);
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timestamp)
            ? timestamp
            : null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        KillProcessTree(process);
        await process.WaitForExitAsync().ConfigureAwait(false);
    }

    private static int GetWorkerThreadCount()
    {
        return Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    }

    private static void SetBackgroundPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);
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

    private static string? FindSiblingFfprobe(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath);
        var fileName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        if (directory is null)
        {
            return FindInPath(fileName);
        }

        var sibling = Path.Combine(directory, fileName);
        return File.Exists(sibling) ? sibling : FindInPath(fileName);
    }

    private static string NormalizeOutputFileName(
        string? customFileName,
        string inputPath,
        string? outputExtension = null)
    {
        var inputExtension = Path.GetExtension(inputPath);
        var preferredExtension = NormalizeExtension(outputExtension) ?? inputExtension;
        var fallbackBaseName = $"{Path.GetFileNameWithoutExtension(inputPath)}_processed";
        var fileName = string.IsNullOrWhiteSpace(customFileName)
            ? $"{fallbackBaseName}{preferredExtension}"
            : customFileName.Trim();

        fileName = Path.GetFileName(fileName);
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = fallbackBaseName;
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = preferredExtension;
        }
        else if (!string.IsNullOrWhiteSpace(outputExtension))
        {
            extension = preferredExtension;
        }

        return $"{baseName}{extension}";
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var trimmed = extension.Trim();
        return trimmed.StartsWith('.')
            ? trimmed.ToLowerInvariant()
            : $".{trimmed.ToLowerInvariant()}";
    }

    private static string? FindInPath(string fileName)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return paths
            .Select(path => Path.Combine(path, fileName))
            .FirstOrDefault(File.Exists);
    }
}
