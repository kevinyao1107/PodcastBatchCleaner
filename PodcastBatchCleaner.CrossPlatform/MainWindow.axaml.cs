using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PodcastBatchCleaner.Core.Services;
using PodcastBatchCleaner.Core.Models;

namespace PodcastBatchCleaner.CrossPlatform;

public partial class MainWindow
{
    private AudioFileItem? _draggedRow;
    private readonly bool _persistUserSettings;
    private Avalonia.Point _dragStart;
    public MainWindow() : this(true) { }
    internal MainWindow(bool loadSettings)
    {
        _persistUserSettings = loadSettings;
        InitializeComponent();
        InitializeWorkspaceUi();
        OriginalWaveform.SeekRequested += SeekOriginalWaveform;
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.MediaFailed += Player_MediaFailed;
        _player.ResolveFfmpeg = () => _ffmpegPath;
        DataContext = this;
        if (loadSettings) LoadSettings();
        if (loadSettings) Opened += (_, _) => Window_Loaded(this, new RoutedEventArgs());
        AudioList.KeyDown += (_, e) =>
        {
            if ((e.KeyModifiers & KeyModifiers.Alt) == 0) return;
            if (e.Key == Key.Up) { MoveSelectedUp_Click(this, new RoutedEventArgs()); e.Handled = true; }
            if (e.Key == Key.Down) { MoveSelectedDown_Click(this, new RoutedEventArgs()); e.Handled = true; }
        };
        PositionSlider.AddHandler(InputElement.PointerPressedEvent, PositionSlider_PointerPressed, RoutingStrategies.Tunnel, true);
        PositionSlider.AddHandler(InputElement.PointerReleasedEvent, PositionSlider_PointerReleased, RoutingStrategies.Tunnel, true);
        PositionSlider.KeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) SeekToSlider();
        };
        AudioList.AddHandler(InputElement.PointerPressedEvent, AudioList_PointerPressed, RoutingStrategies.Tunnel, true);
        AudioList.AddHandler(InputElement.PointerReleasedEvent, AudioList_PointerReleased, RoutingStrategies.Tunnel, true);
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (!CanStartWork || _durationLoadingCancellation is not null)
        {
            e.Cancel = true; _closeAfterWork = true;
            _processingCancellation?.Cancel(); _analysisCancellation?.Cancel();
            _folderLoadingCancellation?.Cancel();
            _durationLoadingCancellation?.Cancel();
            StatusText = "正在停止工作並關閉..."; return;
        }
        SaveSettings(); _durationLoadingCancellation?.Cancel(); _playbackTimer.Stop(); _player.Close();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        try { return (await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title })).FirstOrDefault()?.TryGetLocalPath(); }
        catch (Exception ex) { StatusText = $"無法選取資料夾：{ex.Message}"; return null; }
    }
    private async Task<string?> PickFileAsync(string title, string[]? patterns = null, string? saveName = null)
    {
        try
        {
            var type = new FilePickerFileType(title) { Patterns = patterns ?? ["*"] };
            if (saveName is not null)
                return (await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                { Title = title, SuggestedFileName = saveName, FileTypeChoices = [type], ShowOverwritePrompt = true }))?.TryGetLocalPath();
            return (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = title, FileTypeFilter = [type, FilePickerFileTypes.All] })).FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception ex) { StatusText = $"無法選取檔案：{ex.Message}"; return null; }
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("選取音檔資料夾") is not { } path || !CanStartWork) return;
        try { await LoadAudioFilesAsync(path); } catch (Exception ex) { StatusText = $"載入失敗：{ex.Message}"; }
    }
    private async void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("選取輸出資料夾") is not { } path || !CanStartWork) return;
        _outputFolder = path; OnPropertyChanged(nameof(OutputFolderText));
    }
    private async void ChooseAiAudioFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("選取 AI 音檔資料夾") is not { } path || !CanStartWork) return;
        _aiAudioFolder = path; UseAiProcessedAudio = true; OnPropertyChanged(nameof(AiAudioFolderText));
    }
    private async void ChooseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("選取 ffmpeg 執行檔") is not { } path || !CanStartWork) return;
        _ffmpegPath = path; OnPropertyChanged(nameof(FfmpegPathText));
    }
    private async void ChooseDeepFilterNet_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("選取 deep-filter 執行檔") is not { } path || !CanStartWork) return;
        _deepFilterNetPath = path; EnableDeepFilterNet = true;
        OnPropertyChanged(nameof(DeepFilterNetPathText)); OnPropertyChanged(nameof(DeepFilterNetStatusText));
    }
    private async void ChooseVoiceFixer_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("選取 VoiceFixer 執行檔或啟動腳本") is not { } path || !CanStartWork) return;
        _voiceFixerPath = path; EnableVoiceFixerDereverb = true;
        OnPropertyChanged(nameof(VoiceFixerPathText)); OnPropertyChanged(nameof(VoiceFixerStatusText));
    }
    private static readonly string[] AudioPatterns = ["*.wav", "*.mp3", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.wma", "*.mp4"];
    private async void ChooseIntroAudio_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("選取片頭音檔", AudioPatterns) is not { } path || !CanStartWork) return;
        _introAudioPath = path; OnPropertyChanged(nameof(IntroAudioText));
    }
    private async void ChooseOutroAudio_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("選取片尾音檔", AudioPatterns) is not { } path || !CanStartWork) return;
        _outroAudioPath = path; OnPropertyChanged(nameof(OutroAudioText));
    }
    private async void ChooseCoverImage_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("選取封面圖片", ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"]) is not { } path || !CanStartWork) return;
        _coverImagePath = path; OnPropertyChanged(nameof(CoverImageText));
    }
    private async void SaveWork_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("保存工作檔", ["*.podcast-work.json"], "episode.podcast-work.json") is not { } path || !CanStartWork) return;
        try { SaveWorkSession(path); } catch (Exception ex) { WorkSessionText = StatusText = $"保存失敗：{ex.Message}"; }
    }
    private async void LoadWork_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("恢復工作檔", ["*.json"]) is not { } path || !CanStartWork) return;
        try { LoadWorkSession(path); } catch (Exception ex) { WorkSessionText = StatusText = $"恢復失敗：{ex.Message}"; }
    }
    private async void RelinkWorkAudio_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("重新指定音檔", AudioPatterns) is not { } path || !CanStartWork) return;
        try { RelinkWorkAudio(path); } catch (Exception ex) { StatusText = ex.Message; }
    }
    private async void ImportPortablePresets_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("匯入共用預設", ["*.json"]) is not { } path || !CanStartWork) return;
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("預設不可超過 1 MB。");
            var presets = PortablePresetFile.Parse(await File.ReadAllTextAsync(path));
            if (PortablePresets.Count + presets.Count > 100) throw new InvalidDataException("最多 100 組預設。");
            foreach (var preset in presets) PortablePresets.Add(preset);
            SelectedPortablePreset = presets.FirstOrDefault(); SaveSettings(); StatusText = $"已匯入 {presets.Count} 組預設。";
        }
        catch (Exception ex) { StatusText = $"匯入失敗：{ex.Message}"; }
    }
    private async void ExportPortablePresets_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("匯出共用預設", ["*.json"], "AudioStudio_presets.json") is not { } path || !CanStartWork) return;
        try
        {
            await File.WriteAllTextAsync(path, PortablePresetFile.Serialize(PortablePresets.Count > 0 ? PortablePresets : [CapturePortablePreset()]));
            StatusText = "已匯出共用預設。";
        }
        catch (Exception ex) { StatusText = $"匯出失敗：{ex.Message}"; }
    }
    private void AudioList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control control && (control is TextBox || control.FindAncestorOfType<TextBox>() is not null)) return;
        OpenSelectedForPlayback(true);
    }
    private void AudioList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedRow = null;
        if (!CanStartWork || !e.GetCurrentPoint(AudioList).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Control control || control is TextBox || control.FindAncestorOfType<TextBox>() is not null) return;
        _draggedRow = (control as DataGridRow ?? control.FindAncestorOfType<DataGridRow>())?.DataContext as AudioFileItem;
        _dragStart = e.GetPosition(AudioList);
    }
    private void AudioList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var source = _draggedRow; _draggedRow = null;
        var point = e.GetPosition(AudioList);
        if (!CanStartWork || source is null || (Math.Abs(point.X - _dragStart.X) < 8 && Math.Abs(point.Y - _dragStart.Y) < 8)) return;
        var target = AudioList.GetVisualDescendants().OfType<DataGridRow>()
            .FirstOrDefault(row => new Avalonia.Rect(row.Bounds.Size).Contains(e.GetPosition(row)))?.DataContext as AudioFileItem;
        if (target is null || source == target) return;
        var from = AudioFiles.IndexOf(source); var to = AudioFiles.IndexOf(target);
        if (from < 0 || to < 0) return;
        AudioFiles.Move(from, to); AudioList.SelectedItem = source; AudioList.ScrollIntoView(source, null);
        StatusText = "已調整合併順序。";
    }
    private void PositionSlider_PointerPressed(object? sender, PointerPressedEventArgs e) => _isDraggingPosition = true;
    private void PositionSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    { _isDraggingPosition = false; SeekToSlider(); }
    private void OpenFolder(string folder) => OpenLocation(folder, false);
    private void OpenFileLocation(string path) => OpenLocation(path, true);
    private void OpenLocation(string path, bool reveal)
    {
        try
        {
            var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            if (OperatingSystem.IsWindows())
            { info.FileName = "explorer.exe"; if (reveal) info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path); }
            else if (OperatingSystem.IsMacOS())
            { info.FileName = "/usr/bin/open"; if (reveal) info.ArgumentList.Add("-R"); info.ArgumentList.Add(path); }
            else { info.FileName = "xdg-open"; info.ArgumentList.Add(reveal ? Path.GetDirectoryName(path)! : path); }
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) { StatusText = $"開啟位置失敗：{ex.Message}"; }
    }
}
