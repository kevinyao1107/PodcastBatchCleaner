using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PodcastBatchCleaner.Core.Services;

public sealed record AudioProcessingOptions(
    double SilenceSeconds,
    double SilenceThresholdDb,
    bool EnableDenoise,
    bool ReduceRoomTone,
    double VolumeGainDb,
    string OutputFolder);

public sealed record AudioProcessingProgress(
    double Percent,
    TimeSpan? Position);

public sealed class FfmpegAudioProcessor
{
    private static readonly string[] SupportedExtensions =
    [
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".mp4"
    ];

    public static IReadOnlyCollection<string> AudioExtensions => SupportedExtensions;

    public async Task ProcessAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        AudioProcessingOptions options,
        TimeSpan? duration = null,
        IProgress<AudioProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

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
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);

        if (!string.IsNullOrWhiteSpace(filters))
        {
            startInfo.ArgumentList.Add("-af");
            startInfo.ArgumentList.Add(filters);
        }

        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add(outputPath);

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

    public static string MakeOutputPath(string inputPath, string outputFolder, string? customFileName = null)
    {
        var outputFileName = NormalizeOutputFileName(customFileName, inputPath);
        var baseName = Path.GetFileNameWithoutExtension(outputFileName);
        var outputExtension = Path.GetExtension(outputFileName);
        var candidate = Path.Combine(outputFolder, outputFileName);
        var index = 2;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(outputFolder, $"{baseName}_{index}{outputExtension}");
            index++;
        }

        return candidate;
    }

    public static bool IsSupportedAudioFile(string path)
    {
        return SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
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

    private static string NormalizeOutputFileName(string? customFileName, string inputPath)
    {
        var inputExtension = Path.GetExtension(inputPath);
        var fallbackBaseName = $"{Path.GetFileNameWithoutExtension(inputPath)}_processed";
        var fileName = string.IsNullOrWhiteSpace(customFileName)
            ? $"{fallbackBaseName}{inputExtension}"
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
            extension = inputExtension;
        }

        return $"{baseName}{extension}";
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
