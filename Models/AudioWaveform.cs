using System.IO;

namespace PodcastBatchCleaner.Core.Models;

public sealed record AudioWaveform(float[] Peaks, double DurationSeconds, double BinSeconds);

// Adjacent peak bins are merged as the stream grows; memory stays bounded even without duration metadata.
public sealed class AudioWaveformBuilder
{
    public const int MaximumBins = 1024;
    private readonly float[] _peaks = new float[MaximumBins];
    private long _frames, _binFrames = 1;
    public void AddFrame(float peak)
    {
        if (!float.IsFinite(peak) || peak < 0) throw new InvalidDataException("音檔含有無效的 PCM 數值。");
        if (_frames / _binFrames >= MaximumBins)
        {
            for (var i = 0; i < MaximumBins / 2; i++) _peaks[i] = Math.Max(_peaks[i * 2], _peaks[i * 2 + 1]);
            Array.Clear(_peaks, MaximumBins / 2, MaximumBins / 2);
            _binFrames *= 2;
        }
        var bin = (int)(_frames / _binFrames);
        _peaks[bin] = Math.Max(_peaks[bin], peak);
        _frames++;
    }
    public AudioWaveform Build(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (_frames == 0) throw new InvalidDataException("音檔沒有可讀取的音訊。");
        return new AudioWaveform(_peaks[..(int)((_frames + _binFrames - 1) / _binFrames)],
            (double)_frames / sampleRate, (double)_binFrames / sampleRate);
    }
}
