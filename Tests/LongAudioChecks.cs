using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using PodcastBatchCleaner.Core.Services;
using PodcastBatchCleanerWpf;

internal static class LongAudioChecks
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("Pass input audio, FFmpeg, DeepFilterNet and a test output directory.");
        var input = Path.GetFullPath(args[0]);
        var root = Path.Combine(Path.GetFullPath(args[3]), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceHash = await HashAsync(input);
        var results = new List<object>();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var window = (MainWindow)typeof(MainWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                null, [typeof(bool)], null)!.Invoke([false]);
            var clock = Stopwatch.StartNew();
            var lastTick = clock.Elapsed.TotalMilliseconds;
            var maxGap = 0d;
            var ticks = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                var now = clock.Elapsed.TotalMilliseconds;
                maxGap = Math.Max(maxGap, now - lastTick);
                lastTick = now;
                ticks++;
                window.CurrentProcessingFileText = Path.GetFileName(input);
            };
            timer.Start();
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var stageDirectory = Path.Combine(root, ".ai_stage");
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    var token = deadline.Token;
                    var processor = new FfmpegAudioProcessor();
                    async Task VerifyOutput(string path)
                    {
                        var quality = await processor.AnalyzeQualityAsync(args[1], path, cancellationToken: token);
                        var describe = typeof(FfmpegAudioProcessor).GetMethod("RunAnalysisAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
                        var text = await (Task<string>)describe.Invoke(null, [args[1],
                            new[] { "-hide_banner", "-nostdin", "-i", path, "-map", "0:a:0", "-t", "0", "-f", "null", "-" }, token])!;
                        var match = System.Text.RegularExpressions.Regex.Match(text, @"Audio:[^\r\n]*?, (?<rate>\d+) Hz");
                        var sampleRate = match.Success ? int.Parse(match.Groups["rate"].Value) : 0;
                        if (sampleRate != FfmpegAudioProcessor.DefaultOutputSampleRate || quality.TruePeakDbfs is null or > -1.5)
                            throw new InvalidOperationException($"Invalid encoded output: rate={sampleRate}; peak={quality.TruePeakDbfs}");
                        results.Add(new { Test = "Encoded output verified", File = Path.GetFileName(path), SampleRate = sampleRate,
                            quality.IntegratedLufs, quality.TruePeakDbfs, quality.MaxVolumeDb, Passed = true });
                        Console.WriteLine($"PASS: {Path.GetFileName(path)}; {sampleRate}Hz; {quality.IntegratedLufs}LUFS; {quality.TruePeakDbfs}dBTP");
                    }
                    var duration = await processor.ProbeDurationAsync(args[1], input, token)
                        ?? throw new InvalidOperationException("Unable to read input duration.");
                    if (duration.TotalMinutes < 30) throw new InvalidOperationException("Select a recording at least 30 minutes long.");
                    Console.WriteLine($"INPUT: {duration}; output directory: {root}");
                    var options = new AudioProcessingOptions(0.3, -35, true, true, true, 0.6,
                        true, true, true, 0, FfmpegAudioProcessor.DefaultPodcastChannelMode, 0, root);
                    var progressUpdates = 0;
                    var lastPercent = 0d;
                    var progress = new Progress<AudioProcessingProgress>(value =>
                    {
                        if (!double.IsFinite(value.Percent) || value.Percent is < 0 or > 1)
                            throw new InvalidOperationException("Invalid progress fraction.");
                        progressUpdates++;
                        lastPercent = value.Percent;
                        window.ProcessingProgress = value.Percent * 100;
                    });

                    async Task CancelCheck(string name, Func<CancellationToken, Task> action)
                    {
                        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var watch = Stopwatch.StartNew();
                        var requestedAt = 0d;
                        using var registration = cancel.Token.Register(() => requestedAt = watch.Elapsed.TotalSeconds);
                        cancel.CancelAfter(TimeSpan.FromSeconds(3));
                        try
                        {
                            await action(cancel.Token);
                            throw new InvalidOperationException($"{name} completed before cancellation could be tested.");
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            var latency = watch.Elapsed.TotalSeconds - requestedAt;
                            if (latency > 5) throw new InvalidOperationException($"{name} cancellation took {latency:F2} seconds.");
                            results.Add(new { Test = name, CancellationSeconds = latency, Passed = true });
                            Console.WriteLine($"PASS: {name}; cancellation latency {latency:F3}s");
                        }
                    }

                    var canceledOutput = Path.Combine(root, "canceled_existing.m4a");
                    await File.WriteAllTextAsync(canceledOutput, "previous output", token);
                    await CancelCheck("FFmpeg long-audio cancellation", cancellation => processor.ProcessAsync(args[1],
                        input, canceledOutput, options, duration, progress, cancellation, audioCodec: "aac"));
                    if (await File.ReadAllTextAsync(canceledOutput, token) != "previous output"
                        || Directory.EnumerateFiles(root, ".processing_*").Any())
                        throw new InvalidOperationException("Canceled encoding did not preserve output or remove its temporary file.");
                    File.Delete(canceledOutput);

                    var output = Path.Combine(root, "ffmpeg_processed.m4a");
                    var watch = Stopwatch.StartNew();
                    Console.WriteLine("STAGE: complete FFmpeg processing");
                    await processor.ProcessAsync(args[1], input, output, options, duration, progress, token, audioCodec: "aac");
                    var processedDuration = await processor.ProbeDurationAsync(args[1], output, token);
                    if (processedDuration is null || processedDuration <= TimeSpan.Zero || progressUpdates < 2)
                        throw new InvalidOperationException("Full FFmpeg processing did not produce valid audio and progress updates.");
                    results.Add(new { Test = "Complete FFmpeg processing", Seconds = watch.Elapsed.TotalSeconds,
                        OutputSeconds = processedDuration.Value.TotalSeconds, ProgressUpdates = progressUpdates, LastProgress = lastPercent, Passed = true });
                    Console.WriteLine($"PASS: full FFmpeg processing; {watch.Elapsed.TotalSeconds:F2}s; output {processedDuration}");
                    await VerifyOutput(output);

                    Directory.CreateDirectory(stageDirectory);
                    var prepared = Path.Combine(stageDirectory, "prepared.wav");
                    Console.WriteLine("STAGE: prepare full-length DeepFilterNet input");
                    await processor.PrepareDeepFilterNetInputAsync(args[1], input, prepared, duration, progress, token);
                    var deepFilter = new DeepFilterNetAudioProcessor();
                    var canceledAi = Path.Combine(stageDirectory, "canceled_ai");
                    await CancelCheck("DeepFilterNet long-audio cancellation", async cancellation =>
                        await deepFilter.ProcessAsync(args[2], prepared, canceledAi, false, cancellation));
                    DeleteStage(canceledAi, root);

                    watch.Restart();
                    Console.WriteLine("STAGE: complete full-length DeepFilterNet inference");
                    var ai = await deepFilter.ProcessAsync(args[2], prepared, Path.Combine(stageDirectory, "ai"), false, token);
                    var aiDuration = await processor.ProbeDurationAsync(args[1], ai, token);
                    if (aiDuration is null || Math.Abs((aiDuration.Value - duration).TotalSeconds) > 1)
                        throw new InvalidOperationException("DeepFilterNet unexpectedly changed the recording duration.");
                    results.Add(new { Test = "Complete DeepFilterNet inference", Seconds = watch.Elapsed.TotalSeconds,
                        OutputSeconds = aiDuration.Value.TotalSeconds, Passed = true });
                    Console.WriteLine($"PASS: full DeepFilterNet inference; {watch.Elapsed.TotalSeconds:F2}s");
                    Console.WriteLine("STAGE: FFmpeg post-processing after AI");
                    await processor.ProcessAsync(args[1], ai, Path.Combine(root, "deepfilter_processed.m4a"),
                        options with { EnableDenoise = false }, aiDuration, progress, token, audioCodec: "aac");
                    await VerifyOutput(Path.Combine(root, "deepfilter_processed.m4a"));
                    results.Add(new { Test = "AI then FFmpeg pipeline", Passed = true });
                    DeleteStage(stageDirectory, root);
                    if (await HashAsync(input) != sourceHash) throw new InvalidOperationException("Original recording changed.");
                    if (ticks < 20 || maxGap > 1000)
                        throw new InvalidOperationException($"WPF dispatcher response gap reached {maxGap:F0}ms (ticks={ticks}).");
                    results.Add(new { Test = "WPF dispatcher responsiveness", Ticks = ticks, MaxGapMilliseconds = maxGap, Passed = true });
                    results.Add(new { Test = "Original preserved and staging cleaned", Passed = true });
                    Console.WriteLine($"PASS: WPF dispatcher; {ticks} ticks; max gap {maxGap:F1}ms");
                    await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(results,
                        new JsonSerializerOptions { WriteIndented = true }), token);
                    Console.WriteLine($"REPORT: {Path.Combine(root, "report.json")}");
                    completion.TrySetResult(0);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    results.Add(new { Test = "Pipeline failure", Error = error.ToString(), Ticks = ticks, MaxGapMilliseconds = maxGap, Passed = false });
                    await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(results,
                        new JsonSerializerOptions { WriteIndented = true }));
                    completion.TrySetResult(1);
                }
                finally
                {
                    DeleteStage(stageDirectory, root);
                    timer.Stop();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var exitCode = await completion.Task;
        thread.Join();
        return exitCode;
    }

    private static void DeleteStage(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
