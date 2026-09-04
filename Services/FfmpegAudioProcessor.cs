using System.Diagnostics;
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
    bool EnhanceVoiceEq,
    bool NormalizeLoudness,
    bool EnableLimiter,
    double VolumeGainDb,
    string OutputFolder);

public sealed record AudioProcessingProgress(
    double Percent,
    TimeSpan? Position);

public sealed record AudioMetadataOptions(
    string? Title,
    string? Artist,
    string? Album,
    string? CoverImagePath);

public sealed record AudioOutputFormat(
    string DisplayName,
    string Extension,
    string AudioCodec);

public sealed record AudioQualityAnalysis(
    TimeSpan? Duration,
    double? MeanVolumeDb,
    double? MaxVolumeDb,
    double? IntegratedLufs,
    double? TruePeakDbfs);

public sealed class FfmpegAudioProcessor
{
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

    public static IReadOnlyCollection<string> AudioExtensions => SupportedExtensions;

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

        var filters = BuildFilters(options);
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

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);

        if (trimDuration is { TotalSeconds: > 0 })
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(trimDuration.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(filters))
        {
            startInfo.ArgumentList.Add("-af");
            startInfo.ArgumentList.Add(filters);
        }

        if (sampleRate is not null)
        {
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add(sampleRate.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (channels is not null)
        {
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add(channels.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(audioCodec))
        {
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add(audioCodec);
        }

        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 FFmpeg。");

        SetBackgroundPriority(process);

        var errorTask = ReadOutputAsync(process.StandardError, cancellationToken);
        var outputTask = ReadProgressAsync(process.StandardOutput, trimDuration ?? duration, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(errorTask, outputTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg 處理失敗，ExitCode={process.ExitCode}");
        }
    }

    private static TimeSpan? CalculateTrimDuration(TimeSpan? trimStart, TimeSpan? trimEnd)
    {
        var start = trimStart is { TotalSeconds: > 0 } ? trimStart.Value : TimeSpan.Zero;
        if (trimEnd is not { TotalSeconds: > 0 } || trimEnd.Value <= start)
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
        TimeSpan? trimEnd = null)
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

    public async Task MergeAsync(
        string ffmpegPath,
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        double gapSeconds,
        IProgress<AudioProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default,
        AudioMetadataOptions? metadata = null,
        AudioOutputFormat? outputFormat = null)
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

            await RunFfmpegAsync(
                ffmpegPath,
                BuildMergeArguments(concatListPath, outputPath, metadata, outputFormat),
                totalDuration,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    public async Task<TimeSpan?> ProbeDurationAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var ffprobePath = FindSiblingFfprobe(ffmpegPath);
        if (ffprobePath is null)
        {
            return null;
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

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);

        return process.ExitCode == 0
            && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public async Task<AudioQualityAnalysis> AnalyzeQualityAsync(
        string ffmpegPath,
        string inputPath,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        duration ??= await ProbeDurationAsync(ffmpegPath, inputPath, cancellationToken).ConfigureAwait(false);

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

        foreach (var candidateName in candidateNames)
        {
            foreach (var extension in SupportedExtensions)
            {
                var candidate = Path.Combine(aiAudioFolder, $"{candidateName}{extension}");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return Directory
            .EnumerateFiles(aiAudioFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedAudioFile)
            .FirstOrDefault(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return name.StartsWith(baseName, StringComparison.CurrentCultureIgnoreCase)
                    && (name.Contains("enhanced", StringComparison.CurrentCultureIgnoreCase)
                        || name.Contains("clean", StringComparison.CurrentCultureIgnoreCase)
                        || name.Contains("ai", StringComparison.CurrentCultureIgnoreCase));
            });
    }

    public static string MakeMergedOutputPath(
        string outputFolder,
        string? customFileName = null,
        string? outputExtension = null)
    {
        var sourcePath = Path.Combine(outputFolder, "podcast_merged.m4a");
        return MakeOutputPath(sourcePath, outputFolder, customFileName, outputExtension);
    }

    public static string? TryFindFfmpeg()
    {
        var fromPath = FindInPath("ffmpeg.exe");
        if (fromPath is not null)
        {
            return fromPath;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
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
                var found = Directory.EnumerateFiles(candidate, "ffmpeg.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string BuildFilters(AudioProcessingOptions options)
    {
        var silenceSeconds = Math.Clamp(Math.Round(options.SilenceSeconds, 1), 0.1, 1.0);
        var threshold = Math.Clamp(options.SilenceThresholdDb, -80, -5);
        var volumeGainDb = Math.Clamp(Math.Round(options.VolumeGainDb, 1), -24, 24);

        var filters = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"silenceremove=start_periods=1:start_duration={silenceSeconds:0.0}:start_threshold={threshold:0.#}dB:stop_periods=-1:stop_duration={silenceSeconds:0.0}:stop_threshold={threshold:0.#}dB")
        };

        if (options.EnableDenoise)
        {
            filters.Add("afftdn=nf=-25");
        }

        if (options.ReduceRoomTone)
        {
            filters.Add("highpass=f=100");
            filters.Add("lowpass=f=9000");
            filters.Add("equalizer=f=250:t=q:w=1.1:g=-3");
            filters.Add("equalizer=f=500:t=q:w=1.2:g=-1.5");
        }

        if (options.ReduceReverb)
        {
            filters.Add("highpass=f=120");
            filters.Add("lowpass=f=8500");
            filters.Add("equalizer=f=180:t=q:w=0.8:g=-1.5");
            filters.Add("equalizer=f=420:t=q:w=1.0:g=-2.5");
            filters.Add("equalizer=f=900:t=q:w=1.2:g=-1");
            filters.Add("afftdn=nf=-28:tn=1");
            filters.Add("acompressor=threshold=-24dB:ratio=2.8:attack=4:release=65:makeup=1dB");
        }

        if (options.EnhanceVoiceEq)
        {
            filters.Add("equalizer=f=160:t=q:w=0.9:g=2");
            filters.Add("equalizer=f=320:t=q:w=1.0:g=-1");
            filters.Add("equalizer=f=3200:t=q:w=1.0:g=1");
            filters.Add("acompressor=threshold=-18dB:ratio=2.2:attack=8:release=120:makeup=1.5dB");
        }

        if (options.NormalizeLoudness)
        {
            filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        if (options.EnableLimiter)
        {
            filters.Add("alimiter=limit=0.84:attack=5:release=50");
        }

        if (Math.Abs(volumeGainDb) >= 0.05)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"volume={volumeGainDb:0.#}dB"));
        }

        return string.Join(",", filters);
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
        }
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
        AudioOutputFormat? outputFormat)
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

        var errorTask = ReadOutputAsync(process.StandardError, cancellationToken);
        var outputTask = ReadProgressAsync(process.StandardOutput, duration, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(errorTask, outputTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg 處理失敗，ExitCode={process.ExitCode}");
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

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var combinedOutput = $"{output}{Environment.NewLine}{error}";

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg 分析失敗，ExitCode={process.ExitCode}");
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
        if (directory is null)
        {
            return FindInPath("ffprobe.exe");
        }

        var sibling = Path.Combine(directory, "ffprobe.exe");
        return File.Exists(sibling) ? sibling : FindInPath("ffprobe.exe");
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
