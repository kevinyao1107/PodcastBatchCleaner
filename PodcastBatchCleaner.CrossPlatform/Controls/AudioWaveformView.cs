using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PodcastBatchCleaner.Core.Models;

namespace PodcastBatchCleaner.CrossPlatform.Controls;

public sealed class AudioWaveformView : Control
{
    public AudioWaveform? Waveform { get; private set; }
    private double _start, _end, _position;
    private bool _validRange;
    public event Action<double>? SeekRequested;
    public AudioWaveformView() { Focusable = true; Cursor = new Cursor(StandardCursorType.Hand); }
    public void SetWaveform(AudioWaveform? waveform) { Waveform = waveform; _position = 0; InvalidateVisual(); }
    public void SetRange(double start, double end, bool valid) { _start = start; _end = end; _validRange = valid; InvalidateVisual(); }
    public void SetPosition(double seconds) { _position = seconds; InvalidateVisual(); }
    public double TimeAt(double x) => Waveform is null || Bounds.Width <= 0 ? 0 : Math.Clamp(x / Bounds.Width, 0, 1) * Waveform.DurationSeconds;
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    { base.OnPointerPressed(e); if (Waveform is null) return; Focus(); SeekRequested?.Invoke(TimeAt(e.GetPosition(this).X)); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (Waveform is null) return;
        var target = e.Key switch { Key.Home => 0d, Key.End => Waveform.DurationSeconds, Key.Left => _position - 1, Key.Right => _position + 1, _ => double.NaN };
        if (!double.IsFinite(target)) return;
        SeekRequested?.Invoke(Math.Clamp(target, 0, Waveform.DurationSeconds)); e.Handled = true;
    }
    public override void Render(DrawingContext dc)
    {
        base.Render(dc); var width = Bounds.Width; var height = Bounds.Height;
        dc.DrawRectangle(new SolidColorBrush(Color.Parse("#151515")), null, new Rect(0, 0, width, height));
        if (Waveform is null || Waveform.DurationSeconds <= 0 || width <= 0 || height <= 0) return;
        var wave = new SolidColorBrush(Color.Parse("#58BFD5"));
        for (var i = 0; i < Waveform.Peaks.Length; i++)
        {
            var x1 = i * Waveform.BinSeconds / Waveform.DurationSeconds * width;
            var x2 = Math.Min(width, (i + 1) * Waveform.BinSeconds / Waveform.DurationSeconds * width);
            var amplitude = Math.Clamp(Waveform.Peaks[i], 0, 1) * (height / 2 - 4);
            dc.DrawRectangle(wave, null, new Rect(x1, height / 2 - amplitude, Math.Max(.5, x2 - x1), Math.Max(1, amplitude * 2)));
        }
        if (_validRange)
        {
            var x1 = Math.Clamp(_start / Waveform.DurationSeconds, 0, 1) * width;
            var x2 = Math.Clamp(_end / Waveform.DurationSeconds, 0, 1) * width;
            var shade = new SolidColorBrush(Color.FromArgb(185, 11, 11, 11));
            dc.DrawRectangle(shade, null, new Rect(0, 0, x1, height));
            dc.DrawRectangle(shade, null, new Rect(x2, 0, width - x2, height));
            dc.DrawLine(new Pen(Brushes.Gold, 1), new Point(x1, 0), new Point(x1, height));
            dc.DrawLine(new Pen(Brushes.Gold, 1), new Point(x2, 0), new Point(x2, height));
        }
        var x = Math.Clamp(_position / Waveform.DurationSeconds, 0, 1) * width;
        dc.DrawLine(new Pen(Brushes.White, 2), new Point(x, 0), new Point(x, height));
        if (IsFocused) dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2)));
    }
}
