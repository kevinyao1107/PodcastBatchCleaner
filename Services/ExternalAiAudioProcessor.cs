using System.Diagnostics;
using System.IO;
using System.Text;

namespace PodcastBatchCleaner.Core.Services;

public sealed class ExternalAiAudioProcessor
{
    public async Task ProcessAsync(
        string executablePath,
        string argumentsTemplate,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var renderedArguments = RenderArguments(argumentsTemplate, inputPath, outputPath);
        var startInfo = CreateStartInfo(executablePath, renderedArguments);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 AI 模型命令。");

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
            throw new InvalidOperationException($"AI 模型處理失敗，ExitCode={process.ExitCode}。{TrimMessage(detail)}");
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("AI 模型命令完成，但沒有產生輸出音檔。");
        }
    }

    public static bool HasRequiredPlaceholders(string argumentsTemplate)
    {
        return argumentsTemplate.Contains("{input}", StringComparison.OrdinalIgnoreCase)
            && argumentsTemplate.Contains("{output}", StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, string renderedArguments)
    {
        var extension = Path.GetExtension(executablePath);
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c {QuoteForCommand(executablePath)} {renderedArguments}";
            return startInfo;
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "powershell.exe";
            startInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteForCommand(executablePath)} {renderedArguments}";
            return startInfo;
        }

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "python";
            startInfo.Arguments = $"{QuoteForCommand(executablePath)} {renderedArguments}";
            return startInfo;
        }

        startInfo.FileName = executablePath;
        startInfo.Arguments = renderedArguments;
        return startInfo;
    }

    private static string RenderArguments(string template, string inputPath, string outputPath)
    {
        return template
            .Replace("{input}", EscapeArgumentValue(inputPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{output}", EscapeArgumentValue(outputPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeArgumentValue(string value)
    {
        return value.Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string QuoteForCommand(string value)
    {
        return $"\"{EscapeArgumentValue(value)}\"";
    }

    private static string TrimMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder();
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                normalized.Append(trimmed).Append(' ');
            }
        }

        return normalized.Length > 500 ? normalized.ToString(0, 500) : normalized.ToString();
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
