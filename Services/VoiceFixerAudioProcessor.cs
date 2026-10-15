using System.Diagnostics;
using System.IO;

namespace PodcastBatchCleaner.Core.Services;

public sealed class VoiceFixerAudioProcessor
{
    public static string? TryFindVoiceFixer(string? baseDirectory = null)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "voicefixer.exe", "voicefixer.cmd", "voicefixer.bat", "voicefixer" }
            : new[] { "voicefixer", "voicefixer.sh" };
        foreach (var fileName in names)
        {
            var fromPath = FindInPath(fileName);
            if (fromPath is not null)
            {
                return fromPath;
            }
        }

        return BundledAudioToolLocator.Find("VoiceFixer", names, baseDirectory);
    }

    public async Task<string> ProcessAsync(
        string voiceFixerPath,
        string inputWavPath,
        string outputPath,
        int mode,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var isCommandScript = string.Equals(Path.GetExtension(voiceFixerPath), ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(voiceFixerPath), ".bat", StringComparison.OrdinalIgnoreCase);
        if (isCommandScript && !OperatingSystem.IsWindows())
            throw new InvalidOperationException("macOS / Linux 請指定可執行的 VoiceFixer 啟動腳本；不能使用 Windows cmd / bat。");
        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript ? "cmd.exe" : voiceFixerPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        if (isCommandScript)
        {
            // cmd needs an outer quote pair; environment values avoid interpreting '%' in paths.
            startInfo.Environment["PBC_VOICEFIXER_TOOL"] = Path.GetFullPath(voiceFixerPath);
            startInfo.Environment["PBC_VOICEFIXER_INPUT"] = Path.GetFullPath(inputWavPath);
            startInfo.Environment["PBC_VOICEFIXER_OUTPUT"] = Path.GetFullPath(outputPath);
            startInfo.Arguments = "/d /v:off /s /c \"\"%PBC_VOICEFIXER_TOOL%\" --infile \"%PBC_VOICEFIXER_INPUT%\" --outfile \"%PBC_VOICEFIXER_OUTPUT%\" --mode "
                + Math.Clamp(mode, 0, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
        }
        else
        {
            startInfo.ArgumentList.Add("--infile");
            startInfo.ArgumentList.Add(inputWavPath);
            startInfo.ArgumentList.Add("--outfile");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add(Math.Clamp(mode, 0, 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 VoiceFixer，請確認 voicefixer.exe 路徑正確，且 Python 環境已安裝 VoiceFixer。");

        TrySetBackgroundPriority(process);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"VoiceFixer 處理失敗，ExitCode={process.ExitCode}。請確認已安裝 VoiceFixer，且輸入音檔可以轉成 WAV。{TrimMessage(detail)}");
        }

        return File.Exists(outputPath)
            ? outputPath
            : throw new InvalidOperationException("VoiceFixer 已完成，但找不到輸出 WAV。請確認 --outfile 參數是否被目前版本支援。");
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

    private static string? FindInPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        return pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, fileName))
            .FirstOrDefault(File.Exists);
    }
}
