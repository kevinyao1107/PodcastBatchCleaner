using System.Diagnostics;
using System.IO;
using System.Reflection;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;
using PodcastBatchCleanerWpf;

if (args.Any(a => a.Contains("waveform_fixture_cancel", StringComparison.Ordinal)))
{
    if (args.Contains("-show_entries")) { Console.WriteLine("2"); return 0; }
    if (args.Contains("f32le"))
    {
        File.WriteAllText(args[Array.IndexOf(args, "-i") + 1], Environment.ProcessId.ToString());
        await Task.Delay(TimeSpan.FromSeconds(30)); return 0;
    }
}

if (args.Contains("-encoders"))
{
    Console.WriteLine(" A..... aac AAC encoder fixture");
    return 0;
}

// The same executable acts as a slow external tool to verify process cancellation.
if (args.Contains("-show_entries") || args.Contains("-progress") || (args.Contains("-f") && args.Contains("null")))
{
    var input = args[Array.IndexOf(args, "-i") + 1];
    if (args.Contains("-show_entries")) input = args[^1];
    if (input.Contains("verify_fixture_", StringComparison.Ordinal))
    {
        var directory = Path.GetDirectoryName(input)!;
        if (args.Contains("-progress"))
        {
            File.WriteAllText(args[^1], "unverified output");
            File.AppendAllText(Path.Combine(directory, "renders.txt"), input + Environment.NewLine);
            return 0;
        }
        if (input.Contains("cancellation", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(directory, "validation.marker"), Environment.ProcessId.ToString());
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 0;
        }
        Console.Error.WriteLine(input.Contains("invalid", StringComparison.Ordinal) ? "missing peak metric" : "Peak: 0.0 dBFS");
        return 0;
    }
    if (args.Contains("-progress")) File.WriteAllText(args[^1], "partial output");
    File.WriteAllText(input, Environment.ProcessId.ToString());
    if (input.Contains("fail", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("simulated encoder failure");
        return 7;
    }
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}

try
{
    if (args.FirstOrDefault() == "--long-audio") return await LongAudioChecks.RunAsync(args.Skip(1).ToArray());
    if (args.FirstOrDefault() == "--mp3-audio") return await EncodedAudioChecks.RunAsync(args.Skip(1).ToArray());
    return await RunChecksAsync(args);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static async Task<int> RunChecksAsync(string[] args)
{
    var ffmpeg = args.FirstOrDefault() ?? throw new ArgumentException("Pass the FFmpeg executable path.");
    var root = Path.Combine(Path.GetTempPath(), $"podcast_regression_{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var fakeProbe = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
    var createdFakeProbe = false;
    try
    {
        await AlignmentChecks.RunAsync(ffmpeg, root);
        await ToneChecks.RunAsync(ffmpeg, root);
        await WaveformChecks.RunAsync(ffmpeg, root);
        await WorkSessionChecks.RunAsync(ffmpeg, root, args.Length > 2 ? args[2] : null);
        RunUiChecks(root, args.Length > 2 ? args[2] : null);
        var parse = typeof(MainWindow).GetMethod("TryParseOptionalTime", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var input in new[] { "NaN", "Infinity", "1e300", "-1", "01:99", "-1:10" })
        {
            object?[] parameters = [input, null];
            Check(!(bool)parse.Invoke(null, parameters)!, $"Reject invalid time: {input}");
        }
        foreach (var (input, expected) in new[] { ("12.5", 12.5), ("01:23", 83d), ("1:02:03", 3723d) })
        {
            object?[] parameters = [input, null];
            Check((bool)parse.Invoke(null, parameters)! && (TimeSpan)parameters[1]! == TimeSpan.FromSeconds(expected),
                $"Parse valid time: {input}");
        }
        var trim = typeof(MainWindow).GetMethod("TryGetTrimRange", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] trimParameters = [new AudioFileItem { FilePath = "test.wav", TrimEndText = "0" }, null, null, null];
        Check(!(bool)trim.Invoke(null, trimParameters)!, "Reject zero end with blank start");

        var aiFolder = Path.Combine(root, "paired_ai");
        Directory.CreateDirectory(aiFolder);
        await File.WriteAllTextAsync(Path.Combine(aiFolder, "episode10_ai.wav"), "other episode");
        Check(FfmpegAudioProcessor.FindAiProcessedAudio("episode1.wav", aiFolder) is null,
            "AI matching never selects a different episode with a shared prefix");
        var exactAi = Path.Combine(aiFolder, "Episode1_AI.WAV");
        await File.WriteAllTextAsync(exactAi, "correct episode");
        Check(FfmpegAudioProcessor.FindAiProcessedAudio("episode1.wav", aiFolder) == exactAi,
            "AI matching accepts exact aliases ignoring filename case");
        var secondAi = Path.Combine(aiFolder, "episode1_cleaned.mp3");
        await File.WriteAllTextAsync(secondAi, "ambiguous episode");
        try
        {
            FfmpegAudioProcessor.FindAiProcessedAudio("episode1.wav", aiFolder);
            throw new Exception("Expected ambiguous AI match rejection");
        }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("Episode1_AI.WAV") && error.Message.Contains("episode1_cleaned.mp3"),
                "Ambiguous AI matches stop with candidate filenames");
        }

        var toolRoot = Path.Combine(root, "bundled_tools");
        var buildDirectory = Path.Combine(toolRoot, "bin", "Release", "net8.0-windows");
        Directory.CreateDirectory(buildDirectory);
        var bundledDfn = Path.Combine(toolRoot, "tools", "DeepFilterNet", "deep-filter.exe");
        var bundledVf = Path.Combine(toolRoot, "tools", "VoiceFixer", "voicefixer.cmd");
        var bundledFfmpeg = Path.Combine(toolRoot, "tools", "FFmpeg", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bundledDfn)!);
        Directory.CreateDirectory(Path.GetDirectoryName(bundledVf)!);
        await File.WriteAllTextAsync(bundledDfn, "discovery fixture");
        await File.WriteAllTextAsync(bundledVf, "discovery fixture");
        Directory.CreateDirectory(Path.GetDirectoryName(bundledFfmpeg)!);
        await File.WriteAllTextAsync(bundledFfmpeg, "discovery fixture");
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            Check(DeepFilterNetAudioProcessor.TryFindDeepFilterNet(buildDirectory) == bundledDfn,
                "DeepFilterNet is found from the standard Release directory");
            Check(VoiceFixerAudioProcessor.TryFindVoiceFixer(buildDirectory) == bundledVf,
                "VoiceFixer scripts are found in the repository tools directory");
            Check(DeepFilterNetAudioProcessor.TryFindDeepFilterNet(toolRoot) == bundledDfn,
                "DeepFilterNet is found beside a portable installation");
            Check(FfmpegAudioProcessor.TryFindFfmpeg(buildDirectory) == bundledFfmpeg,
                "Bundled FFmpeg is found from the standard Release directory");
            var otherTools = Path.Combine(root, "other_tools");
            Directory.CreateDirectory(otherTools);
            await File.WriteAllTextAsync(Path.Combine(otherTools, "ffmpeg.exe"), "PATH fixture");
            Environment.SetEnvironmentVariable("PATH", otherTools);
            Check(FfmpegAudioProcessor.TryFindFfmpeg(buildDirectory) == bundledFfmpeg,
                "Bundled FFmpeg takes precedence over a PATH tool");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }

        var processor = new FfmpegAudioProcessor();
        Check(await processor.HasAudioEncoderAsync(ffmpeg, "aac")
            && !await processor.HasAudioEncoderAsync(ffmpeg, "missing_encoder_fixture"),
            "Encoder preflight matches exact available audio encoders");
        var options = new AudioProcessingOptions(0.1, -80, false, false, false, 0.6,
            false, false, true, 6, FfmpegAudioProcessor.KeepOriginalChannelMode, 0, root);
        var currentOptions = options with { EnableDenoise = true, ReduceRoomTone = true, ReduceReverb = true,
            VolumeGainDb = 3.7, StereoBalance = 0.4, DenoiseReductionDb = 24,
            ChannelMode = new AudioChannelMode("Balance", "balance") };
        var presetFactory = typeof(MainWindow).GetMethod("CreateComparisonPresetsForOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
        var comparisonPresets = ((System.Collections.IEnumerable)presetFactory.Invoke(null, [currentOptions])!).Cast<object>().ToArray();
        var originalPreset = comparisonPresets[0];
        var originalOptions = (AudioProcessingOptions)originalPreset.GetType().GetProperty("Options")!.GetValue(originalPreset)!;
        Check((bool)originalPreset.GetType().GetProperty("UseOriginalInput")!.GetValue(originalPreset)!
            && !originalOptions.RemoveSilence && !originalOptions.EnableDenoise && !originalOptions.ReduceRoomTone
            && !originalOptions.ReduceReverb && !originalOptions.EnhanceVoiceEq && !originalOptions.NormalizeLoudness
            && !originalOptions.EnableLimiter && originalOptions.VolumeGainDb == 0 && originalOptions.ChannelMode.Value == "keep",
            "A/B original preset routes to original audio and disables all enhancements and silence removal");
        var currentPreset = comparisonPresets[1];
        Check(!(bool)currentPreset.GetType().GetProperty("UseOriginalInput")!.GetValue(currentPreset)!
            && (AudioProcessingOptions)currentPreset.GetType().GetProperty("Options")!.GetValue(currentPreset)! == currentOptions,
            "A/B current preset preserves every selected processing option");
        var sourceTrim = typeof(MainWindow).GetMethod("TryGetTrimRangeForDuration", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cachedItem = new AudioFileItem { FilePath = "source.wav", Duration = TimeSpan.FromSeconds(10), TrimEndText = "15" };
        object?[] longerParameters = [cachedItem, TimeSpan.FromSeconds(20), null, null, null];
        Check((bool)sourceTrim.Invoke(null, longerParameters)!, "Trim validation uses a longer AI source instead of the original duration");
        cachedItem.TrimEndText = "8";
        object?[] shorterParameters = [cachedItem, TimeSpan.FromSeconds(5), null, null, null];
        Check(!(bool)sourceTrim.Invoke(null, shorterParameters)! && cachedItem.Duration == TimeSpan.FromSeconds(10),
            "Trim validation rejects ranges outside shorter AI audio without replacing original duration");
        var inputPath = Path.Combine(root, "stereo.wav");
        await ExecuteAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
            "aevalsrc=0.9*sin(2*PI*300*t)|0.09*sin(2*PI*1000*t):s=48000:d=2",
            "-c:a", "pcm_s16le", inputPath]);
        var fallbackProbe = typeof(FfmpegAudioProcessor).GetMethod("ProbeDurationWithFfmpegAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fallbackDuration = await (Task<TimeSpan?>)fallbackProbe.Invoke(processor, [ffmpeg, inputPath, CancellationToken.None])!;
        Check(fallbackDuration is not null && Math.Abs(fallbackDuration.Value.TotalSeconds - 2) < 0.02,
            "FFmpeg duration fallback reads the source duration without ffprobe");
        var originalComparison = Path.Combine(root, "original_comparison.wav");
        await processor.ProcessAsync(ffmpeg, inputPath, originalComparison, originalOptions, audioCodec: "pcm_s16le");
        var originalPcm = ReadStereoPcm(inputPath);
        var comparisonPcm = ReadStereoPcm(originalComparison);
        Check(originalPcm.Rate == comparisonPcm.Rate && originalPcm.Samples.SequenceEqual(comparisonPcm.Samples),
            "A/B original rendering preserves the original stereo samples");
        var fadedOutro = Path.Combine(root, "faded_outro.wav");
        await processor.PrepareClipForMergeAsync(ffmpeg, inputPath, fadedOutro, 0, 1);
        var fadedPcm = ReadStereoPcm(fadedOutro);
        Check(fadedPcm.Samples.TakeLast((int)(0.05 * fadedPcm.Rate) * 2).Max(sample => Math.Abs((int)sample)) < 1800,
            "Outro fade automatically obtains duration and actually fades the ending");
        var limitedPath = Path.Combine(root, "limited.wav");
        await processor.ProcessAsync(ffmpeg, inputPath, limitedPath, options, audioCodec: "pcm_s16le");
        var quality = await processor.AnalyzeQualityAsync(ffmpeg, limitedPath);
        Check(quality.MaxVolumeDb is <= -1.4, $"Final limiter protects +6dB gain (peak={quality.MaxVolumeDb})");

        var leftMode = new AudioChannelMode("Left", "left_only");
        var rightMode = new AudioChannelMode("Right", "right_only");
        var left = Path.Combine(root, "left.wav");
        var right = Path.Combine(root, "right.wav");
        await processor.PrepareDeepFilterNetInputAsync(ffmpeg, inputPath, left, inputChannelMode: leftMode);
        await processor.PrepareDeepFilterNetInputAsync(ffmpeg, inputPath, right, inputChannelMode: rightMode);
        var leftQuality = await processor.AnalyzeQualityAsync(ffmpeg, left);
        var rightQuality = await processor.AnalyzeQualityAsync(ffmpeg, right);
        Check(Math.Abs((leftQuality.MaxVolumeDb!.Value - rightQuality.MaxVolumeDb!.Value) - 20) < 0.3,
            "DeepFilterNet preparation selects original channels before mixing");
        var stereo = Path.Combine(root, "restored.wav");
        await processor.RestoreStereoAsync(ffmpeg, left, right, stereo);
        Check(await processor.ProbeAudioChannelsAsync(ffmpeg, stereo) == 2, "Stereo channels restored");
        var shortRight = Path.Combine(root, "short_right.wav");
        await ExecuteAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-i", right,
            "-t", "1", "-c:a", "pcm_s16le", shortRight]);
        var unevenStereo = Path.Combine(root, "uneven_stereo.wav");
        await processor.RestoreStereoAsync(ffmpeg, left, shortRight, unevenStereo);
        var uneven = ReadStereoPcm(unevenStereo);
        Check(Math.Abs(uneven.Samples.Length / (2d * uneven.Rate) - 2) < 0.01,
            "Stereo restoration preserves a longer left channel");
        var tail = uneven.Samples.Skip((int)(1.5 * uneven.Rate) * 2).ToArray();
        Check(tail.Where((_, index) => index % 2 == 0).Any(sample => Math.Abs((int)sample) > 100)
            && tail.Where((_, index) => index % 2 == 1).All(sample => sample == 0),
            "Shorter right channel is padded with silence without losing the left tail");
        await processor.RestoreStereoAsync(ffmpeg, shortRight, left, unevenStereo);
        uneven = ReadStereoPcm(unevenStereo);
        tail = uneven.Samples.Skip((int)(1.5 * uneven.Rate) * 2).ToArray();
        Check(Math.Abs(uneven.Samples.Length / (2d * uneven.Rate) - 2) < 0.01
            && tail.Where((_, index) => index % 2 == 0).All(sample => sample == 0)
            && tail.Where((_, index) => index % 2 == 1).Any(sample => Math.Abs((int)sample) > 100),
            "Stereo restoration also preserves a longer right channel");
        var unevenQuality = await processor.AnalyzeQualityAsync(ffmpeg, unevenStereo);
        Check(Math.Abs(unevenQuality.MaxVolumeDb!.Value - leftQuality.MaxVolumeDb!.Value) < 0.2,
            "Stereo restoration does not change the channel peak level");
        var vfLeft = Path.Combine(root, "vf_left.wav");
        var vfRight = Path.Combine(root, "vf_right.wav");
        await processor.PrepareVoiceFixerInputAsync(ffmpeg, inputPath, vfLeft, inputChannelMode: leftMode);
        await processor.PrepareVoiceFixerInputAsync(ffmpeg, inputPath, vfRight, inputChannelMode: rightMode);
        var vfLeftQuality = await processor.AnalyzeQualityAsync(ffmpeg, vfLeft);
        var vfRightQuality = await processor.AnalyzeQualityAsync(ffmpeg, vfRight);
        Check(Math.Abs((vfLeftQuality.MaxVolumeDb!.Value - vfRightQuality.MaxVolumeDb!.Value) - 20) < 0.3,
            "VoiceFixer preparation selects original channels before mixing");

        var scriptDirectory = Path.Combine(root, "VoiceFixer 中文 space & %PATH% !");
        Directory.CreateDirectory(scriptDirectory);
        var scriptInput = Path.Combine(scriptDirectory, "input 音檔 & %PATH% !.wav");
        File.Copy(vfLeft, scriptInput);
        foreach (var extension in new[] { ".cmd", ".bat" })
        {
            var script = Path.Combine(scriptDirectory, "voice fixer" + extension);
            await File.WriteAllTextAsync(script, "@echo off\r\ncopy /b \"%~2\" \"%~4\" >nul\r\nexit /b %errorlevel%\r\n");
            var scriptOutput = Path.Combine(scriptDirectory, "output 音檔 & %PATH% !" + extension + ".wav");
            var processed = await new VoiceFixerAudioProcessor().ProcessAsync(script, scriptInput, scriptOutput, 0);
            Check(processed == scriptOutput && File.ReadAllBytes(scriptInput).SequenceEqual(File.ReadAllBytes(scriptOutput)),
                $"VoiceFixer {extension} supports spaces, Unicode and shell characters in all paths");
        }

        var spacePath = Path.Combine(root, "dynamic_space.wav");
        await processor.ProcessAsync(ffmpeg, inputPath, spacePath,
            options with { ReduceRoomTone = true, ReduceReverb = true, EnableDenoise = true },
            audioCodec: "pcm_s16le");
        Check(File.Exists(spacePath), "Dynamic EQ, gentle expansion and controlled denoise render successfully");

        var blendDry = Path.Combine(root, "blend_dry.wav");
        var blendWet = Path.Combine(root, "blend_wet.wav");
        await ExecuteAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
            "aevalsrc=(0.15+0.08*sin(2*PI*0.73*t)+0.04*sin(2*PI*1.37*t))*sin(2*PI*(230*t+17*t*t)):s=44100:d=8",
            "-c:a", "pcm_s16le", blendDry]);
        await ExecuteAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-i", blendDry,
            "-af", "adelay=125,volume=2dB", "-c:a", "pcm_s16le", blendWet]);
        var blender = new AudioBlendProcessor();
        var blended = await blender.BlendAsync(ffmpeg, blendDry, blendWet,
            Path.Combine(root, "blended.wav"), 0.6, CancellationToken.None);
        Check(blended.Warning is null && blended.Path != blendWet, "Delayed and louder repair is aligned before mixing");
        var dryQuality = await processor.AnalyzeQualityAsync(ffmpeg, blendDry);
        var blendQuality = await processor.AnalyzeQualityAsync(ffmpeg, blended.Path);
        Check(Math.Abs(dryQuality.MeanVolumeDb!.Value - blendQuality.MeanVolumeDb!.Value) < 0.5,
            "Aligned blend preserves reference volume");
        Check((await blender.BlendAsync(ffmpeg, blendDry, blendWet, "unused.wav", 0, CancellationToken.None)).Path == blendDry,
            "Zero repair mix returns original");
        Check((await blender.BlendAsync(ffmpeg, blendDry, blendWet, "unused.wav", 1, CancellationToken.None)).Path == blendWet,
            "Full repair mix returns restored audio");
        var unrelated = await blender.BlendAsync(ffmpeg, blendDry, left,
            Path.Combine(root, "unrelated.wav"), 0.6, CancellationToken.None);
        Check(unrelated.Warning is not null && unrelated.Path == left, "Unreliable alignment declines mixing with warning");

        var matchLeft = Path.Combine(root, "matched_left.wav");
        var matchRight = Path.Combine(root, "matched_right.wav");
        await processor.MatchComparisonLoudnessAsync(ffmpeg, left, matchLeft, "pcm_s16le", CancellationToken.None);
        await processor.MatchComparisonLoudnessAsync(ffmpeg, right, matchRight, "pcm_s16le", CancellationToken.None);
        var matchedLeftQuality = await processor.AnalyzeQualityAsync(ffmpeg, matchLeft);
        var matchedRightQuality = await processor.AnalyzeQualityAsync(ffmpeg, matchRight);
        Check(Math.Abs(matchedLeftQuality.IntegratedLufs!.Value + 16) < 0.5
            && Math.Abs(matchedRightQuality.IntegratedLufs!.Value + 16) < 0.5,
            "A/B outputs match -16 LUFS despite different original levels");

        var loudMusic = Path.Combine(root, "loud_music.wav");
        var quietVoice = Path.Combine(root, "quiet_voice.wav");
        await processor.PrepareClipForMergeAsync(ffmpeg, inputPath, loudMusic, 0, 0);
        await ExecuteAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
            "aevalsrc=0.06*sin(2*PI*230*t)|0.06*sin(2*PI*230*t):s=44100:d=6", "-c:a", "pcm_s16le", quietVoice]);
        var mergedMaster = Path.Combine(root, "merged_master.wav");
        await processor.MergeAsync(ffmpeg, [loudMusic, quietVoice, loudMusic], mergedMaster, 0,
            outputFormat: FfmpegAudioProcessor.OutputFormats[2], normalizeLoudness: true, enableLimiter: true);
        var masterQuality = await processor.AnalyzeQualityAsync(ffmpeg, mergedMaster);
        Check(Math.Abs(masterQuality.IntegratedLufs!.Value + 16) < 0.7 && masterQuality.MaxVolumeDb is <= -1.4,
            $"Whole-episode mastering includes loud intro/outro (LUFS={masterQuality.IntegratedLufs}, peak={masterQuality.MaxVolumeDb})");
        var limitedMerge = Path.Combine(root, "limited_merge.wav");
        await processor.MergeAsync(ffmpeg, [loudMusic, quietVoice], limitedMerge, 0,
            outputFormat: FfmpegAudioProcessor.OutputFormats[2], enableLimiter: true);
        Check((await processor.AnalyzeQualityAsync(ffmpeg, limitedMerge)).MaxVolumeDb is <= -1.4,
            "Whole-episode limiter protects intro/outro even without normalization");
        var unchangedMerge = Path.Combine(root, "unchanged_merge.wav");
        await processor.MergeAsync(ffmpeg, [loudMusic], unchangedMerge, 0,
            outputFormat: FfmpegAudioProcessor.OutputFormats[2], sampleRate: 44100);
        Check(ReadStereoPcm(loudMusic).Samples.SequenceEqual(ReadStereoPcm(unchangedMerge).Samples),
            "Disabled mastering preserves merged PCM samples");
        var silentClip = Path.Combine(root, "silent.wav");
        await ExecuteAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
            "anullsrc=r=44100:cl=stereo", "-t", "1", "-c:a", "pcm_s16le", silentClip]);
        var silentMerge = Path.Combine(root, "silent_merge.wav");
        await processor.MergeAsync(ffmpeg, [silentClip], silentMerge, 0,
            outputFormat: FfmpegAudioProcessor.OutputFormats[2], normalizeLoudness: true, enableLimiter: true);
        Check(ReadStereoPcm(silentMerge).Samples.All(sample => sample == 0),
            "Whole-episode normalization safely preserves entirely silent audio");

        var cover = Path.Combine(root, "cover.jpg");
        await ExecuteAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
            "color=c=green:s=32x32", "-frames:v", "1", cover]);
        var coveredMaster = Path.Combine(root, "covered_master.m4a");
        await processor.MergeAsync(ffmpeg, [loudMusic, quietVoice], coveredMaster, 0,
            metadata: new AudioMetadataOptions("MasterEpisode", "TestArtist", "TestAlbum", cover),
            outputFormat: FfmpegAudioProcessor.OutputFormats[0], normalizeLoudness: true, enableLimiter: true);
        var analyze = typeof(FfmpegAudioProcessor).GetMethod("RunAnalysisAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var coverOutput = await (Task<string>)analyze.Invoke(null, [ffmpeg,
            new[] { "-hide_banner", "-nostdin", "-i", coveredMaster, "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-" }, CancellationToken.None])!;
        Check(coverOutput.Contains("MasterEpisode") && coverOutput.Contains("TestArtist") && coverOutput.Contains("mjpeg"),
            "Whole-episode mastering retains M4A cover art and metadata");

        var transients = Path.Combine(root, "transients.wav");
        await ExecuteAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
            "aevalsrc=(0.65*sin(2*PI*330*t)+0.3*sin(2*PI*13000*t))*if(lt(mod(t\\,0.25)\\,0.015)\\,1\\,0.12):s=48000:d=5",
            "-ac", "2", "-c:a", "pcm_s16le", transients]);
        var encoders = await (Task<string>)analyze.Invoke(null, [ffmpeg,
            new[] { "-hide_banner", "-encoders" }, CancellationToken.None])!;
        foreach (var format in FfmpegAudioProcessor.OutputFormats.Take(2))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(encoders,
                @"\b" + System.Text.RegularExpressions.Regex.Escape(format.AudioCodec) + @"\b"))
            {
                Console.WriteLine($"SKIP: {format.Extension} encoded peak checks; this FFmpeg lacks {format.AudioCodec}");
                continue;
            }
            var lossy = Path.Combine(root, "transient_output" + format.Extension);
            var recorded = new RecordingAudioProgress();
            await processor.ProcessAsync(ffmpeg, transients, lossy,
                options with { NormalizeLoudness = true, VolumeGainDb = 6, RemoveSilence = false },
                progress: recorded, audioCodec: format.AudioCodec);
            var lossyQuality = await processor.AnalyzeQualityAsync(ffmpeg, lossy);
            Check(lossyQuality.TruePeakDbfs is <= -1.5,
                $"{format.Extension} decoded true peak stays below the ceiling (peak={lossyQuality.TruePeakDbfs})");
            var description = await (Task<string>)analyze.Invoke(null, [ffmpeg,
                new[] { "-hide_banner", "-nostdin", "-i", lossy, "-map", "0:a:0", "-t", "0", "-f", "null", "-" }, CancellationToken.None])!;
            Check(description.Contains("48000 Hz"), $"{format.Extension} output defaults to 48kHz after loudnorm");
            var events = recorded.Events.ToArray();
            Check(events.Any(value => value.Stage == "驗證輸出真峰值") && events[^1].Percent == 1
                && events.All(value => value.Percent is >= 0 and <= 1)
                && events.Zip(events.Skip(1)).All(pair => pair.First.Percent <= pair.Second.Percent),
                $"{format.Extension} progress includes validation and reaches 100% only after validation");
        }
        await processor.MatchComparisonLoudnessAsync(ffmpeg, transients, Path.Combine(root, "comparison_aac.m4a"), "aac", CancellationToken.None);
        Check((await processor.AnalyzeQualityAsync(ffmpeg, Path.Combine(root, "comparison_aac.m4a"))).TruePeakDbfs is <= -1.5,
            "A/B matching also verifies the encoded AAC true peak");
        Check((await processor.AnalyzeQualityAsync(ffmpeg, coveredMaster)).TruePeakDbfs is <= -1.5,
            "Whole-episode AAC mastering also verifies the encoded true peak");
        foreach (var kind in new[] { "invalid", "high" })
        {
            var verificationDirectory = Path.Combine(root, "verify_fixture_" + kind);
            Directory.CreateDirectory(verificationDirectory);
            var verificationOutput = Path.Combine(verificationDirectory, "output.wav");
            var verificationInput = Path.Combine(verificationDirectory, "input.wav");
            await File.WriteAllTextAsync(verificationOutput, "previous output");
            try
            {
                await processor.ProcessAsync(Environment.ProcessPath!, verificationInput, verificationOutput, options);
                throw new Exception("Expected true peak verification failure");
            }
            catch (InvalidOperationException)
            {
                Check(await File.ReadAllTextAsync(verificationOutput) == "previous output"
                    && !Directory.EnumerateFiles(verificationDirectory, ".processing_*").Any(),
                    $"{kind} peak verification preserves the old output and removes unverified files");
            }
            if (kind == "high")
                Check((await File.ReadAllLinesAsync(Path.Combine(verificationDirectory, "renders.txt"))).SequenceEqual(
                    Enumerable.Repeat(verificationInput, 4)), "Peak corrections re-render the original input with a bounded retry count");
        }
        var cancelDirectory = Path.Combine(root, "verify_fixture_cancellation");
        Directory.CreateDirectory(cancelDirectory);
        var cancelVerificationOutput = Path.Combine(cancelDirectory, "output.wav");
        await File.WriteAllTextAsync(cancelVerificationOutput, "previous output");
        using (var cancel = new CancellationTokenSource())
        {
            var task = processor.ProcessAsync(Environment.ProcessPath!, Path.Combine(cancelDirectory, "input.wav"),
                cancelVerificationOutput, options, cancellationToken: cancel.Token);
            var pid = await WaitForMarkerAsync(Path.Combine(cancelDirectory, "validation.marker"));
            cancel.Cancel();
            await ExpectCanceledAsync(task);
            Check(ProcessStopped(pid) && await File.ReadAllTextAsync(cancelVerificationOutput) == "previous output"
                && !Directory.EnumerateFiles(cancelDirectory, ".processing_*").Any(),
                "Cancel during true peak validation kills the analyzer and preserves the old output");
        }

        if (args.Length > 1)
        {
            var deepFilter = new DeepFilterNetAudioProcessor();
            var aiOutput = await deepFilter.ProcessAsync(args[1], left, Path.Combine(root, "ai"), false,
                attenuationLimitDb: 10);
            Check(File.Exists(aiOutput), "Installed DeepFilterNet accepts strength and delay compensation");
        }

        await File.WriteAllTextAsync(limitedPath, "previous valid output");
        try
        {
            await processor.ProcessAsync(ffmpeg, Path.Combine(root, "missing.wav"), limitedPath,
                options, audioCodec: "pcm_s16le");
            throw new Exception("Expected missing-input failure");
        }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("missing.wav", StringComparison.Ordinal), "Encoder errors retain diagnostic details");
        }
        Check(await File.ReadAllTextAsync(limitedPath) == "previous valid output", "Failed output preserves existing file");
        Check(!Directory.EnumerateFiles(root, ".processing_*").Any(), "No failed temporary outputs remain");

        savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            using var cancel = new CancellationTokenSource();
            var marker = Path.Combine(root, "fallback.marker");
            var task = processor.ProbeDurationAsync(Environment.ProcessPath!, marker, cancel.Token);
            var pid = await WaitForMarkerAsync(marker);
            cancel.Cancel();
            await ExpectCanceledAsync(task);
            Check(ProcessStopped(pid), "Cancel kills the fallback FFmpeg duration probe before returning");
            using var analysisCancel = new CancellationTokenSource();
            var analysisMarker = Path.Combine(root, "loudness.marker");
            var analysisTask = processor.MatchComparisonLoudnessAsync(Environment.ProcessPath!, analysisMarker,
                limitedPath, "pcm_s16le", analysisCancel.Token);
            var analysisPid = await WaitForMarkerAsync(analysisMarker);
            analysisCancel.Cancel();
            await ExpectCanceledAsync(analysisTask);
            Check(ProcessStopped(analysisPid) && await File.ReadAllTextAsync(limitedPath) == "previous valid output",
                "Cancel kills loudness analysis and preserves the existing output");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }

        File.Copy(Environment.ProcessPath!, fakeProbe, overwrite: false);
        createdFakeProbe = true;
        using (var cancel = new CancellationTokenSource())
        {
            var marker = Path.Combine(root, "waveform_fixture_cancel.marker");
            var task = processor.ReadWaveformAsync(Environment.ProcessPath!, marker, cancel.Token);
            var pid = await WaitForMarkerAsync(marker);
            cancel.Cancel();
            await ExpectCanceledAsync(task);
            Check(ProcessStopped(pid), "Cancel during PCM streaming kills waveform decoder before returning");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var marker = Path.Combine(root, "probe.marker");
            var task = processor.ProbeDurationAsync(Environment.ProcessPath!, marker, cancel.Token);
            var pid = await WaitForMarkerAsync(marker);
            cancel.Cancel();
            await ExpectCanceledAsync(task);
            Check(ProcessStopped(pid), "Cancel kills ffprobe before returning");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var marker = Path.Combine(root, "encoder.marker");
            var task = processor.ProcessAsync(Environment.ProcessPath!, marker, limitedPath, options,
                cancellationToken: cancel.Token, audioCodec: "pcm_s16le");
            var pid = await WaitForMarkerAsync(marker);
            cancel.Cancel();
            await ExpectCanceledAsync(task);
            Check(ProcessStopped(pid), "Cancel kills encoder before returning");
            Check(await File.ReadAllTextAsync(limitedPath) == "previous valid output", "Canceled output preserves existing file");
            Check(!Directory.EnumerateFiles(root, ".processing_*").Any(), "Cancel removes partial temporary file");
        }
        Console.WriteLine("All regression checks passed.");
        return 0;
    }
    finally
    {
        if (createdFakeProbe && File.Exists(fakeProbe)) File.Delete(fakeProbe);
        Directory.Delete(root, recursive: true);
    }
}

