using System.IO;

namespace PodcastBatchCleaner.Core.Services;

internal static class BundledAudioToolLocator
{
    public static string? Find(string toolFolder, string[] fileNames, string? baseDirectory)
    {
        DirectoryInfo? directory = new DirectoryInfo(baseDirectory ?? AppContext.BaseDirectory);
        foreach (var fileName in fileNames)
        {
            var adjacent = Path.Combine(directory.FullName, fileName);
            if (File.Exists(adjacent)) return adjacent;
        }

        // Build output is several levels below the repository's bundled tools.
        for (; directory is not null; directory = directory.Parent)
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(directory.FullName, "tools", toolFolder, fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
