using System.IO;

namespace PodcastBatchCleaner.Core.Models;

public sealed record AudioCleanupTuning(
    double DenoiseReductionDb = 12,
    double NoiseFloorDb = -35,
    double RoomHighpassHz = 65,
    double RoomFrequencyHz = 300,
    double RoomReductionDb = 4,
    double TailThresholdDb = -38.416375,
    double TailReleaseMs = 250,
    bool TrackNoise = true)
{
    public void Validate()
    {
        if (!InRange(DenoiseReductionDb, 1, 30) || !InRange(NoiseFloorDb, -70, -20)
            || !InRange(RoomHighpassHz, 40, 120) || !InRange(RoomFrequencyHz, 150, 600)
            || !InRange(RoomReductionDb, 2, 12) || !InRange(TailThresholdDb, -60, -25)
            || !InRange(TailReleaseMs, 100, 800))
            throw new InvalidDataException("聲音清理進階參數超出有效範圍。");
    }
    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

public enum AudioCleanupEffect { Denoise, RoomTone, Reverb, VoiceTone }
