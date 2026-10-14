using System.Diagnostics;
using System.IO;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;

internal static class WaveformChecks
{
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
    public static async Task RunAsync(string ffmpeg, string root)
    {
        var builder = new AudioWaveformBuilder();
        const int frames = 10_000_001;
        for (var i = 0; i < frames; i++) builder.AddFrame(i == frames - 1 ? .9f : 0);
        var longData = builder.Build(48000);
        Check(longData.Peaks.Length <= 1024 && Math.Abs(longData.DurationSeconds - (double)frames / 48000) < 1e-10
            && longData.Peaks[^1] == .9f && longData.Peaks.Take(longData.Peaks.Length-1).All(p => p == 0),
            "Long waveform stays bounded and retains a transient in the partial final bin");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity })
        {
            try { builder.AddFrame(invalid); throw new Exception("Invalid PCM accepted"); }
            catch (InvalidDataException) { Check(true, "Reject non-finite PCM"); }
        }
        var path = Path.Combine(root, "antiphase waveform.wav");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "aevalsrc=0.8*sin(2*PI*440*t)*lt(t\\,1)|-0.8*sin(2*PI*440*t)*lt(t\\,1):s=48000:d=3", "-c:a", "pcm_f32le", path }) start.ArgumentList.Add(arg);
        using (var p = Process.Start(start)!)
        {
            var log = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new Exception(await log); await log;
        }
        var processor = new FfmpegAudioProcessor();
        var waveform = await processor.ReadWaveformAsync(ffmpeg, path);
        Check(Math.Abs(waveform.DurationSeconds - 3) < .0001 && waveform.Peaks.Length <= 1024,
            "Real stereo decode measures the full source duration");
        Check(waveform.Peaks.Take((int)(.9 / waveform.BinSeconds)).Max() > .79f,
            "Opposite-phase stereo remains visible without channel cancellation");
        Check(waveform.Peaks.Skip((int)(1.1 / waveform.BinSeconds)).All(p => p == 0),
            "Silent source tail remains at the correct waveform position");
        var compressed = Path.Combine(root, "waveform.mp3");
        start.ArgumentList.Clear();
        foreach (var arg in new[] { "-y", "-i", path, "-c:a", "libmp3lame", compressed }) start.ArgumentList.Add(arg);
        using (var p = Process.Start(start)!) { var log = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); if (p.ExitCode != 0) throw new Exception(await log); await log; }
        Check(Math.Abs((await processor.ReadWaveformAsync(ffmpeg, compressed)).DurationSeconds - 3) < .01,
            "Compressed source waveform uses decoded duration rather than container padding");
        var bad = Path.Combine(root, "invalid_waveform.wav"); await File.WriteAllTextAsync(bad, "invalid audio");
        try { await processor.ReadWaveformAsync(ffmpeg, bad); throw new Exception("Invalid audio accepted"); }
        catch (InvalidOperationException) { Check(true, "Corrupt audio reports waveform failure"); }
    }
}
