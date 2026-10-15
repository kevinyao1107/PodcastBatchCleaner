using System.IO;
using System.Text.Json;
using PodcastBatchCleaner.Core.Models;
namespace PodcastBatchCleaner.Core.Services;

public static class AudioWorkSessionFile
{
    public const string Format = "podcast-cleaner-work";
    public const int MaximumBytes = 2 * 1024 * 1024;
    public static AudioWorkSession Read(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("工作檔最大 2 MB。");
        try
        {
            var work = JsonSerializer.Deserialize<AudioWorkSession>(File.ReadAllText(path))
                ?? throw new InvalidDataException("工作檔沒有內容。");
            Validate(work);
            return work;
        }
        catch (JsonException ex) { throw new InvalidDataException("工作檔 JSON 格式不正確。", ex); }
    }
    public static void Validate(AudioWorkSession work)
    {
        if (work.Format != Format || work.SchemaVersion != 1 || work.Settings.ValueKind != JsonValueKind.Object ||
            work.Files is null || work.Files.Count > 2000)
            throw new InvalidDataException("不支援此工作檔版本或內容（最多 2000 個音檔）。");
        foreach (var file in work.Files)
            if (file is null || string.IsNullOrWhiteSpace(file.FilePath) || file.FilePath.Length > 32768 ||
                file.CustomOutputFileName is null || file.TrimStartText is null || file.TrimEndText is null ||
                file.CustomOutputFileName.Length > 256 || file.TrimStartText.Length > 128 || file.TrimEndText.Length > 128)
                throw new InvalidDataException("工作檔音檔欄位不正確。");
    }
    public static void Write(string path, AudioWorkSession work)
    {
        Validate(work);
        var json = JsonSerializer.Serialize(work, new JsonSerializerOptions { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("工作檔最大 2 MB。");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, json); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string RelativePath(string path, string projectFolder)
        => Path.GetRelativePath(projectFolder, path).Replace(Path.DirectorySeparatorChar, '/');

    public static string ResolvePath(string path, string projectFolder)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) throw new InvalidDataException("來源路徑不正確。");
        try
        {
            var resolved = Path.GetFullPath(path, projectFolder);
            // Older Windows work files used backslashes for relative source and attachment paths.
            if (!OperatingSystem.IsWindows() && path.Contains('\\') && !File.Exists(resolved) && !Directory.Exists(resolved))
                return Path.GetFullPath(path.Replace('\\', '/'), projectFolder);
            return resolved;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("來源路徑不正確。", ex); }
    }
    public static List<EpisodeChapter> BuildChapters(IReadOnlyList<(string Name, TimeSpan Duration)> segments, double gapSeconds)
    {
        if (!double.IsFinite(gapSeconds) || gapSeconds < 0 || gapSeconds > 10) throw new InvalidDataException("段落間隔需為 0～10 秒。");
        // Merge staging and its silence WAV use 44.1 kHz; the FFmpeg time argument has millisecond precision.
        var gap = Math.Round(Math.Round(gapSeconds, 3) * 44100) / 44100;
        var chapters = new List<EpisodeChapter>();
        double cursor = 0;
        foreach (var segment in segments)
        {
            if (segment.Duration <= TimeSpan.Zero) throw new InvalidDataException("無法判定處理後段落長度。");
            if (chapters.Count > 0) cursor += gap;
            var end = cursor + segment.Duration.TotalSeconds;
            chapters.Add(new EpisodeChapter(segment.Name, cursor, end)); cursor = end;
        }
        return chapters;
    }
}
