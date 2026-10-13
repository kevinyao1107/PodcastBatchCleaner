using System.IO;

namespace PodcastBatchCleaner.Core.Services;

public sealed record AudioBlendResult(string Path, string? Warning);

public sealed class AudioBlendProcessor
{
    private readonly FfmpegAudioProcessor _processor = new();

    public async Task<AudioBlendResult> BlendAsync(string ffmpegPath, string originalPath,
        string restoredPath, string outputPath, double mix, CancellationToken cancellationToken)
    {
        if (mix <= 0) return new(originalPath, null);
        if (mix >= 1) return new(restoredPath, null);
        var directory = Path.Combine(Path.GetDirectoryName(outputPath)!, $".alignment_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var duration = ReadPcm16(originalPath, readSamples: false).Duration;
            if (duration < 3) return new(restoredPath, "片段少於 3 秒，無法可靠對齊；使用純修復版本。");
            var offsets = duration > 18 ? new[] { 0d, Math.Max(0, duration / 2 - 6) } : new[] { 0d };
            var estimates = new List<(double Delay, double Gain)>();
            foreach (var offset in offsets)
            {
                var dry = Path.Combine(directory, "dry.wav");
                var wet = Path.Combine(directory, "wet.wav");
                await _processor.PrepareAlignmentProbeAsync(ffmpegPath, originalPath, dry, offset, cancellationToken);
                await _processor.PrepareAlignmentProbeAsync(ffmpegPath, restoredPath, wet, offset, cancellationToken);
                var estimate = await Task.Run(() => Estimate(ReadPcm16(dry).Samples,
                    ReadPcm16(wet).Samples, cancellationToken), cancellationToken);
                if (estimate is null)
                    return new(restoredPath, "原音與修復波形相似度不足，未混合以避免重影；使用純修復版本。");
                estimates.Add(estimate.Value);
            }
            if (estimates.Any(value => Math.Abs(value.Delay - estimates[0].Delay) > 0.005))
                return new(restoredPath, "修復音檔的時間偏移不一致，未混合；使用純修復版本。");
            var delay = estimates.Average(value => value.Delay);
            var gain = estimates.Average(value => value.Gain);
            await _processor.MixAlignedAudioAsync(ffmpegPath, originalPath, restoredPath,
                outputPath, mix, delay, gain, duration, cancellationToken);
            return new(outputPath, null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (double Delay, double Gain)? Estimate(short[] dry, short[] wet, CancellationToken token)
    {
        const int block = 80;
        double[] Envelope(short[] samples) => Enumerable.Range(0, samples.Length / block)
            .Select(index => Math.Sqrt(samples.Skip(index * block).Take(block)
                .Average(sample => (double)sample * sample))).ToArray();
        var dryEnvelope = Envelope(dry);
        var wetEnvelope = Envelope(wet);
        if (dryEnvelope.Length < 200 || wetEnvelope.Length < 200) return null;
        var best = -1d;
        var coarse = 0;
        for (var lag = -100; lag <= 100; lag++)
        {
            token.ThrowIfCancellationRequested();
            var score = Correlation(dryEnvelope, wetEnvelope, lag, centered: true);
            if (score > best) { best = score; coarse = lag; }
        }
        if (best < 0.8 || Math.Abs(coarse) == 100) return null;
        var drySamples = Array.ConvertAll(dry, sample => (double)sample);
        var wetSamples = Array.ConvertAll(wet, sample => (double)sample);
        best = -1;
        var bestLag = 0;
        for (var lag = coarse * block - block; lag <= coarse * block + block; lag++)
        {
            token.ThrowIfCancellationRequested();
            var score = Correlation(drySamples, wetSamples, lag, centered: false);
            if (score > best) { best = score; bestLag = lag; }
        }
        if (best < 0.7) return null;
        var dryStart = Math.Max(0, -bestLag);
        var wetStart = Math.Max(0, bestLag);
        var count = Math.Min(dry.Length - dryStart, wet.Length - wetStart);
        var dryEnergy = drySamples.Skip(dryStart).Take(count).Sum(sample => sample * sample);
        var wetEnergy = wetSamples.Skip(wetStart).Take(count).Sum(sample => sample * sample);
        if (dryEnergy < 1 || wetEnergy < 1) return null;
        return (bestLag / 8000d, Math.Clamp(10 * Math.Log10(dryEnergy / wetEnergy), -6, 6));
    }

    private static double Correlation(double[] dry, double[] wet, int lag, bool centered)
    {
        var dryStart = Math.Max(0, -lag);
        var wetStart = Math.Max(0, lag);
        var count = Math.Min(dry.Length - dryStart, wet.Length - wetStart);
        if (count < dry.Length / 2) return -1;
        double x = 0, y = 0, xx = 0, yy = 0, xy = 0;
        for (var index = 0; index < count; index++)
        {
            var a = dry[dryStart + index];
            var b = wet[wetStart + index];
            x += a; y += b; xx += a * a; yy += b * b; xy += a * b;
        }
        if (centered) { xx -= x * x / count; yy -= y * y / count; xy -= x * y / count; }
        return xx <= 0 || yy <= 0 ? -1 : xy / Math.Sqrt(xx * yy);
    }

    private static (short[] Samples, double Duration) ReadPcm16(string path, bool readSamples = true)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("對齊需要 PCM WAV。");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("無效的 WAV。");
        var rate = 0;
        var channels = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                var format = reader.ReadUInt16();
                channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                if (format != 1 || bits != 16) throw new InvalidDataException("對齊需要 16-bit PCM WAV。");
            }
            else if (id == "data" && rate > 0 && channels > 0)
            {
                var count = (int)(size / 2);
                if (!readSamples) return ([], count / (double)(rate * channels));
                if (channels != 1 || count > 120_000) throw new InvalidDataException("對齊探測音檔格式不正確。");
                var samples = new short[count];
                for (var index = 0; index < count; index++) samples[index] = reader.ReadInt16();
                return (samples, count / (double)rate);
            }
            reader.BaseStream.Position = next;
        }
        throw new InvalidDataException("找不到 WAV 音訊資料。");
    }
}
