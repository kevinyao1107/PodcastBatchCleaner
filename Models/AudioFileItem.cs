using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace PodcastBatchCleaner.Core.Models;

public sealed class AudioFileItem : INotifyPropertyChanged
{
    private string _status = "待處理";
    private string? _processedPath;
    private TimeSpan? _duration;
    private bool _isProcessing;
    private string _customOutputFileName = string.Empty;

    public required string FilePath { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    public string Folder => Path.GetDirectoryName(FilePath) ?? string.Empty;

    public TimeSpan? Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    public string DurationText => Duration is null ? "--:--" : FormatTime(Duration.Value);

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string? ProcessedPath
    {
        get => _processedPath;
        set
        {
            if (SetField(ref _processedPath, value))
            {
                OnPropertyChanged(nameof(ProcessedFileName));
                OnPropertyChanged(nameof(HasProcessedFile));
            }
        }
    }

    public string ProcessedFileName => ProcessedPath is null ? string.Empty : Path.GetFileName(ProcessedPath);

    public bool HasProcessedFile => !string.IsNullOrWhiteSpace(ProcessedPath) && File.Exists(ProcessedPath);

    public string CustomOutputFileName
    {
        get => _customOutputFileName;
        set => SetField(ref _customOutputFileName, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetField(ref _isProcessing, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);

        if (propertyName == nameof(Duration))
        {
            OnPropertyChanged(nameof(DurationText));
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
