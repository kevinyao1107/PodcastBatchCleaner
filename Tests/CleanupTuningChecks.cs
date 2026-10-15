using System.Diagnostics;
using System.IO;
using System.Reflection;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;

internal static class CleanupTuningChecks
{
    public static async Task RunAsync(string ffmpeg, string root)
    {
        static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var baseline = AudioCleanupPreview.CreateOptions(AudioCleanupEffect.Denoise, null, 12, .6, root);
        var filterBuilder = typeof(FfmpegAudioProcessor).GetMethod("BuildFilters", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Filters(AudioProcessingOptions options) => (string)filterBuilder.Invoke(null, [options, false])!;
        Check(Filters(baseline).Contains("afftdn=nf=-35:nr=12:tn=1"), "Basic cleanup retains the original denoise parameters");
        Check(Filters(baseline with { CleanupTuning = new(TrackNoise: false) }).Contains("tn=0"), "Fixed noise floor can disable automatic tracking");
        var legacyRoom = AudioCleanupPreview.CreateOptions(AudioCleanupEffect.RoomTone, null, 12, .6, root);
        Check(Filters(legacyRoom) == Filters(legacyRoom with { CleanupTuning = new() }), "Default fine room controls preserve the existing sound");
        foreach (var effect in Enum.GetValues<AudioCleanupEffect>())
        {
            var options = AudioCleanupPreview.CreateOptions(effect, new(), 12, .6, root);
            Check(new[] { options.EnableDenoise, options.ReduceRoomTone, options.ReduceReverb, options.EnhanceVoiceEq }.Count(flag => flag) == 1
                && !options.RemoveSilence && !options.EnableLimiter && !options.NormalizeLoudness
                && options.VolumeGainDb == 0 && options.ChannelMode.Value == "keep", $"Isolated {effect} never adds unrelated effects or changes timing");
            Check(Filters(AudioCleanupPreview.Bypass(options)) == "", "Bypass has no audio filters");
        }
        foreach (var bad in new[] { new AudioCleanupTuning(NoiseFloorDb: double.NaN), new(RoomFrequencyHz: 9999), new(RoomReductionDb: 0), new(TailReleaseMs: 9000) })
        {
            try { bad.Validate(); throw new Exception("Invalid cleanup tuning accepted"); }
            catch (InvalidDataException) { Check(true, "Unsafe cleanup tuning is rejected"); }
        }
        var processor = new FfmpegAudioProcessor();
        var voiceOptions = AudioCleanupPreview.CreateOptions(AudioCleanupEffect.VoiceTone, null, 12, .6, root);
        Check(Filters(voiceOptions) == "equalizer=f=160:t=q:w=0.9:g=2,equalizer=f=320:t=q:w=1.0:g=-1,equalizer=f=3200:t=q:w=1.0:g=1,acompressor=threshold=-18dB:ratio=2.2:attack=8:release=120:makeup=1.5dB", "Legacy voice tone preserves its exact default filter chain");
        Check(Filters(voiceOptions with { VoiceTuning = new(0, 0, false) }) == "", "Zero voice tone with compression disabled is a bypass");
        Check(Filters(voiceOptions with { VoiceTuning = new(0, 3, false) }) == "equalizer=f=3200:t=q:w=1.0:g=3", "Clarity is independent of thickness and compression");
        Check(Filters(voiceOptions with { VoiceTuning = new(0, 0, true) }).StartsWith("acompressor="), "Compression can run without tonal EQ");
        foreach (var bad in new[] { new VoiceToneTuning(double.NaN), new(7), new(2, -1) })
        {
            try { bad.Validate(); throw new Exception("Invalid voice tuning accepted"); }
            catch (InvalidDataException) { Check(true, "Unsafe voice tuning is rejected"); }
        }
        async Task<string> Fixture(string name, string source)
        {
            var input = Path.Combine(root, name + "_cleanup.wav");
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", source, "-c:a", "pcm_s16le", input }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var log = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); var error = await log;
            if (process.ExitCode != 0) throw new Exception(error);
            return input;
        }
        async Task<double> RenderMean(string input, string name, AudioProcessingOptions options)
        {
            var output = Path.Combine(root, name + ".wav");
            await processor.ProcessAsync(ffmpeg, input, output, options, audioCodec: "pcm_s16le");
            return (await processor.AnalyzeQualityAsync(ffmpeg, output)).MeanVolumeDb!.Value;
        }
        var voiceInput = await Fixture("voice", "aevalsrc=0.05*sin(2*PI*160*t):s=48000:d=3");
        var thin = await RenderMean(voiceInput, "voice_zero", voiceOptions with { VoiceTuning = new(0, 0, false) });
        var thick = await RenderMean(voiceInput, "voice_thick", voiceOptions with { VoiceTuning = new(6, 0, false) });
        Check(thick > thin + 4, $"Thickness changes actual low-band level ({thick - thin:0.0} dB)");
        var clarityInput = await Fixture("clarity", "aevalsrc=0.05*sin(2*PI*3200*t):s=48000:d=3");
        var dull = await RenderMean(clarityInput, "voice_dull", voiceOptions with { VoiceTuning = new(0, 0, false) });
        var clear = await RenderMean(clarityInput, "voice_clear", voiceOptions with { VoiceTuning = new(0, 4, false) });
        Check(Math.Abs(clear - dull - 4) < .5, "Clarity changes actual presence-band level by the selected dB");
        var noise = await Fixture("noise", "anoisesrc=c=white:a=0.03:r=48000:d=3:seed=42");
        var light = await RenderMean(noise, "noise_light", baseline with { CleanupTuning = new(DenoiseReductionDb: 3, TrackNoise: false) });
        var strong = await RenderMean(noise, "noise_strong", baseline with { CleanupTuning = new(DenoiseReductionDb: 24, TrackNoise: false) });
        Check(strong < light - .5, $"Fine denoise strength changes actual noise attenuation ({light:0.0} vs {strong:0.0} dBFS)");
        var room = await Fixture("room", "aevalsrc=0.6*sin(2*PI*300*t):s=48000:d=3");
        var roomOptions = AudioCleanupPreview.CreateOptions(AudioCleanupEffect.RoomTone, new(), 12, .6, root);
        light = await RenderMean(room, "room_light", roomOptions with { CleanupTuning = new(RoomReductionDb: 2) });
        strong = await RenderMean(room, "room_strong", roomOptions with { CleanupTuning = new(RoomReductionDb: 6) });
        Check(Math.Abs((light - strong) - 4) < 1, $"Room reduction dB matches the actual target-band change ({light - strong:0.0} dB)");
        var tail = await Fixture("tail", "aevalsrc=0.003*sin(2*PI*1000*t):s=48000:d=3");
        var tailOptions = AudioCleanupPreview.CreateOptions(AudioCleanupEffect.Reverb, new(), 12, .6, root);
        light = await RenderMean(tail, "tail_light", tailOptions with { CleanupTuning = new(TailThresholdDb: -60) });
        strong = await RenderMean(tail, "tail_strong", tailOptions with { CleanupTuning = new(TailThresholdDb: -25) });
        Check(strong < light - 1, $"Tail threshold changes attenuation of quiet audio ({light:0.0} vs {strong:0.0} dBFS)");
        Check(Filters(tailOptions with { CleanupTuning = new(TailReleaseMs: 700) }).Contains("release=700"), "Tail release reaches the gate filter");
    }
}
