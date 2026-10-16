using System.IO;
using System.Text.Json;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;

internal static class WorkDraftChecks
{
    public static void Run(string root)
    {
        void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var folder = Path.Combine(root, "draft-store-checks"); Directory.CreateDirectory(folder);
        var work = new AudioWorkSession(AudioWorkSessionFile.Format, 1, JsonSerializer.SerializeToElement(new { Gain = 2 }),
            [new WorkAudioFile("recording.wav", "episode", ".1", "2")]);
        var a = new WorkDraftStore(folder); a.Write(work);
        using var b = new WorkDraftStore(folder);
        Check(b.FindLatestRecoverable() is null, "An active window's draft cannot be recovered by another instance");
        var first = a.DraftPath; a.Dispose();
        var corrupt = Path.Combine(folder, "session-corrupt.podcast-work.json"); File.WriteAllText(corrupt, "broken");
        using (var recovery = b.FindLatestRecoverable())
        {
            Check(recovery?.Path == first, "Recovery skips corrupt drafts and finds a released valid session");
            using var c = new WorkDraftStore(folder);
            Check(c.FindLatestRecoverable() is null, "A recovery lease prevents concurrent recovery of the same draft");
            b.Write(AudioWorkSessionFile.Read(first));
            Check(AudioWorkSessionFile.Read(b.DraftPath).Files[0].TrimStartText == ".1", "Drafts preserve editing data without embedding audio");
            var before = File.ReadAllText(b.DraftPath);
            try { b.Write(work with { SchemaVersion = 99 }); throw new Exception("Invalid draft accepted"); }
            catch (InvalidDataException) { Check(File.ReadAllText(b.DraftPath) == before, "A failed draft write preserves the last valid draft"); }
            recovery!.Delete();
            Check(!File.Exists(first) && File.Exists(corrupt) && File.Exists(b.DraftPath), "Recovery removes only its selected draft, not other sessions or corrupt evidence");
        }
        b.Delete();
    }
}
