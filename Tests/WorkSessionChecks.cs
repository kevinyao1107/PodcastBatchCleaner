using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using PodcastBatchCleaner.Core.Models;
using PodcastBatchCleaner.Core.Services;
using PodcastBatchCleanerWpf;

internal static class WorkSessionChecks
{
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
    public static async Task RunAsync(string ffmpeg, string root, string? snapshotDirectory = null)
    {
        var path = Path.Combine(root,"core_work.json");
        var work = new AudioWorkSession(AudioWorkSessionFile.Format,1,JsonSerializer.SerializeToElement(new { Gain=6 }),
            [new("source.wav","output","0.2","1.5")]);
        AudioWorkSessionFile.Write(path,work);var restored=AudioWorkSessionFile.Read(path);
        Check(restored.Files.SequenceEqual(work.Files) && restored.Settings.GetProperty("Gain").GetInt32()==6,"Work document preserves source order, clip text and settings");
        var old=File.ReadAllText(path);
        try { AudioWorkSessionFile.Write(path,work with {SchemaVersion=99});throw new Exception("Bad version accepted"); }
        catch(InvalidDataException) { Check(File.ReadAllText(path)==old,"Invalid project save preserves an existing work file"); }
        File.WriteAllText(path,"{");try{AudioWorkSessionFile.Read(path);throw new Exception("Bad JSON accepted");}catch(InvalidDataException){Check(true,"Malformed work JSON reports a readable failure");}
        File.WriteAllText(path,new string(' ',AudioWorkSessionFile.MaximumBytes+1));try{AudioWorkSessionFile.Read(path);throw new Exception("Oversized work accepted");}catch(InvalidDataException){Check(true,"Oversized work is rejected before deserialization");}
        Check(AudioWorkSessionFile.ResolvePath("source.wav",root)==Path.Combine(root,"source.wav"),"Relative project sources resolve against the work file folder");
        var input=Path.Combine(root,"work_chapter_source.wav");
        var start=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-y","-f","lavfi","-i","aevalsrc=if(between(t\\,0.2\\,0.8)\\,0\\,0.3*sin(2*PI*440*t)):s=48000:d=1","-c:a","pcm_s16le",input})start.ArgumentList.Add(arg);
        using(var process=Process.Start(start)!){var log=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();if(process.ExitCode!=0)throw new Exception(await log);await log;}
        var processor=new FfmpegAudioProcessor();string a=Path.Combine(root,"work_segment_a.wav"),b=Path.Combine(root,"work_segment_b.wav");
        var options=new AudioProcessingOptions(.5,-45,false,false,false,.6,false,false,false,0,FfmpegAudioProcessor.KeepOriginalChannelMode,0,root,RemoveSilence:true,SilencePaddingSeconds:.05);
        await processor.ProcessAsync(ffmpeg,input,a,options,trimStart:TimeSpan.FromSeconds(.1),trimEnd:TimeSpan.FromSeconds(.9),audioCodec:"pcm_s16le",sampleRate:44100,channels:2);
        await processor.ProcessAsync(ffmpeg,input,b,options with{RemoveSilence=false},trimStart:TimeSpan.FromSeconds(.2),trimEnd:TimeSpan.FromSeconds(.9),audioCodec:"pcm_s16le",sampleRate:44100,channels:2);
        var probedA=await processor.ProbeDurationAsync(ffmpeg,a);
        Check(probedA is not null && probedA.Value.TotalSeconds > .15,"Short voiced bursts survive silence removal even when shorter than the silence threshold duration");
        var aDuration=probedA!.Value;var bDuration=(await processor.ProbeDurationAsync(ffmpeg,b))!.Value;
        var chapters=AudioWorkSessionFile.BuildChapters([("trimmed",aDuration),("second",bDuration)],.25);
        var merged=Path.Combine(root,"work_chapters.wav");await processor.MergeAsync(ffmpeg,[a,b],merged,.25,outputFormat:FfmpegAudioProcessor.OutputFormats.First(f=>f.Extension==".wav"));
        var duration=(await processor.ProbeDurationAsync(ffmpeg,merged))!.Value.TotalSeconds;
        Check(aDuration.TotalSeconds<.8 && Math.Abs(chapters[1].StartSeconds-aDuration.TotalSeconds-.25)<.0001 && Math.Abs(chapters[^1].EndSeconds-duration)<.001,
             $"Real trim and silence removal feed chapter positions matching the final merged duration (a={aDuration.TotalSeconds}, b={bDuration.TotalSeconds}, chapterEnd={chapters[^1].EndSeconds}, actual={duration})");
        RunUi(root, merged, chapters, snapshotDirectory);
    }
    private static void RunUi(string root, string completedFile, IReadOnlyList<EpisodeChapter> completedChapters, string? snapshotDirectory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            try{
                var window=(MainWindow)typeof(MainWindow).GetConstructor(BindingFlags.NonPublic|BindingFlags.Instance,null,[typeof(bool)],null)!.Invoke([false]);
                window.Dispatcher.Invoke(()=>{}, System.Windows.Threading.DispatcherPriority.DataBind);
                const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
                void Call(string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
                void Set(string name,object? value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
                string original=Path.Combine(root,"project-original"),moved=Path.Combine(root,"project-moved");Directory.CreateDirectory(original);Directory.CreateDirectory(moved);
                string sourceA=Path.Combine(original,"a.wav"),sourceB=Path.Combine(original,"b.wav");File.WriteAllText(sourceA,"fixture");File.WriteAllText(sourceB,"fixture");
                var b=new AudioFileItem{FilePath=sourceB,CustomOutputFileName="part-b",TrimStartText=".2",TrimEndText=".9"};
                var a=new AudioFileItem{FilePath=sourceA,CustomOutputFileName="part-a",TrimStartText=".1",TrimEndText=".7"};
                window.AudioFiles.Add(b);window.AudioFiles.Add(a);window.VolumeGainDb=6;window.EnableThreeBandEq=true;window.LowEqDb=3;window.MergeSegments=true;window.MergeGapSeconds=.3;window.PodcastTitle="測試第一集";
                var machine=Path.Combine(root,"machine-tool.exe");Set("_ffmpegPath",machine);Set("_introAudioPath",sourceB);Set("_coverImagePath",Path.Combine(original,"cover.png"));
                var path=Path.Combine(original,"work.podcast-work.json");Call("SaveWorkSession",path);
                var json=File.ReadAllText(path);Check(!json.Contains("machine-tool.exe") && AudioWorkSessionFile.Read(path).Files[0].FilePath=="b.wav","Work snapshot references relative sources and excludes machine executable paths");
                File.Copy(path,Path.Combine(moved,"work.podcast-work.json"));File.Copy(sourceA,Path.Combine(moved,"a.wav"));
                window.VolumeGainDb=0;window.AudioFiles.Clear();
                Call("LoadWorkSession",Path.Combine(moved,"work.podcast-work.json"));
                Check(window.AudioFiles[0].FilePath==Path.Combine(moved,"b.wav") && window.AudioFiles[0].Status=="來源不存在" && window.AudioFiles[1].FilePath==Path.Combine(moved,"a.wav"),"Moved work files preserve ordered rows and report missing sources without dropping edits");
                Check(window.VolumeGainDb==6 && window.LowEqDb==3 && window.MergeGapSeconds==.3 && window.PodcastTitle=="測試第一集" && window.WorkSessionText.Contains("cover.png"),"Work restore retains tone, episode metadata, gap and missing attachment warnings");
                Check((string?)typeof(MainWindow).GetField("_ffmpegPath",flags)!.GetValue(window)==machine,"Restoring a project keeps this machine's configured FFmpeg path");
                window.Dispatcher.Invoke(()=>{}, System.Windows.Threading.DispatcherPriority.DataBind);
                var list=(System.Windows.Controls.ListView)window.FindName("AudioList")!;list.SelectedItem=window.AudioFiles[0];
                var replacement=Path.Combine(moved,"replacement.wav");File.WriteAllText(replacement,"fixture");Call("RelinkWorkAudio",replacement);
                Check(window.AudioFiles[0].FilePath==replacement && window.AudioFiles[0].CustomOutputFileName=="part-b" && window.AudioFiles[0].TrimEndText==".9","Relinking a missing recording preserves trim and custom output name");
                var snapshot=window.AudioFiles.ToArray();File.WriteAllText(Path.Combine(moved,"bad.json"),json.Replace("\"VolumeGainDb\": 6","\"VolumeGainDb\": 1e999"));
                try{Call("LoadWorkSession",Path.Combine(moved,"bad.json"));throw new Exception("Invalid project accepted");}
                catch(TargetInvocationException ex)when(ex.InnerException is InvalidDataException){Check(window.AudioFiles.SequenceEqual(snapshot)&&window.VolumeGainDb==6,"Invalid numeric project settings cannot partially replace the current work");}
                Call("SetEpisodePreview",completedFile,completedChapters);
                list.SelectedItem=null;Call("SeekEpisode",completedChapters[1].StartSeconds);
                var slider=(System.Windows.Controls.Slider)window.FindName("PositionSlider")!;
                Check(window.CanPlayEpisode&&window.PlaybackSourceIndex==2&&Math.Abs(slider.Value-completedChapters[1].StartSeconds)<.0001,"Episode chapters can seek the completed file without selecting a source row");
                list.SelectedItem=window.AudioFiles[1];Check(Math.Abs(slider.Value-completedChapters[1].StartSeconds)<.0001,"Selecting another recording does not restart episode playback");
                if(snapshotDirectory is not null)
                {
                    Directory.CreateDirectory(snapshotDirectory);
                    ((System.Windows.Controls.TabControl)window.FindName("EditorTabs")!).SelectedIndex=3;
                    window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.DataBind);
                    foreach(var (width,height) in new[]{(1180,760),(980,640)})
                    {
                        var content=(System.Windows.FrameworkElement)window.Content;
                        content.Measure(new System.Windows.Size(width,height));content.Arrange(new System.Windows.Rect(0,0,width,height));content.UpdateLayout();
                        var panel=(System.Windows.Controls.ScrollViewer)window.FindName("PlaybackPanelScroll")!;
                        var heading=(System.Windows.FrameworkElement)window.FindName(width==1180?"WorkSessionHeading":"EpisodeReviewHeading")!;
                        panel.ScrollToVerticalOffset(0);content.UpdateLayout();
                        panel.ScrollToVerticalOffset(heading.TranslatePoint(new System.Windows.Point(0,0),panel).Y-8);content.UpdateLayout();
                        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(content);
                        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var file=File.Create(Path.Combine(snapshotDirectory,$"work_review_{width}x{height}.png"));encoder.Save(file);
                    }
                }
                ((System.Windows.Media.MediaPlayer)typeof(MainWindow).GetField("_player",flags)!.GetValue(window)!).Close();
                File.Delete(completedFile);Check(!window.CanPlayEpisode,"Missing completed output is detected before playback");
                Call("LoadWorkSession",Path.Combine(moved,"work.podcast-work.json"));Check(window.EpisodeChapters.Count==0&&!window.CanPlayEpisode,"Project restore clears previous rendered episode chapters");
                ((System.Windows.Media.MediaPlayer)typeof(MainWindow).GetField("_player",flags)!.GetValue(window)!).Close();
            }catch(Exception ex){failure=ex;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw new Exception("Work UI checks failed",failure);
    }
}
