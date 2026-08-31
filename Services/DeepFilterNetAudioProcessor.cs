using System.Diagnostics;
using System.IO;

namespace PodcastBatchCleaner.Core.Services;

public sealed class DeepFilterNetAudioProcessor
{
    public async Task<string> ProcessAsync(
        string deepFilterPath,
        string inputWavPath,
        string outputFolder,
        bool enablePostFilter,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);

        var startInfo = new ProcessStartInfo
        {
            FileName = deepFilterPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        if (enablePostFilter)
        {
            startInfo.ArgumentList.Add("--pf");
        }

        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputFolder);
        startInfo.ArgumentList.Add(inputWavPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 DeepFilterNet。");

        TrySetBackgroundPriority(process);

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"DeepFilterNet 處理失敗，ExitCode={process.ExitCode}。{TrimMessage(detail)}");
        }

        return FindOutputPath(inputWavPath, outputFolder)
            ?? throw new InvalidOperationException("DeepFilterNet 已完成，但找不到輸出 WAV。");
    }

    private static string? FindOutputPath(string inputWavPath, string outputFolder)
    {
        var inputName = Path.GetFileName(inputWavPath);
        var directMatch = Path.Combine(outputFolder, inputName);
        if (File.Exists(directMatch))
        {
            return directMatch;
        }

        var baseName = Path.GetFileNameWithoutExtension(inputWavPath);
        return Directory
            .EnumerateFiles(outputFolder, "*.wav", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileNameWithoutExtension(path).Contains(baseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string TrimMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length > 500 ? normalized[..500] : normalized;
    }

    private static void TrySetBackgroundPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
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
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
