using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PodcastBatchCleaner.Core.Services;

internal static class EncodedAudioChecks
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Pass input recording, FFmpeg and a test output directory.");
        var input = Path.GetFullPath(args[0]);
        var root = Path.Combine(Path.GetFullPath(args[2]), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task<string> Hash()
        {
            await using var stream = File.OpenRead(input);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
        var originalHash = await Hash();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var processor = new FfmpegAudioProcessor();
        if (!await processor.HasAudioEncoderAsync(args[1], "libmp3lame", token))
            throw new InvalidOperationException("The supplied FFmpeg cannot encode MP3.");
        var output = Path.Combine(root, "mp3_processed.mp3");
        var duration = await processor.ProbeDurationAsync(args[1], input, token);
        var options = new AudioProcessingOptions(0.3, -35, true, true, true, 0.6,
            true, true, true, 0, FfmpegAudioProcessor.DefaultPodcastChannelMode, 0, root);
        var progress = new RecordingAudioProgress();
        var watch = Stopwatch.StartNew();
        Console.WriteLine($"STAGE: full-recording MP3 export; output: {output}");
        await processor.ProcessAsync(args[1], input, output, options, duration, progress, token, audioCodec: "libmp3lame");
        var seconds = watch.Elapsed.TotalSeconds;
        var quality = await processor.AnalyzeQualityAsync(args[1], output, cancellationToken: token);
        var describe = typeof(FfmpegAudioProcessor).GetMethod("RunAnalysisAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var description = await (Task<string>)describe.Invoke(null, [args[1],
            new[] { "-hide_banner", "-nostdin", "-i", output, "-map", "0:a:0", "-t", "0", "-f", "null", "-" }, token])!;
        var rateMatch = System.Text.RegularExpressions.Regex.Match(description, @"Audio:[^\r\n]*?, (?<rate>\d+) Hz");
        var sampleRate = rateMatch.Success ? int.Parse(rateMatch.Groups["rate"].Value) : 0;
        if (sampleRate != 48000 || quality.TruePeakDbfs is null or > -1.5 || await Hash() != originalHash)
            throw new InvalidOperationException("MP3 export failed format, peak or original-preservation checks.");
        if (Directory.EnumerateFiles(root, ".processing_*").Any()) throw new InvalidOperationException("Unexpected temporary output.");
        var report = new { File = output, SampleRate = sampleRate, Seconds = seconds, quality.Duration,
            quality.IntegratedLufs, quality.TruePeakDbfs, OriginalPreserved = true, Passed = true };
        await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine($"PASS: full MP3; {sampleRate}Hz; {quality.IntegratedLufs}LUFS; {quality.TruePeakDbfs}dBTP; {seconds:F2}s");
        Console.WriteLine($"REPORT: {Path.Combine(root, "report.json")}");
        return 0;
    }
}