static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine($"PASS: {label}");
}

static void RunUiChecks(string root, string? snapshotDirectory)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var window = (MainWindow)typeof(MainWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                null, [typeof(bool)], null)!.Invoke([false]);
            var sharedJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "portable-preset-v1.json"));
            window.SelectedPortablePreset = PortablePresetFile.Parse(sharedJson)[0];
            window.EnableDenoise = true; window.EnableDeepFilterNet = true; window.EnableVoiceFixerDereverb = true;
            typeof(MainWindow).GetMethod("ApplyPortablePreset_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, new System.Windows.RoutedEventArgs()]);
            Check(window.LoudnessTarget == -18 && window.SilenceSeconds == 2 && window.SilencePaddingSeconds == .12 && window.SelectedChannelMode.Value == "left_mono",
                "WPF applies every imported core parameter");
            Check(!window.EnableDenoise && !window.EnableDeepFilterNet && !window.EnableVoiceFixerDereverb && window.StatusText.Contains("未套用"),
                "WPF disables foreign DSP and AI and reports unsupported SoundLab effects");
            window.SelectedPortablePreset = PortablePresetFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "portable-preset-v2.json")))[0];
            typeof(MainWindow).GetMethod("ApplyPortablePreset_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, new System.Windows.RoutedEventArgs()]);
            Check(window.EnableThreeBandEq && window.LowEqDb == 3 && window.MidEqDb == -2 && window.HighEqDb == 1.5 && window.EnableDeEsser && window.DeEsserAmount == 70 && !window.EnhanceVoiceEq,
                "WPF applies shared tone without enabling voice-thickening EQ");
            window.LowEqDb = double.NaN; window.DeEsserAmount = double.PositiveInfinity;
            Check(window.LowEqDb == 3 && window.DeEsserAmount == 70, "Invalid tone values cannot corrupt saved settings");
            var captured = (PortablePreset)typeof(MainWindow).GetMethod("CapturePortablePreset", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
            Check(captured.Core.Tone == window.SelectedPortablePreset.Core.Tone, "WPF preset export retains tone controls");
            var tabs = FindVisualChild<System.Windows.Controls.TabControl>((System.Windows.DependencyObject)window.Content)!;
            tabs.SelectedIndex = 1;
            void Flush() => window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            System.Windows.UIElement Control(string name) => (System.Windows.UIElement)window.FindName(name)!;
            window.EnableDenoise = false;
            window.EnableDeepFilterNet = false;
            window.EnableVoiceFixerDereverb = false;
            Flush();
            Check(!Control("DeepFilterPostFilterCheckBox").IsEnabled && !Control("VoiceFixerMixControls").IsEnabled
                && !Control("AiFallbackCheckBox").IsEnabled && !Control("DenoiseStrengthSelector").IsEnabled,
                "WPF disables AI-dependent options when no applicable processor is enabled");
            window.EnableDeepFilterNet = true;
            Flush();
            Check(Control("DeepFilterPostFilterCheckBox").IsEnabled && Control("DenoiseStrengthSelector").IsEnabled
                && Control("AiFallbackCheckBox").IsEnabled, "WPF DeepFilterNet toggle updates all dependent bindings");
            window.EnableDeepFilterNetPostFilter = false;
            window.EnableDeepFilterNet = false;
            window.EnableDeepFilterNet = true;
            Flush();
            Check(!window.EnableDeepFilterNetPostFilter, "Disabling a processor preserves its saved option values");
            window.EnableDeepFilterNet = false;
            window.EnableVoiceFixerDereverb = true;
            window.VoiceFixerMix = 0.6;
            Flush();
            Check(Control("VoiceFixerModeSelector").IsEnabled && Control("VoiceFixerMixControls").IsEnabled
                && Control("AiFallbackCheckBox").IsEnabled, "WPF VoiceFixer toggle enables its settings and fallback");
            window.VoiceFixerMix = 0;
            Flush();
            Check(!Control("VoiceFixerModeSelector").IsEnabled && !Control("AiFallbackCheckBox").IsEnabled
                && Control("VoiceFixerMixControls").IsEnabled, "Zero repair mix disables model settings but permits changing the mix");
            var validate = typeof(MainWindow).GetMethod("ValidateProcessingInputs", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var errors = (List<string>)validate.Invoke(window, [Array.Empty<AudioFileItem>(), Environment.ProcessPath!, root])!;
            Check(errors.Count == 0 && window.VoiceFixerStatusText.Contains("略過模型"),
                "Zero repair mix does not require an installed VoiceFixer model");
            window.EnableVoiceFixerDereverb = false;
            window.EnableDenoise = true;
            Flush();
            Check(Control("DenoiseStrengthSelector").IsEnabled && !Control("VoiceFixerMixControls").IsEnabled,
                "FFmpeg denoise alone enables the denoise strength selector");
            var busy = typeof(MainWindow).GetProperty("IsProcessing")!;
            var getInput = typeof(MainWindow).GetMethod("GetProcessingInputPath", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var completed = typeof(MainWindow).GetMethod("RecordCompletedProcessingTool", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var fallback = typeof(MainWindow).GetMethod("RecordAiFallback", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var item = new AudioFileItem { FilePath = Path.Combine(root, "測試錄音 episode.wav") };
            busy.SetValue(window, true);
            getInput.Invoke(window, [item]);
            completed.Invoke(window, ["DeepFilterNet"]);
            fallback.Invoke(window, ["VoiceFixer", item.FileName, new InvalidOperationException("模型測試失敗")]);
            completed.Invoke(window, ["FFmpeg"]);
            busy.SetValue(window, false);
            Flush();
            Check(Control("ProcessingWarningBanner").Visibility == System.Windows.Visibility.Visible
                && window.ProcessingWarningSummary.Contains("1") && window.ProcessingWarningsText.Contains("模型測試失敗"),
                "Fallback warning banner remains visible after processing completes");
            Check(window.ProcessingToolsText.Contains("DeepFilterNet") && window.ProcessingToolsText.Contains("FFmpeg")
                && !window.ProcessingToolsText.Contains("VoiceFixer") && item.ProcessingDetailsText.Contains("模型測試失敗"),
                "Completed tool report retains successful AI stages and excludes the failed model");
            window.AudioFiles.Add(item);
            item.Status = "完成";
            var wave = (PodcastBatchCleanerWpf.Controls.AudioWaveformView)window.FindName("OriginalWaveform")!;
            var list = (System.Windows.Controls.ListView)window.FindName("AudioList")!;
            list.SelectedItem = item;
            item.Duration = TimeSpan.FromSeconds(10);
            wave.SetWaveform(new AudioWaveform(Enumerable.Repeat(.5f, 100).ToArray(), 10, .1));
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(MainWindow).GetMethod("SeekOriginalWaveform", flags)!.Invoke(window, [3.25d]);
            typeof(MainWindow).GetMethod("SetTrimFromWaveform", flags)!.Invoke(window, [true]);
            typeof(MainWindow).GetMethod("SeekOriginalWaveform", flags)!.Invoke(window, [8.75d]);
            typeof(MainWindow).GetMethod("SetTrimFromWaveform", flags)!.Invoke(window, [false]);
            Check(item.TrimStartText == "3.25" && item.TrimEndText == "8.75" && !window.WaveformText.Contains("無效"),
                "Waveform cursor writes fractional trim bounds on the selected file");
            item.TrimEndText = "2";
            Check(window.WaveformText.Contains("無效"), "Editing trim fields immediately reports invalid waveform range");
            item.TrimEndText = "8.75";
            wave.Measure(new System.Windows.Size(400, 96)); wave.Arrange(new System.Windows.Rect(0, 0, 400, 96));
            Check(wave.TimeAt(100) == 2.5 && wave.TimeAt(-1) == 0 && wave.TimeAt(401) == 10,
                "Waveform click mapping clamps to the original source timeline");
            window.PlaybackSourceIndex = 1;
            var slider = (System.Windows.Controls.Slider)window.FindName("PositionSlider")!;
            slider.Value = 1;
            typeof(MainWindow).GetMethod("SetTrimFromWaveform", flags)!.Invoke(window, [false]);
            Check(item.TrimEndText == "8.75", "Processed playback position cannot replace the original waveform cursor");
            typeof(MainWindow).GetMethod("SeekOriginalWaveform", flags)!.Invoke(window, [6d]);
            Check(window.PlaybackSourceIndex == 0 && slider.Value == 6, "Waveform seeking switches processed playback back to the original timeline");
            using var waveCancel = new CancellationTokenSource();
            typeof(MainWindow).GetField("_waveformCancellation", flags)!.SetValue(window, waveCancel);
            var next = new AudioFileItem { FilePath = Path.Combine(root, "next.wav") };
            window.AudioFiles.Add(next); list.SelectedItem = next;
            Check(wave.Waveform is null && waveCancel.IsCancellationRequested,
                "Changing selected files clears the old waveform and cancels its pending decode");
            typeof(MainWindow).GetField("_waveformCancellation", flags)!.SetValue(window, null);
            list.SelectedItem = item;
            wave.SetWaveform(new AudioWaveform(Enumerable.Range(0,100).Select(i => i < 55 ? .1f + .6f*(float)Math.Abs(Math.Sin(i)) : .02f).ToArray(), 10, .1));
            typeof(MainWindow).GetMethod("UpdateWaveformRange", flags)!.Invoke(window, null);
            tabs.SelectedIndex = 3;
            Flush();
            if (snapshotDirectory is not null)
            {
                Directory.CreateDirectory(snapshotDirectory);
                var expander = FindVisualChild<System.Windows.Controls.Expander>(Control("ProcessingWarningBanner"))!;
                expander.IsExpanded = true;
                foreach (var (width, height) in new[] { (1180, 760), (980, 640) })
                {
                    var content = (System.Windows.FrameworkElement)window.Content;
                    content.Measure(new System.Windows.Size(width, height));
                    content.Arrange(new System.Windows.Rect(0, 0, width, height));
                    content.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(snapshotDirectory, $"ui_{width}x{height}.png"));
                    encoder.Save(file);
                }
            }
            busy.SetValue(window, true);
            Flush();
            Check(!window.HasProcessingWarnings && Control("ProcessingWarningBanner").Visibility == System.Windows.Visibility.Collapsed,
                "Starting a new job clears the previous banner without removing per-file history");
            window.UseAiProcessedAudio = true;
            Check((string)getInput.Invoke(window, [item])! == item.FilePath && window.HasProcessingWarnings,
                "Missing external AI audio records an explicit original-audio fallback warning");
            for (var index = 0; index < 21; index++)
                fallback.Invoke(window, ["VoiceFixer", item.FileName, new InvalidOperationException("Repeated warning")]);
            Check(window.ProcessingWarningSummary.Contains("22") && window.ProcessingWarningsText.Contains("其餘 2 則"),
                "Warning counter continues beyond the bounded detail log");
            var bundled = FfmpegAudioProcessor.TryFindBundledFfmpeg();
            if (bundled is not null)
            {
                busy.SetValue(window, false);
                busy.SetValue(window, true);
                window.SelectedOutputFormat = FfmpegAudioProcessor.OutputFormats[1];
                var ensure = typeof(MainWindow).GetMethod("EnsureOutputEncoderAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
                try
                {
                    var result = (Task<string>)ensure.Invoke(window, [Environment.ProcessPath!, new ProcessingProgressWindow(), CancellationToken.None])!;
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    _ = result.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    Check(result.GetAwaiter().GetResult() == bundled && window.ProcessingWarningsText.Contains("libmp3lame"),
                        "WPF switches an unsupported MP3 tool to bundled FFmpeg with an explicit warning");
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new Exception("WPF UI regression checks failed", failure);
}

static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
{
    if (parent is T matched) return matched;
    foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent).OfType<System.Windows.DependencyObject>())
    {
        var match = FindVisualChild<T>(child);
        if (match is not null) return match;
    }
    return null;
}

static (int Rate, short[] Samples) ReadStereoPcm(string path)
{
    using var reader = new BinaryReader(File.OpenRead(path));
    if (new string(reader.ReadChars(4)) != "RIFF") throw new Exception("Expected RIFF WAV");
    reader.ReadUInt32();
    if (new string(reader.ReadChars(4)) != "WAVE") throw new Exception("Expected WAV");
    var rate = 0;
    while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
    {
        var id = new string(reader.ReadChars(4));
        var length = reader.ReadUInt32();
        var end = reader.BaseStream.Position + length + length % 2;
        if (id == "fmt ")
        {
            if (reader.ReadUInt16() != 1 || reader.ReadUInt16() != 2) throw new Exception("Expected stereo PCM");
            rate = reader.ReadInt32();
            reader.ReadUInt32();
            reader.ReadUInt16();
            if (reader.ReadUInt16() != 16) throw new Exception("Expected 16-bit PCM");
        }
        else if (id == "data" && rate > 0)
        {
            var samples = new short[length / 2];
            for (var index = 0; index < samples.Length; index++) samples[index] = reader.ReadInt16();
            return (rate, samples);
        }
        reader.BaseStream.Position = end;
    }
    throw new Exception("Missing WAV samples");
}

static async Task ExecuteAsync(string tool, string[] arguments)
{
    var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(await error);
    await error;
}

static async Task<int> WaitForMarkerAsync(string path)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(10))
    {
        if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), out var pid)) return pid;
        await Task.Delay(20);
    }
    throw new TimeoutException("External process did not start.");
}

static bool ProcessStopped(int pid)
{
    try { using var process = Process.GetProcessById(pid); return process.HasExited; }
    catch (ArgumentException) { return true; }
}

static async Task ExpectCanceledAsync(Task task)
{
    try { await task; throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
}

sealed class RecordingAudioProgress : IProgress<AudioProcessingProgress>
{
    public System.Collections.Concurrent.ConcurrentQueue<AudioProcessingProgress> Events { get; } = new();
    public void Report(AudioProcessingProgress value) => Events.Enqueue(value);
}
