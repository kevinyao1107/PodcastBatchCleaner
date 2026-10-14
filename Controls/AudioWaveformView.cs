using System.Windows;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Input;
using System.Windows.Media;
using PodcastBatchCleaner.Core.Models;

namespace PodcastBatchCleanerWpf.Controls;

public sealed class AudioWaveformView : FrameworkElement
{
    public AudioWaveform? Waveform { get; private set; }
    private double _start, _end, _position;
    private bool _validRange;
    public event Action<double>? SeekRequested;
    public AudioWaveformView() { Focusable = true; Cursor = Cursors.Hand; }
    public void SetWaveform(AudioWaveform? waveform) { Waveform = waveform; _position = 0; InvalidateVisual(); }
    public void SetRange(double start, double end, bool valid)
    { _start = start; _end = end; _validRange = valid; InvalidateVisual(); }
    public void SetPosition(double seconds) { _position = seconds; InvalidateVisual(); }
    public double TimeAt(double x) => Waveform is null || ActualWidth <= 0 ? 0
        : Math.Clamp(x / ActualWidth, 0, 1) * Waveform.DurationSeconds;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Waveform is null) return;
        Focus(); SeekRequested?.Invoke(TimeAt(e.GetPosition(this).X)); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Waveform is null) return;
        var target = e.Key switch { Key.Home => 0d, Key.End => Waveform.DurationSeconds,
            Key.Left => _position - 1, Key.Right => _position + 1, _ => double.NaN };
        if (!double.IsFinite(target)) return;
        SeekRequested?.Invoke(Math.Clamp(target, 0, Waveform.DurationSeconds)); e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth; var height = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 23, 34)), null, new Rect(0, 0, width, height));
        if (Waveform is null || width <= 0 || height <= 0) return;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(88, 191, 213)), 1);
        for (var i = 0; i < Waveform.Peaks.Length; i++)
        {
            var x1 = i * Waveform.BinSeconds / Waveform.DurationSeconds * width;
            var x2 = Math.Min(width, (i + 1) * Waveform.BinSeconds / Waveform.DurationSeconds * width);
            var amplitude = Math.Clamp(Waveform.Peaks[i], 0, 1) * (height / 2 - 4);
            dc.DrawRectangle(pen.Brush, null, new Rect(x1, height / 2 - amplitude,
                Math.Max(.5, x2 - x1), Math.Max(1, amplitude * 2)));
        }
        if (_validRange)
        {
            var x1 = Math.Clamp(_start / Waveform.DurationSeconds, 0, 1) * width;
            var x2 = Math.Clamp(_end / Waveform.DurationSeconds, 0, 1) * width;
            var shade = new SolidColorBrush(Color.FromArgb(185, 9, 11, 16));
            dc.DrawRectangle(shade, null, new Rect(0, 0, x1, height));
            dc.DrawRectangle(shade, null, new Rect(x2, 0, width - x2, height));
            var boundary = new Pen(Brushes.Gold, 1);
            dc.DrawLine(boundary, new Point(x1, 0), new Point(x1, height));
            dc.DrawLine(boundary, new Point(x2, 0), new Point(x2, height));
        }
        if (double.IsFinite(_position))
        {
            var x = Math.Clamp(_position / Waveform.DurationSeconds, 0, 1) * width;
            dc.DrawLine(new Pen(Brushes.White, 2), new Point(x, 0), new Point(x, height));
        }
        if (IsKeyboardFocusWithin) dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(1, 1, Math.Max(0,width-2), Math.Max(0,height-2)));
    }
}
