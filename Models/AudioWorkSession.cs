using System.Text.Json;
namespace PodcastBatchCleaner.Core.Models;

public sealed record WorkAudioFile(string FilePath, string CustomOutputFileName, string TrimStartText, string TrimEndText);
public sealed record AudioWorkSession(string Format, int SchemaVersion, JsonElement Settings, List<WorkAudioFile> Files);
public sealed record EpisodeChapter(string Name, double StartSeconds, double EndSeconds)
{
    public string DisplayText => $"{(long)(StartSeconds / 3600):00}:{(int)(StartSeconds / 60) % 60:00}:{StartSeconds % 60:00.###} · {Name}";
}
