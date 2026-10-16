using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;
using PodcastBatchCleaner.CrossPlatform;

internal static partial class CrossPlatformChecks
{
    private static void SafetyWorkflowChecks(string ffmpeg, string output)
    {
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null, [false], null)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        T Run<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, 15000); return task.GetAwaiter().GetResult(); }
        void Answer(string? id) => Set("_safetyPromptOverride", new Func<string, string, IReadOnlyList<(string Id, string Text)>, Task<string?>>((_, _, _) => Task.FromResult(id)));
        Set("_ffmpegPath", ffmpeg);
        var exportFolder = Path.Combine(output, "safety-export-canceled"); Set("_outputFolder", exportFolder);
        var source = Path.Combine(output, "safety-source.wav");
        File.Copy(Path.Combine(output, "playback.wav"), source, true);
        var rendered = Path.Combine(output, "safety-render.wav"); File.Copy(source, rendered, true);
        var first = new AudioFileItem { FilePath = source, TrimStartText = ".1" };
        var second = new AudioFileItem { FilePath = Path.Combine(output, "playback.wav") };
        window.Show(); window.AudioFiles.Add(first); window.AudioFiles.Add(second);
        window.GetVisualDescendants().OfType<DataGrid>().Single().SelectedItem = first;
        var work = Path.Combine(output, "safety.podcast-work.json"); Invoke(window, "SaveWorkSession", work);
        Check(!window.HasUnsavedWork, "A successful manual save establishes a clean work baseline");
        first.Duration = TimeSpan.FromSeconds(2); first.Status = "完成";
        Check(!window.HasUnsavedWork, "Playback and processing status do not dirty a work file");
        window.VolumeGainDb = 2;
        Check(window.HasUnsavedWork, "Changing processing settings marks work unsaved"); window.VolumeGainDb = 0;
        Check(!window.HasUnsavedWork, "Reverting identical settings returns to the saved baseline");
        var before = File.ReadAllText(work);
        using var store = new WorkDraftStore(Path.Combine(output, "ui-drafts")); Set("_draftStore", store);
        first.TrimEndText = "1.8";
        Check((bool)Invoke(window, "FlushWorkDraft")!, "Edited work can be saved atomically as a recovery draft");
        Check(File.ReadAllText(work) == before && AudioWorkSessionFile.Read(store.DraftPath).Files[0].TrimEndText == "1.8",
            "Autosave never overwrites the manually saved work file");
        first.TrimStartText = ".15";
        typeof(MainWindow).GetProperty("IsProcessing")!.SetValue(window, true);
        Check(AudioWorkSessionFile.Read(store.DraftPath).Files[0].TrimStartText == ".15", "Starting a long processing job first protects the latest edits in its draft");
        typeof(MainWindow).GetProperty("IsProcessing")!.SetValue(window, false);
        first.TrimStartText = ".1"; Invoke(window, "FlushWorkDraft");
        Set("_interactiveSafety", true); Answer("cancel"); window.Close();
        Check(window.IsVisible && window.HasUnsavedWork, "Canceling the real window close keeps the workspace open and dirty");
        Answer(null);
        Check(!Run((Task<bool>)Invoke(window, "ConfirmLeaveWorkAsync", true)!) && window.HasUnsavedWork,
            "Closing a confirmation without choosing preserves unsaved work");
        Answer("save"); Set("_saveWorkPathOverride", new Func<Task<string?>>(() => Task.FromResult<string?>(null)));
        Check(!Run((Task<bool>)Invoke(window, "ConfirmLeaveWorkAsync", true)!), "Canceling the save picker cancels closing");
        Set("_saveWorkPathOverride", new Func<Task<string?>>(() => Task.FromResult<string?>(Path.Combine(output, "missing-parent", "work.json"))));
        Check(!Run((Task<bool>)Invoke(window, "ConfirmLeaveWorkAsync", true)!) && window.HasUnsavedWork,
            "Failed manual saving cannot permit closing or discard current work");
        Set("_saveWorkPathOverride", new Func<Task<string?>>(() => Task.FromResult<string?>(work)));
        Check(Run((Task<bool>)Invoke(window, "ConfirmLeaveWorkAsync", true)!) && !window.HasUnsavedWork,
            "Saving from the close guard clears dirty state only after success");
        first.TrimStartText = ".2"; Answer("draft");
        Check(Run((Task<bool>)Invoke(window, "ConfirmLeaveWorkAsync", true)!) && File.Exists(store.DraftPath) && window.HasUnsavedWork,
            "Keep-draft close retains a recoverable copy without pretending the work file was saved");
        Answer("discard");
        Check(Run((Task<bool>)Invoke(window, "ConfirmLeaveWorkAsync", true)!) && !File.Exists(store.DraftPath),
            "Explicit discard removes only this window's draft");
        Set("_discardOnSafetyClose", false);
        first.ProcessedPath = rendered;
        var renderKey = (string)Invoke(window, "CaptureRenderKey", first, "完整輸出")!;
        Invoke(window, "RememberRenderedResult", first, renderKey, "完整輸出");
        Check(!window.HasResultWarning && window.ProcessedResultText.Contains("符合"), "A recorded full output identifies its current settings");
        window.VolumeGainDb = 3;
        Check(window.HasResultWarning && window.ProcessedResultText.Contains("先前的完整輸出"), "Changing gain marks the old output stale without deleting it");
        foreach (var width in new[] { 1180, 980 })
        {
            window.Width = width; window.Height = width == 1180 ? 760 : 640;
            Invoke(window, "RefreshSafetyUi"); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var staleFrame = new RenderTargetBitmap(new PixelSize(width, (int)window.Height), new Vector(96, 96));
            staleFrame.Render(window); staleFrame.Save(Path.Combine(output, $"stale_result_{width}.png"));
            var bar = window.FindControl<Border>("PersistentPlayer")!;
            var point = bar.TranslatePoint(new Point(), window)!.Value;
            Check(point.Y + bar.Bounds.Height <= window.Height, $"Stale-result warning preserves the player at {width}");
        }
        window.VolumeGainDb = 0; Check(!window.HasResultWarning, "Identical restored render settings make the existing output current again");
        var stamp = File.GetLastWriteTimeUtc(rendered); File.SetLastWriteTimeUtc(rendered, stamp.AddSeconds(2));
        Check(window.HasResultWarning, "Externally modified output is never labeled current"); File.SetLastWriteTimeUtc(rendered, stamp);
        first.TrimEndText = "1.7"; Check(window.HasResultWarning, "Per-file trim changes invalidate render provenance"); first.TrimEndText = "1.8";
        var key = (string)Invoke(window, "CaptureRenderKey", first, "30 秒試聽")!;
        Invoke(window, "RememberRenderedResult", first, key, "30 秒試聽");
        Check(window.ProcessedResultText.StartsWith("30 秒試聽"), "A short preview cannot masquerade as a full output");
        Invoke(window, "SetEpisodePreview", rendered, new List<EpisodeChapter> { new("part", 0, 2) });
        var items = window.AudioFiles.ToList();
        var episodeKey = (string)Invoke(window, "CaptureEpisodeKey", items)!;
        Invoke(window, "RememberEpisodeResult", items, episodeKey, rendered); window.PlaybackSourceIndex = 2;
        Check(!window.HasResultWarning, "Episode provenance can verify its ordered sources");
        window.AudioFiles.Move(0, 1); Check(window.HasResultWarning, "Reordering segments marks the existing episode stale"); window.AudioFiles.Move(1, 0);
        var sameSourceClip = new AudioFileItem { FilePath = source, TrimStartText = ".4" };
        window.AudioFiles.Add(sameSourceClip);
        var repeatedSource = new List<AudioFileItem> { first, sameSourceClip };
        var repeatedKey = (string)Invoke(window, "CaptureEpisodeKey", repeatedSource)!;
        Invoke(window, "RememberEpisodeResult", repeatedSource, repeatedKey, rendered);
        Check(!window.HasResultWarning, "Repeated source recordings retain distinct clip provenance");
        window.AudioFiles.Move(2, 0);
        Check(window.HasResultWarning, "Reordering different trims of the same recording invalidates an old episode");
        window.AudioFiles.Remove(sameSourceClip);
        Invoke(window, "RememberEpisodeResult", items, episodeKey, rendered);
        window.PodcastTitle = "changed"; Check(window.HasResultWarning, "Episode metadata edits mark the previous master stale");
        window.PlaybackSourceIndex = 1;
        string summary = "";
        Set("_safetyPromptOverride", new Func<string, string, IReadOnlyList<(string Id, string Text)>, Task<string?>>((_, message, _) =>
        { summary = message; Check(!window.CanStartWork, "Export confirmation blocks competing work"); return Task.FromResult<string?>("cancel"); }));
        var processing = (Task)Invoke(window, "ProcessItemsAsync", new List<AudioFileItem> { first })!;
        PumpUntil(() => processing.IsCompleted); processing.GetAwaiter().GetResult();
        Check(!Directory.Exists(exportFolder) && !window.IsProcessing, "Canceling export creates no output directory or processing job");
        Check(summary.Contains("1 個（清單共 2 個）") && summary.Contains(exportFolder) && summary.Contains("音量增益"),
            "Export summary shows the actual subset, destination and enabled settings");
        Set("_safetyPromptOverride", new Func<string, string, IReadOnlyList<(string Id, string Text)>, Task<string?>>((_, _, _) =>
        { window.VolumeGainDb = 1; return Task.FromResult<string?>("export"); }));
        Check(!Run((Task<bool>)Invoke(window, "ConfirmExportAsync", new List<AudioFileItem> { first })!),
            "Changed settings during confirmation require a new confirmation");
        window.VolumeGainDb = double.NaN; Invoke(window, "RefreshSafetyUi");
        Check(!Run((Task<bool>)Invoke(window, "ConfirmExportAsync", new List<AudioFileItem> { first })!),
            "Invalid numeric input cannot crash dirty-state tracking or start an export");
        window.VolumeGainDb = 0; Answer("recover");
        var recoveryFolder = Path.Combine(output, "recovery-drafts");
        var oldStore = new WorkDraftStore(recoveryFolder);
        oldStore.Write(AudioWorkSessionFile.Read(work)); oldStore.Dispose();
        store.Dispose();
        var newStore = new WorkDraftStore(recoveryFolder); Set("_draftStore", newStore);
        Check(Run((Task<bool>)Invoke(window, "TryRecoverWorkDraftAsync")!) && window.HasUnsavedWork && window.AudioFiles.Count == 2,
            "Recovery restores editing data and still requires an explicit work-file save");
        Check(File.Exists(newStore.DraftPath) && !File.Exists(oldStore.DraftPath), "Recovery hands off to a new draft only after the replacement is safely written");
        Invoke(window, "SaveWorkSession", work); Check(!File.Exists(newStore.DraftPath), "Saving recovered work removes its now-obsolete draft");
        var promptType = typeof(MainWindow).Assembly.GetType("PodcastBatchCleaner.CrossPlatform.SafetyDecisionDialog")!;
        Window? decisionWindow = null;
        using (Window.WindowOpenedEvent.AddClassHandler<Window>((opened, _) => { if (opened != window) decisionWindow = opened; }))
        {
            var dialogTask = (Task<string?>)promptType.GetMethod("ShowAsync")!.Invoke(null,
                [window, "確認輸出", summary, new (string, string)[] { ("export", "開始輸出"), ("cancel", "返回調整") }])!;
            Dispatcher.UIThread.RunJobs(); Check(decisionWindow is not null, "Export confirmation is an actual owned desktop dialog");
            using var frame = new RenderTargetBitmap(new PixelSize(620, 440), new Vector(96, 96));
            frame.Render(decisionWindow!); frame.Save(Path.Combine(output, "export_confirmation.png"));
            decisionWindow!.Close(); Check(Run(dialogTask) is null, "Closing the real dialog defaults to cancellation");
        }
        window.Close();
        foreach (var choice in new[] { "draft", "discard" })
        {
            var closingWindow = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null, [false], null)!;
            var ownedStore = new WorkDraftStore(Path.Combine(output, "actual-close-" + choice));
            typeof(MainWindow).GetField("_draftStore", Private)!.SetValue(closingWindow, ownedStore);
            typeof(MainWindow).GetField("_interactiveSafety", Private)!.SetValue(closingWindow, true);
            typeof(MainWindow).GetField("_safetyPromptOverride", Private)!.SetValue(closingWindow,
                new Func<string, string, IReadOnlyList<(string Id, string Text)>, Task<string?>>((_, _, _) => Task.FromResult<string?>(choice)));
            closingWindow.Show(); closingWindow.AudioFiles.Add(new AudioFileItem { FilePath = source });
            Invoke(closingWindow, "FlushWorkDraft"); closingWindow.Close();
            PumpUntil(() => !closingWindow.IsVisible);
            Check(File.Exists(ownedStore.DraftPath) == (choice == "draft"), $"Actual {choice} close applies the correct draft lifetime without reentrant closing");
        }
    }
}
