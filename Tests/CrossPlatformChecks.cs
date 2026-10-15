using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;
using PodcastBatchCleaner.CrossPlatform;
using PodcastBatchCleaner.CrossPlatform.Controls;
using PodcastBatchCleaner.CrossPlatform.Services;

internal static class CrossPlatformChecks
{
    private static int _checks;
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [STAThread]
    public static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? ".build_cross_ui"); Directory.CreateDirectory(output);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null, [false], null)!;
        try
        {
            // The isolated constructor disables automatic loading and persistence of user settings.
            var shell = window;
            shell.Show(); Dispatcher.UIThread.RunJobs();
            var tabs = shell.GetVisualDescendants().OfType<TabControl>().Single();
            Check(tabs.Items.Count == 4, "Four WPF-style tabs");
            Check(!window.CanExportAll && !window.CanExportSelection, "An empty workspace cannot start an export");
            Check(!window.FindControl<Expander>("PresetManager")!.IsExpanded && !window.FindControl<Expander>("SilenceAdvanced")!.IsExpanded,
                "Secondary preset management and silence detection start collapsed");
            Check(window.HasNoAudioFiles, "Empty workspace exposes a clear import state");
            using (var emptyFrame = new RenderTargetBitmap(new PixelSize(1180, 760), new Vector(96, 96)))
            { emptyFrame.Render(shell); emptyFrame.Save(Path.Combine(output, "empty_workspace_1180.png")); }
            var list = shell.GetVisualDescendants().OfType<DataGrid>().Single();
            Check(list.Columns.Count == 3, "Focused audio table avoids horizontal editing columns");
            var first = new AudioFileItem { FilePath = Path.Combine(output, "first.wav"), TrimStartText = "0.1", TrimEndText = "1.5", CustomOutputFileName = "episode" };
            window.AudioFiles.Add(first); window.AudioFiles.Add(new AudioFileItem { FilePath = Path.Combine(output, "second.wav") });
            list.SelectedItem = first; Dispatcher.UIThread.RunJobs();
            Check(window.SelectedAudioItem == first && window.HasSelectedAudioItem, "File inspector follows the selected recording");
            var outputName = shell.FindControl<TextBox>("SelectedOutputNameTextBox")!;
            outputName.Text = "inspector-edit"; Dispatcher.UIThread.RunJobs();
            Check(first.CustomOutputFileName == "inspector-edit", "File inspector edits the real output filename");
            outputName.Text = "episode"; Dispatcher.UIThread.RunJobs();
            var trimEditor = shell.FindControl<TextBox>("SelectedTrimStartTextBox")!;
            trimEditor.Text = "0.2"; Dispatcher.UIThread.RunJobs();
            Check(first.TrimStartText == "0.2", "File inspector updates fractional trim values");
            trimEditor.Text = "0.1";
            var other = window.AudioFiles[1]; list.SelectedItem = other; Dispatcher.UIThread.RunJobs();
            outputName.Text = "other-file"; Dispatcher.UIThread.RunJobs();
            Check(other.CustomOutputFileName == "other-file" && first.CustomOutputFileName == "episode", "Switching the inspector cannot edit the previous recording");
            list.SelectedItem = null; Dispatcher.UIThread.RunJobs();
            Check(!shell.FindControl<Border>("SelectedFileInspector")!.IsVisible, "Clearing selection hides the per-file editor");
            list.SelectedItem = first; Dispatcher.UIThread.RunJobs();
            Check(window.CanExportAll && window.CanExportSelection && window.OutputScopeText.Contains("1 / 2"), "Selection scope is explicit before export");
            window.MergeSegments = true;
            Check(window.ExportSelectedText == "合併選取" && window.ExportAllText == "合併全部", "Export commands reflect merge mode");
            window.MergeSegments = false;
            Invoke(window, "MoveSelectedDown_Click", window, new RoutedEventArgs());
            Check(window.AudioFiles[1] == first, "Reorder maintains item edits");
            var silence = shell.GetVisualDescendants().OfType<Slider>().First(s => s.Minimum == .1 && s.Maximum == 10);
            silence.Value = .3; Dispatcher.UIThread.RunJobs();
            Check(window.SilenceSeconds == .3, "Silence slider updates in 0.1s increments");
            window.EnableThreeBandEq = true; window.LowEqDb = 3; window.EnableDeEsser = true;
            window.RemoveSilence = false; window.VolumeGainDb = -2; window.LoudnessTarget = -18;
            var work = Path.Combine(output, "roundtrip.podcast-work.json"); Invoke(window, "SaveWorkSession", work);
            window.AudioFiles.Clear(); window.VolumeGainDb = 4; window.LowEqDb = 0;
            Invoke(window, "LoadWorkSession", work);
            Check(window.AudioFiles.Count == 2 && window.AudioFiles[1].TrimStartText == "0.1", "Work file restores order and trims");
            Check(window.VolumeGainDb == -2 && window.LowEqDb == 3 && window.LoudnessTarget == -18 && !window.RemoveSilence, "Work file restores DSP settings");
            Dispatcher.UIThread.RunJobs(); shell.UpdateLayout();
            var rows = list.GetVisualDescendants().OfType<DataGridRow>().OrderBy(row => row.Bounds.Y).ToList();
            Check(rows.Count == 2, "Audio rows are realized");
            var dragged = window.AudioFiles[0];
            var fromPoint = rows[0].TranslatePoint(new Point(20, 20), shell)!.Value;
            var toPoint = rows[1].TranslatePoint(new Point(20, 20), shell)!.Value;
            shell.MouseDown(fromPoint, MouseButton.Left);
            shell.MouseMove(toPoint);
            shell.MouseUp(toPoint, MouseButton.Left);
            Check(window.AudioFiles[1] == dragged, "Pointer drag reorders audio rows");
            for (var size = 0; size < 2; size++)
            {
                shell.Width = size == 0 ? 1180 : 980; shell.Height = size == 0 ? 760 : 640;
                for (var index = 0; index < 4; index++)
                {
                    tabs.SelectedIndex = index; Dispatcher.UIThread.RunJobs();
                    shell.UpdateLayout();
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)shell.Width, (int)shell.Height), new Vector(96, 96));
                    bitmap.Render(shell); bitmap.Save(Path.Combine(output, $"tab_{index}_{shell.Width:0}.png"));
                    Check(tabs.Bounds.Width > 280 && list.Bounds.Width > 500, $"Layout {index} at {shell.Width}");
                    var playerBar = shell.FindControl<Border>("PersistentPlayer")!;
                    var playerPoint = playerBar.TranslatePoint(new Point(0, 0), shell)!.Value;
                    Check(playerBar.IsEffectivelyVisible && playerPoint.Y >= 0 && playerPoint.Y + playerBar.Bounds.Height <= shell.Height,
                        $"Player remains visible on tab {index} at {shell.Width}");
                    Check(playerBar.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "30 秒試聽")),
                        $"Preview is accessible on tab {index} at {shell.Width}");
                    var inspector = shell.FindControl<Border>("SelectedFileInspector")!;
                    Check(inspector.IsEffectivelyVisible && inspector.Bounds.Width >= 490, $"File inspector stays usable at {shell.Width}");
                }
            }
            shell.Width = 1180; shell.Height = 760; Dispatcher.UIThread.RunJobs(); shell.UpdateLayout();
            var workspace = shell.FindControl<Grid>("WorkspaceGrid")!;
            var splitter = shell.FindControl<GridSplitter>("WorkspaceSplitter")!;
            var beforeResize = workspace.ColumnDefinitions[2].ActualWidth;
            var splitPoint = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), shell)!.Value;
            shell.MouseDown(splitPoint, MouseButton.Left); shell.MouseMove(splitPoint + new Vector(-40, 0)); shell.MouseUp(splitPoint + new Vector(-40, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs(); shell.UpdateLayout();
            Check(workspace.ColumnDefinitions[2].ActualWidth > beforeResize + 20, "Dragging the splitter gives settings more width");
            var outputTab = (TabItem)tabs.Items[2]!;
            Check(outputTab.GetLogicalDescendants().OfType<Button>().Any(b => Equals(b.Content, "輸出位置")), "Output destination is grouped with output settings");
            if (args.Length > 1)
            {
                var ffmpeg = Path.GetFullPath(args[1]);
                PlaybackChecks(ffmpeg, output);
                var cleanupChecks = CleanupTuningChecks.RunAsync(ffmpeg, output);
                PumpUntil(() => cleanupChecks.IsCompleted, 60000); cleanupChecks.GetAwaiter().GetResult();
                FolderChecks(ffmpeg, output);
                ProcessingChecks(window, ffmpeg, output);
                CleanupWorkflowChecks(ffmpeg, output);
            }
            shell.Close(); Console.WriteLine($"PASS: {_checks} cross-platform checks. Screenshots: {output}"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static object? Invoke(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Check(bool success, string description)
    { if (!success) throw new Exception(description); _checks++; Console.WriteLine($"PASS {description}"); }
    private static void PumpUntil(Func<bool> condition, int timeout = 5000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < timeout) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Check(condition(), "Async playback completes before timeout");
    }
    private static void PlaybackChecks(string ffmpeg, string output)
    {
        var audio = Path.Combine(output, "playback.wav");
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", audio }) info.ArgumentList.Add(argument);
        using (var generator = Process.Start(info)!) { generator.WaitForExit(); Check(generator.ExitCode == 0, "Generated playback fixture"); }
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        using var player = new FfplayAudioPlayer { ResolveFfmpeg = () => ffmpeg };
        bool opened = false, ended = false; Exception? failure = null;
        player.MediaOpened += (_, _) => opened = true; player.MediaEnded += (_, _) => ended = true;
        player.MediaFailed += (_, e) => failure = e.ErrorException;
        player.Open(new Uri(audio)); PumpUntil(() => opened || failure is not null);
        Check(failure is null && player.NaturalDuration.TimeSpan.TotalSeconds >= 1.9, "Player reads source duration");
        player.Play(); Thread.Sleep(150); player.Pause(); var pausedAt = player.Position;
        Thread.Sleep(150); Check(player.Position == pausedAt, "Pause holds position and stops decoder");
        Check(typeof(FfplayAudioPlayer).GetField("_process", Private)!.GetValue(player) is null, "Pause terminates audio output");
        player.Position = TimeSpan.FromSeconds(1.5); Check(player.Position.TotalSeconds == 1.5, "Seek remains paused");
        player.Play(); PumpUntil(() => ended || failure is not null);
        Check(ended && failure is null, "Resume from seek and natural end");
        player.Open(new Uri(Path.Combine(output, "missing.wav"))); PumpUntil(() => failure is not null);
        Check(failure is not null, "Missing media reports playback error");
        Check(player.Source is null, "Failed media does not remain loaded for subsequent play");
    }
    private static void FolderChecks(string ffmpeg, string output)
    {
        var nested = Path.Combine(output, "portable-assets", "source.wav");
        var relative = PodcastBatchCleaner.Core.Services.AudioWorkSessionFile.RelativePath(nested, output);
        Check(relative == "portable-assets/source.wav" && PodcastBatchCleaner.Core.Services.AudioWorkSessionFile.ResolvePath(relative, output) == nested,
            "Work paths use portable separators for nested source folders");
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null, [false], null)!;
        var a = Path.Combine(output, "folder_a"); var b = Path.Combine(output, "folder_b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.Copy(Path.Combine(output, "playback.wav"), Path.Combine(a, "a.wav"), true);
        File.Copy(Path.Combine(output, "playback.wav"), Path.Combine(b, "b.wav"), true);
        var first = (Task)Invoke(window, "LoadAudioFilesAsync", a)!;
        Check(!window.CanStartWork, "Folder scan blocks processing until its list is ready");
        var second = (Task)Invoke(window, "LoadAudioFilesAsync", b)!;
        PumpUntil(() => first.IsCompleted && second.IsCompleted);
        first.GetAwaiter().GetResult(); second.GetAwaiter().GetResult();
        Check(window.AudioFiles.Count == 1 && window.AudioFiles[0].Folder == b, "Latest folder scan wins without mixing old results");
        var outputField = typeof(MainWindow).GetField("_outputFolder", Private)!;
        Check((string?)outputField.GetValue(window) == Path.Combine(b, "processed"), "Default output belongs to the selected folder");
        var load = (Task)Invoke(window, "LoadAudioFilesAsync", a)!; PumpUntil(() => load.IsCompleted); load.GetAwaiter().GetResult();
        Check((string?)outputField.GetValue(window) == Path.Combine(a, "processed"), "Changing source moves an automatic output folder");
        var custom = Path.Combine(output, "custom_output"); outputField.SetValue(window, custom);
        load = (Task)Invoke(window, "LoadAudioFilesAsync", b)!; PumpUntil(() => load.IsCompleted); load.GetAwaiter().GetResult();
        Check((string?)outputField.GetValue(window) == custom, "Explicit custom output survives a source change");
        load = (Task)Invoke(window, "LoadAudioFilesAsync", Path.Combine(output, "absent_folder"))!;
        PumpUntil(() => load.IsCompleted); load.GetAwaiter().GetResult();
        Check(window.AudioFiles.Count == 1 && window.AudioFiles[0].Folder == b && window.CanStartWork,
            "A failed folder scan preserves the previous usable list");
        typeof(MainWindow).GetField("_ffmpegPath", Private)!.SetValue(window, ffmpeg);
        load = (Task)Invoke(window, "LoadDurationsAsync", window.AudioFiles.ToList())!;
        window.Close(); PumpUntil(() => load.IsCompleted);
        Check(typeof(MainWindow).GetField("_durationLoadingCancellation", Private)!.GetValue(window) is null,
            "Closing the window cancels and releases background duration probes");
    }
    private static void ProcessingChecks(MainWindow window, string ffmpeg, string output)
    {
        foreach (var item in window.AudioFiles)
        {
            File.Copy(Path.Combine(output, "playback.wav"), item.FilePath, overwrite: true);
            item.Duration = TimeSpan.FromSeconds(2);
        }
        typeof(MainWindow).GetField("_ffmpegPath", Private)!.SetValue(window, ffmpeg);
        typeof(MainWindow).GetField("_outputFolder", Private)!.SetValue(window, Path.Combine(output, "processed"));
        window.SelectedOutputFormat = PodcastBatchCleaner.Core.Services.FfmpegAudioProcessor.OutputFormats.First(f => f.Extension == ".wav");
        window.NormalizeLoudness = false; window.EnableDenoise = false; window.RemoveSilence = false;
        window.EnableThreeBandEq = true; window.EnableDeEsser = true;
        var first = window.AudioFiles.First(item => item.TrimStartText == "0.1");
        var task = (Task)Invoke(window, "ProcessItemsAsync", new List<AudioFileItem> { first })!;
        PumpUntil(() => task.IsCompleted, 30000); task.GetAwaiter().GetResult();
        Check(first.HasProcessedFile && first.Status == "完成", "Avalonia exports through the shared FFmpeg pipeline");
        var processor = new PodcastBatchCleaner.Core.Services.FfmpegAudioProcessor();
        var probe = processor.ProbeDurationAsync(ffmpeg, first.ProcessedPath!);
        PumpUntil(() => probe.IsCompleted);
        Check(Math.Abs(probe.GetAwaiter().GetResult()!.Value.TotalSeconds - 1.4) < .05, "Per-file trimming affects actual output duration");
        window.MergeSegments = true; window.MergedOutputFileName = "cross_episode.wav";
        task = (Task)Invoke(window, "ProcessItemsAsync", window.AudioFiles.ToList())!;
        PumpUntil(() => task.IsCompleted, 30000); task.GetAwaiter().GetResult();
        Check(window.CanReviewEpisode && window.EpisodeChapters.Count == 2, "Avalonia merge creates a playable episode with chapters");
        var expectedChapterStart = (window.AudioFiles[0] == first ? 1.4 : 2) + window.MergeGapSeconds;
        Check(Math.Abs(window.EpisodeChapters[1].StartSeconds - expectedChapterStart) < .05,
            "Chapter positions use the actual processed segment duration");
        window.PlaybackSourceIndex = 0;
        var list = window.GetVisualDescendants().OfType<DataGrid>().Single(); list.SelectedItem = first;
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();
        Invoke(window, "LoadWaveform_Click", window, new RoutedEventArgs());
        PumpUntil(() => !window.IsAnalyzing);
        var waveform = window.GetVisualDescendants().OfType<AudioWaveformView>().Single();
        Check(waveform.Waveform?.Peaks.Length > 0 && Math.Abs(waveform.TimeAt(waveform.Bounds.Width / 2) - 1) < .02,
            "Avalonia waveform renders decoded peaks and maps its original timeline");
        Invoke(window, "SeekOriginalWaveform", .4);
        Invoke(window, "SetTrimStartFromWaveform_Click", window, new RoutedEventArgs());
        Check(first.TrimStartText == "0.4", "Waveform cursor updates per-file trim");
        window.Width = 1180; window.Height = 760; Dispatcher.UIThread.RunJobs();
        using var frame = new RenderTargetBitmap(new PixelSize(1180, 760), new Vector(96, 96));
        frame.Render(window); frame.Save(Path.Combine(output, "waveform_episode_1180.png"));
        Invoke(window, "OpenSelectedForPlayback", false);
        var player = (FfplayAudioPlayer)typeof(MainWindow).GetField("_player", Private)!.GetValue(window)!;
        Check(player.Source?.LocalPath == first.FilePath, "Playback opens the selected original source");
        first.ProcessedPath = null; window.PlaybackSourceIndex = 1;
        Invoke(window, "OpenSelectedForPlayback", false);
        Check(player.Source is null && window.PlaybackDurationText == "00:00",
            "Missing processed audio clears the previous source and duration");
        window.PlaybackSourceIndex = 0; list.SelectedItem = null;
        Check(player.Source is null, "Clearing the selection cannot leave the previous audio loaded");
    }
    private static void CleanupWorkflowChecks(string ffmpeg, string output)
    {
        var source = Path.Combine(output, "cleanup_long_source.wav");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=35", source }) start.ArgumentList.Add(argument);
        using (var generator = Process.Start(start)!) { generator.WaitForExit(); Check(generator.ExitCode == 0, "Generated long isolated-effect fixture"); }
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null, [false], null)!;
        typeof(MainWindow).GetField("_ffmpegPath", Private)!.SetValue(window, ffmpeg);
        typeof(MainWindow).GetField("_outputFolder", Private)!.SetValue(window, output);
        window.Show();
        var item = new AudioFileItem { FilePath = source, TrimStartText = "1", TrimEndText = "34", ProcessedPath = source };
        window.AudioFiles.Add(item); window.GetVisualDescendants().OfType<DataGrid>().Single().SelectedItem = item;
        window.EnableAdvancedCleanup = true; window.FineDenoiseDb = 9.5; window.RoomFrequencyHz = 340;
        window.TailReleaseMs = 600; window.EnableDeepFilterNet = true; window.EnableVoiceFixerDereverb = true;
        window.UseAiProcessedAudio = true;
        window.VoiceThicknessDb = 3.5; window.VoiceClarityDb = 2.5; window.UseVoiceCompression = false;
        window.SelectedCleanupEffect = 3;
        var work = Path.Combine(output, "cleanup-settings.podcast-work.json"); Invoke(window, "SaveWorkSession", work);
        window.FineDenoiseDb = 2; window.EnableAdvancedCleanup = false;
        window.VoiceThicknessDb = 0; window.VoiceClarityDb = 0; window.UseVoiceCompression = true;
        Invoke(window, "LoadWorkSession", work);
        Check(window.EnableAdvancedCleanup && window.FineDenoiseDb == 9.5 && window.RoomFrequencyHz == 340 && window.TailReleaseMs == 600,
            "Advanced cleanup settings survive work-file restore");
        Check(window.VoiceThicknessDb == 3.5 && window.VoiceClarityDb == 2.5 && !window.UseVoiceCompression,
            "Voice tone settings survive work-file restore");
        item = window.AudioFiles.Single(); item.ProcessedPath = source;
        var comparison = (Task)Invoke(window, "ProcessCleanupComparisonAsync", item)!;
        PumpUntil(() => comparison.IsCompleted, 60000); comparison.GetAwaiter().GetResult();
        Check(window.CanPlayEffectPreview, "Single-effect preview works without configured AI tools or external AI audio");
        Check(item.ProcessedPath == source && window.EnableDeepFilterNet && window.EnableVoiceFixerDereverb && window.UseAiProcessedAudio,
            "Isolated preview preserves formal outputs and AI settings");
        var bypass = (string)typeof(MainWindow).GetField("_effectBypassPath", Private)!.GetValue(window)!;
        var effect = (string)typeof(MainWindow).GetField("_effectProcessedPath", Private)!.GetValue(window)!;
        var processor = new FfmpegAudioProcessor();
        var metrics = Task.WhenAll(processor.ProbeDurationAsync(ffmpeg, bypass), processor.ProbeDurationAsync(ffmpeg, effect));
        PumpUntil(() => metrics.IsCompleted); var durations = metrics.GetAwaiter().GetResult();
        Check(durations.All(duration => Math.Abs(duration!.Value.TotalSeconds - 30) < .01), "Both comparison versions use the same 30-second source range");
        var analysis = Task.WhenAll(processor.AnalyzeQualityAsync(ffmpeg, bypass), processor.AnalyzeQualityAsync(ffmpeg, effect));
        PumpUntil(() => analysis.IsCompleted, 15000); var levels = analysis.GetAwaiter().GetResult();
        Check(levels.All(level => Math.Abs(level.IntegratedLufs!.Value + 16) < .5), "Both comparison versions match -16 LUFS");
        Invoke(window, "PlayCleanupVersion", false);
        var player = (FfplayAudioPlayer)typeof(MainWindow).GetField("_player", Private)!.GetValue(window)!;
        PumpUntil(() => player.NaturalDuration.HasTimeSpan);
        player.Position = TimeSpan.FromSeconds(3);
        Invoke(window, "PlayCleanupVersion", true);
        PumpUntil(() => player.NaturalDuration.HasTimeSpan && player.Position.TotalSeconds >= 3);
        Check(player.Source?.LocalPath == effect && player.Position.TotalSeconds < 3.6, "Switching bypass/effect retains the comparison position");
        window.FineDenoiseDb = 10;
        Check(!window.CanPlayEffectPreview && player.Source is null, "Changing cleanup tuning invalidates and stops an old comparison");
        window.FineDenoiseDb = 9.5;
        Check(window.CanPlayEffectPreview, "Restoring identical settings can reuse the valid comparison");
        window.VoiceThicknessDb = 4;
        Check(!window.CanPlayEffectPreview, "Changing voice tone invalidates the comparison");
        window.VoiceThicknessDb = 3.5;
        Check(window.CanPlayEffectPreview, "Restoring voice tone reuses the matching comparison");
        var existingRuns = Directory.GetDirectories(Path.Combine(output, "effect_preview")).Length;
        var canceledComparison = (Task)Invoke(window, "ProcessCleanupComparisonAsync", item)!;
        Invoke(window, "CancelProcessing_Click", window, new RoutedEventArgs());
        PumpUntil(() => canceledComparison.IsCompleted, 15000); canceledComparison.GetAwaiter().GetResult();
        Check(window.CanPlayEffectPreview && Directory.GetDirectories(Path.Combine(output, "effect_preview")).Length == existingRuns,
            "Cancel removes only the new incomplete comparison and preserves the prior pair");
        item.TrimStartText = "2";
        Check(!window.CanPlayEffectPreview, "Changing a trim invalidates the comparison"); item.TrimStartText = "1";
        var modified = File.GetLastWriteTimeUtc(source);
        File.SetLastWriteTimeUtc(source, modified.AddSeconds(2));
        Invoke(window, "PlayCleanupVersion", true);
        Check(!window.CanPlayEffectPreview && player.Source is null, "An externally changed source cannot replay stale effect audio");
        File.SetLastWriteTimeUtc(source, modified);
        window.SelectedChannelMode = FfmpegAudioProcessor.KeepOriginalChannelMode;
        window.PresetName = "Cleanup test";
        var preset = (PortablePreset)Invoke(window, "CapturePortablePreset")!;
        window.SelectedPortablePreset = preset; window.FineDenoiseDb = 3; window.VoiceThicknessDb = 0;
        Invoke(window, "ApplyPortablePreset_Click", window, new RoutedEventArgs());
        Check(window.EnableAdvancedCleanup && window.FineDenoiseDb == 9.5 && window.TailReleaseMs == 600,
            "Desktop preset extensions restore fine cleanup controls");
        Check(window.VoiceThicknessDb == 3.5 && window.VoiceClarityDb == 2.5 && !window.UseVoiceCompression,
            "Desktop preset extensions restore voice tone controls");
        window.VolumeGainDb = 3;
        window.SelectedPortablePreset = preset with { Extensions = new() { ["desktop"] = JsonSerializer.SerializeToElement(new
        { EnableAdvancedCleanup = true, CleanupTuning = new { NoiseFloorDb = -999 } }) } };
        Invoke(window, "ApplyPortablePreset_Click", window, new RoutedEventArgs());
        Check(window.VolumeGainDb == 3 && window.FineDenoiseDb == 9.5 && window.StatusText.Contains("未套用"),
            "Malformed advanced preset cannot partially change active settings");
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex = 1;
        window.EnhanceVoiceEq = true;
        Dispatcher.UIThread.RunJobs();
        var expander = window.FindControl<Expander>("CleanupTuningExpander")!; expander.IsExpanded = true;
        foreach (var width in new[] { 1180, 980 })
        {
            window.Width = width; window.Height = width == 1180 ? 760 : 640;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var scroll = (ScrollViewer)((TabItem)tabs.Items[1]!).Content!;
            scroll.Offset = new Vector(0, 0); Dispatcher.UIThread.RunJobs();
            using (var frame = new RenderTargetBitmap(new PixelSize(width, (int)window.Height), new Vector(96, 96)))
            { frame.Render(window); frame.Save(Path.Combine(output, $"cleanup_compare_{width}.png")); }
            var point = expander.TranslatePoint(new Point(0, 0), scroll)!.Value;
            scroll.Offset = new Vector(0, point.Y); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var fine = new RenderTargetBitmap(new PixelSize(width, (int)window.Height), new Vector(96, 96));
            fine.Render(window); fine.Save(Path.Combine(output, $"cleanup_fine_{width}.png"));
            Check(expander.Bounds.Width > 200 && expander.Bounds.Width <= scroll.Bounds.Width, $"Expanded cleanup controls fit at {width}");
            var voiceControl = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "輕度壓縮"));
            var voicePoint = voiceControl.TranslatePoint(new Point(0, 0), scroll)!.Value;
            scroll.Offset = new Vector(0, scroll.Offset.Y + voicePoint.Y - 230); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var voiceFrame = new RenderTargetBitmap(new PixelSize(width, (int)window.Height), new Vector(96, 96));
            voiceFrame.Render(window); voiceFrame.Save(Path.Combine(output, $"voice_tone_{width}.png"));
            Check(voiceControl.Bounds.Width > 0 && voiceControl.Bounds.Width <= scroll.Bounds.Width, $"Voice controls fit at {width}");
        }
        window.Close();
    }
}
