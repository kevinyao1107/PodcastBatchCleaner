using System.Diagnostics;
using System.IO;
using PodcastBatchCleaner.Core.Services;

internal static class ToneChecks
{
    public static async Task RunAsync(string ffmpeg, string root)
    {
        static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS: " + name); }
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "portable-preset-v2.json"));
        var preset = PortablePresetFile.Parse(fixture)[0];
        Check(preset.Core.Tone == new PortableTone(true, 3, -2, 1.5, true, 70), "V2 EQ/de-esser fixture transfers every tone parameter");
        Check(PortablePresetFile.Parse(PortablePresetFile.Serialize([preset]))[0].Core == preset.Core, "V2 tone settings round trip exactly");
        try { PortablePresetFile.Parse(fixture.Replace("\"highEqDb\": 1.5", "\"highEqDb\": 99")); throw new Exception("Unsafe EQ accepted"); }
        catch (InvalidDataException) { Check(true, "Reject unsafe shared EQ gain"); }
        var processor = new FfmpegAudioProcessor();
        var options = new AudioProcessingOptions(.5, -45, false, false, false, .6, false, false, false, 0, FfmpegAudioProcessor.KeepOriginalChannelMode, 0, root, RemoveSilence: false);
        foreach (var (frequency, name, settings, expectedDelta) in new[] {
            (40, "low_eq", options with { EnableThreeBandEq = true, LowEqDb = 6 }, 5d),
            (1100, "mid_eq", options with { EnableThreeBandEq = true, MidEqDb = 6 }, 5d),
            (16000, "high_eq", options with { EnableThreeBandEq = true, HighEqDb = 6 }, 5d),
            (7200, "de_esser", options with { EnableDeEsser = true, DeEsserAmount = 100 }, -8d) })
        {
            var input = Path.Combine(root, name + "_source.wav");
            var output = Path.Combine(root, name + "_processed.wav");
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=2:sample_rate=48000", "-c:a", "pcm_s16le", input }) start.ArgumentList.Add(arg);
            using (var p = Process.Start(start)!) { var log = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); await log; Check(p.ExitCode == 0, "Generate tone fixture " + name); }
            await processor.ProcessAsync(ffmpeg, input, output, settings, audioCodec: "pcm_s16le");
            var before = await processor.AnalyzeQualityAsync(ffmpeg, input);
            var after = await processor.AnalyzeQualityAsync(ffmpeg, output);
            var delta = after.MeanVolumeDb!.Value - before.MeanVolumeDb!.Value;
            Check(expectedDelta > 0 ? delta > expectedDelta : delta < expectedDelta, $"Actual {name} changes its target frequency ({delta:0.0} dB)");
        }
        var disabledOutput = Path.Combine(root, "disabled_tone.wav");
        var source = Path.Combine(root, "de_esser_source.wav");
        await processor.ProcessAsync(ffmpeg, source, disabledOutput, options with { LowEqDb = 12, DeEsserAmount = 100 }, audioCodec: "pcm_s16le");
        var original = await processor.AnalyzeQualityAsync(ffmpeg, source); var disabled = await processor.AnalyzeQualityAsync(ffmpeg, disabledOutput);
        Check(Math.Abs(original.MeanVolumeDb!.Value - disabled.MeanVolumeDb!.Value) < .1, "Disabled EQ/de-esser preserve the source level despite saved nonzero values");
    }
}
