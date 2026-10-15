using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using PodcastBatchCleaner.Core.Services;

namespace PodcastBatchCleaner.CrossPlatform.Services;

public sealed class AudioPlaybackErrorEventArgs(Exception error) : EventArgs
{ public Exception ErrorException { get; } = error; }
public readonly record struct AudioDuration(TimeSpan TimeSpan)
{ public bool HasTimeSpan => TimeSpan > TimeSpan.Zero; }

public sealed class FfplayAudioPlayer : IDisposable
{
    private readonly FfmpegAudioProcessor _processor = new();
    private readonly Stopwatch _elapsed = new();
    private Process? _process;
    private CancellationTokenSource? _opening;
    private TimeSpan _position;
    private bool _ready, _wantPlay;
    public Func<string?>? ResolveFfmpeg { get; set; }
    public Uri? Source { get; private set; }
    public AudioDuration NaturalDuration { get; private set; }
    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<AudioPlaybackErrorEventArgs>? MediaFailed;
    public TimeSpan Position
    {
        get => _position + _elapsed.Elapsed;
        set
        {
            var playing = _wantPlay;
            StopProcess();
            _position = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0,
                NaturalDuration.HasTimeSpan ? NaturalDuration.TimeSpan.TotalSeconds : double.MaxValue));
            if (playing && _ready) StartProcess();
        }
    }
    public void Open(Uri source)
    {
        Close(); Source = source;
        var cancellation = new CancellationTokenSource(); _opening = cancellation;
        _ = OpenAsync(source, cancellation);
    }
    private async Task OpenAsync(Uri source, CancellationTokenSource cancellation)
    {
        try
        {
            var ffmpeg = ResolveFfmpeg?.Invoke() ?? FfmpegAudioProcessor.TryFindFfmpeg()
                ?? throw new FileNotFoundException("播放前請指定 FFmpeg（需包含 ffplay）。");
            var duration = await _processor.ProbeDurationAsync(ffmpeg, source.LocalPath, cancellation.Token);
            if (cancellation.IsCancellationRequested || _opening != cancellation) return;
            NaturalDuration = new AudioDuration(duration ?? TimeSpan.Zero); _ready = true;
            MediaOpened?.Invoke(this, EventArgs.Empty);
            if (_wantPlay && _process is null) StartProcess();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_opening != cancellation) return;
            _wantPlay = false; _ready = false; Source = null; NaturalDuration = default;
            MediaFailed?.Invoke(this, new(ex));
        }
    }
    public void Play() { _wantPlay = true; if (_ready && _process is null) StartProcess(); }
    public void Pause() { _position = Position; _wantPlay = false; StopProcess(); }
    public void Stop() { _wantPlay = false; StopProcess(); _position = TimeSpan.Zero; }
    public void Close()
    {
        _opening?.Cancel(); _opening?.Dispose(); _opening = null;
        Stop(); Source = null; _ready = false; NaturalDuration = default;
    }
    private string FindFfplay()
    {
        var name = OperatingSystem.IsWindows() ? "ffplay.exe" : "ffplay";
        var ffmpeg = ResolveFfmpeg?.Invoke();
        if (ffmpeg is not null && File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg)!, name)))
            return Path.Combine(Path.GetDirectoryName(ffmpeg)!, name);
        return BundledAudioToolLocator.Find("FFmpeg", [name], null)
            ?? (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists)
            ?? new[] { "/opt/homebrew/bin/ffplay", "/usr/local/bin/ffplay", "/usr/bin/ffplay" }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("找不到 ffplay，請安裝完整 FFmpeg 工具組。");
    }
    private void StartProcess()
    {
        if (Source is null) return;
        try
        {
            var info = new ProcessStartInfo { FileName = FindFfplay(), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-nodisp", "-autoexit", "-hide_banner", "-loglevel", "error", "-ss",
                _position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", Source.LocalPath })
                info.ArgumentList.Add(argument);
            var process = Process.Start(info) ?? throw new InvalidOperationException("無法啟動 ffplay。");
            _process = process; _elapsed.Restart();
            _ = ObserveExitAsync(process);
        }
        catch (Exception ex) { _wantPlay = false; MediaFailed?.Invoke(this, new(ex)); }
    }
    private async Task ObserveExitAsync(Process process)
    {
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var error = await errorTask; await outputTask;
            var exitCode = process.ExitCode;
            Dispatcher.UIThread.Post(() =>
            {
                if (_process != process) return;
                _process = null; _elapsed.Reset(); _wantPlay = false; process.Dispose();
                if (exitCode == 0) MediaEnded?.Invoke(this, EventArgs.Empty);
                else MediaFailed?.Invoke(this, new(new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "播放器無法輸出音訊。" : error.Trim())));
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException) { }
    }
    private void StopProcess()
    {
        _elapsed.Reset(); var process = _process; _process = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    public void Dispose() => Close();
}
