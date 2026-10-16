using System.IO;
using PodcastBatchCleaner.Core.Models;

namespace PodcastBatchCleaner.Core.Services;

public sealed class WorkDraftStore : IDisposable
{
    private readonly FileStream _lease;
    public string DirectoryPath { get; }
    public string DraftPath { get; }
    public WorkDraftStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        DraftPath = Path.Combine(DirectoryPath, $"session-{Guid.NewGuid():N}.podcast-work.json");
        _lease = Lock(DraftPath);
    }
    private static FileStream Lock(string path) => new(path + ".lock", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    public void Write(AudioWorkSession work) => AudioWorkSessionFile.Write(DraftPath, work);
    public void Delete() { if (File.Exists(DraftPath)) File.Delete(DraftPath); }
    public DraftRecovery? FindLatestRecoverable()
    {
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "session-*.podcast-work.json")
            .Where(path => path != DraftPath).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            FileStream? lease = null;
            try
            {
                lease = Lock(file);
                AudioWorkSessionFile.Read(file);
                return new DraftRecovery(file, lease);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            { lease?.Dispose(); }
        }
        return null;
    }
    public void Dispose() => _lease.Dispose();
}

public sealed class DraftRecovery(string path, FileStream lease) : IDisposable
{
    public string Path { get; } = path;
    public void Delete() { if (File.Exists(Path)) File.Delete(Path); }
    public void Dispose() => lease.Dispose();
}
