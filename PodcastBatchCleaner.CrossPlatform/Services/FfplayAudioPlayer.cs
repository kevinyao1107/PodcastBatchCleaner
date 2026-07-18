using System.Diagnostics;

namespace PodcastBatchCleaner.CrossPlatform.Services;

public sealed class FfplayAudioPlayer : IDisposable
{
    private Process? _process;
    private string? _currentPath;
    private TimeSpan _startedAtPosition;
    private DateTimeOffset _startedAt;
    private bool _isPaused;

    public bool IsPlaying => _process is { HasExited: false } && !_isPaused;

    public TimeSpan Position
    {
        get
        {
            if (_process is null || _process.HasExited)
            {
                return TimeSpan.Zero;
            }

            return _isPaused
                ? _startedAtPosition
                : _startedAtPosition + (DateTimeOffset.Now - _startedAt);
        }
    }

    public async Task PlayAsync(string ffplayPath, string audioPath, TimeSpan? startAt = null)
    {
        Stop();

        _currentPath = audioPath;
        _startedAtPosition = startAt ?? TimeSpan.Zero;
        _startedAt = DateTimeOffset.Now;
        _isPaused = false;

        var startInfo = new ProcessStartInfo
        {
            FileName = ffplayPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-nodisp");
        startInfo.ArgumentList.Add("-autoexit");
        startInfo.ArgumentList.Add("-hide_banner");

        if (_startedAtPosition > TimeSpan.Zero)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(_startedAtPosition.TotalSeconds.ToString("0.###"));
        }

        startInfo.ArgumentList.Add(audioPath);

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 ffplay。");

        _ = Task.Run(() => DrainAsync(_process.StandardError));
        _ = Task.Run(() => DrainAsync(_process.StandardOutput));

        await Task.CompletedTask;
    }

    public void PauseOrResume()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        if (!_isPaused)
        {
            _startedAtPosition = Position;
        }
        else
        {
            _startedAt = DateTimeOffset.Now;
        }

        _isPaused = !_isPaused;

        try
        {
            _process.StandardInput.Write("p");
            _process.StandardInput.Flush();
        }
        catch
        {
            // ffplay may close stdin near the end of playback.
        }
    }

    public async Task SeekAsync(string ffplayPath, TimeSpan position)
    {
        if (_currentPath is null)
        {
            return;
        }

        await PlayAsync(ffplayPath, _currentPath, position);
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        _isPaused = false;
        _startedAtPosition = TimeSpan.Zero;

        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (!reader.EndOfStream)
            {
                await reader.ReadLineAsync();
            }
        }
        catch
        {
            // Best-effort output drain.
        }
    }
}
