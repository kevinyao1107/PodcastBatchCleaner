using System.IO;
using System.Reflection;
using System.Text.Json;
using PodcastBatchCleaner.Core.Services;

internal static class AlignmentChecks
{
    public static async Task RunAsync(string ffmpeg, string root)
    {
        static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS: " + name); }
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "portable-preset-v1.json"));
        var presets = PortablePresetFile.Parse(json);
        Check(presets[0].Core.LoudnessTarget == -18 && presets[0].Core.ChannelMode == "left", "SoundLab shared fixture imports with exact core settings");
        Check((PortablePresetFile.Parse(PortablePresetFile.Serialize(presets))[0].Core with { Tone = null }) == presets[0].Core, "Portable presets round trip without changing core settings");
        foreach (var invalid in new[] { json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"), json.Replace("\"minimumSilenceSeconds\": 2", "\"minimumSilenceSeconds\": 20"), json.Replace("\"loudnessEnabled\": true,", "") })
        {
            try { PortablePresetFile.Parse(invalid); throw new Exception("Invalid portable preset accepted"); }
            catch (InvalidDataException) { Check(true, "Reject incompatible or malformed portable preset"); }
        }
        var processor = new FfmpegAudioProcessor();
        var source = Path.Combine(root, "alignment_source.wav");
        var start = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2:sample_rate=48000", "-c:a", "pcm_s16le", source }) start.ArgumentList.Add(arg);
        using (var process = System.Diagnostics.Process.Start(start)!) { var logs = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); await logs; Check(process.ExitCode == 0, "Generate alignment audio fixture"); }
        var options = new AudioProcessingOptions(.5, -45, false, false, false, .6, false, true, true, 0, FfmpegAudioProcessor.KeepOriginalChannelMode, 0, root, RemoveSilence: false);
        foreach (var target in new[] { -14, -16, -18, -23 })
        {
            var output = Path.Combine(root, $"alignment_{target}.wav");
            await processor.ProcessAsync(ffmpeg, source, output, options with { LoudnessTarget = target }, audioCodec: "pcm_s16le");
            var metrics = await processor.AnalyzeQualityAsync(ffmpeg, output);
            Check(Math.Abs(metrics.IntegratedLufs!.Value - target) < .5 && metrics.TruePeakDbfs <= -1.5, $"Rendered WAV reaches {target} LUFS and passes true peak verification");
        }
        var selectedAi = Path.Combine(root, "alignment_right_ai.wav");
        await processor.PrepareDeepFilterNetInputAsync(ffmpeg, source, selectedAi, inputChannelMode: FfmpegAudioProcessor.ChannelModes.First(m => m.Value == "right_mono"));
        Check(await processor.ProbeAudioChannelsAsync(ffmpeg, selectedAi) == 1, "Shared AI channel selection handles a mono source");
        var quietRight = Path.Combine(root, "alignment_quiet_right.wav");
        await processor.ProcessAsync(ffmpeg, source, quietRight, options with { NormalizeLoudness = false, EnableLimiter = false, VolumeGainDb = -12 }, audioCodec: "pcm_s16le");
        var stereo = Path.Combine(root, "alignment_stereo.wav");
        await processor.RestoreStereoAsync(ffmpeg, source, quietRight, stereo);
        await processor.PrepareDeepFilterNetInputAsync(ffmpeg, stereo, selectedAi, inputChannelMode: FfmpegAudioProcessor.ChannelModes.First(m => m.Value == "right_mono"));
        var sourceMetrics = await processor.AnalyzeQualityAsync(ffmpeg, source);
        var selectedMetrics = await processor.AnalyzeQualityAsync(ffmpeg, selectedAi);
        Check(Math.Abs((sourceMetrics.MeanVolumeDb!.Value - selectedMetrics.MeanVolumeDb!.Value) - 12) < .3, "Shared right Mono AI input selects the original right channel before downmix");
        var filters = typeof(FfmpegAudioProcessor).GetMethod("BuildFilters", BindingFlags.NonPublic | BindingFlags.Static)!;
        var chain = (string)filters.Invoke(null, [options with { RemoveSilence = true, SilenceSeconds = 5, SilencePaddingSeconds = .12, VolumeGainDb = 2 }, false])!;
        Check(chain.Contains("stop_duration=5.0") && chain.Contains("stop_silence=0.12"), "Shared silence duration and padding reach FFmpeg");
        Check(chain.IndexOf("volume=", StringComparison.Ordinal) < chain.IndexOf("loudnorm=", StringComparison.Ordinal), "Gain precedes normalization consistently with SoundLab");
        foreach (var mode in new[] { "left_mono", "right_mono", "swap" })
        {
            var output = Path.Combine(root, $"alignment_{mode}.wav");
            await processor.ProcessAsync(ffmpeg, source, output, options with { NormalizeLoudness = false, ChannelMode = FfmpegAudioProcessor.ChannelModes.First(m => m.Value == mode) }, audioCodec: "pcm_s16le");
            Check(await processor.ProbeAudioChannelsAsync(ffmpeg, output) == 1, $"Portable channel mode {mode} preserves mono source channel count");
        }
    }
}
