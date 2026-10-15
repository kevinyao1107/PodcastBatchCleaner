using PodcastBatchCleaner.Core.Models;

namespace PodcastBatchCleaner.Core.Services;

public static class AudioCleanupPreview
{
    public static AudioProcessingOptions CreateOptions(AudioCleanupEffect effect, AudioCleanupTuning? tuning,
        double denoiseReductionDb, double reverbAmount, string outputFolder, VoiceToneTuning? voiceTuning = null)
    {
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        tuning?.Validate();
        return new AudioProcessingOptions(.1, -35, effect == AudioCleanupEffect.Denoise,
            effect == AudioCleanupEffect.RoomTone, effect == AudioCleanupEffect.Reverb, reverbAmount,
            effect == AudioCleanupEffect.VoiceTone, false, false, 0, FfmpegAudioProcessor.KeepOriginalChannelMode, 0, outputFolder,
            DenoiseReductionDb: denoiseReductionDb, RemoveSilence: false, CleanupTuning: tuning, VoiceTuning: voiceTuning);
    }
    public static AudioProcessingOptions Bypass(AudioProcessingOptions options)
        => options with { EnableDenoise = false, ReduceRoomTone = false, ReduceReverb = false, EnhanceVoiceEq = false };
}
