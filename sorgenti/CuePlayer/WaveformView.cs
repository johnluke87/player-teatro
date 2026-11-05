using System.Windows;
using System.Windows.Media;

namespace CuePlayer;

/// <summary>
/// Waveform overview of a track: one bar per peak bucket, the part already played in the
/// state colour, the part outside the trim (inizio/fine) dimmed.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(object), typeof(WaveformView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RestBrushProperty = DependencyProperty.Register(
        nameof(RestBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x7E)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StartFractionProperty = DependencyProperty.Register(
        nameof(StartFraction), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EndFractionProperty = DependencyProperty.Register(
        nameof(EndFraction), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>float[] of peaks (typed as object: XAML templates cannot bind array-typed properties).</summary>
    public object? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush PlayedBrush { get => (Brush)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush RestBrush { get => (Brush)GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }
    public double StartFraction { get => (double)GetValue(StartFractionProperty); set => SetValue(StartFractionProperty, value); }
    public double EndFraction { get => (double)GetValue(EndFractionProperty); set => SetValue(EndFractionProperty, value); }

    private static readonly Brush HitBrush = Brushes.Transparent;
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x16, 0x18, 0x1C));

    static WaveformView()
    {
        HitBrush.Freeze();
        DimBrush.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(HitBrush, null, new Rect(0, 0, w, h)); // clickable everywhere (seek)
        var peaks = Peaks as float[];
        if (peaks == null || peaks.Length == 0) return;

        const double barW = 2, gap = 1;
        int bars = Math.Max(1, (int)(w / (barW + gap)));
        double mid = h / 2;
        double playedX = Math.Clamp(Progress, 0, 1) * w;
        for (int b = 0; b < bars; b++)
        {
            // max of the peak buckets that fall under this bar
            int from = b * peaks.Length / bars, to = Math.Max(from + 1, (b + 1) * peaks.Length / bars);
            float p = 0;
            for (int i = from; i < to && i < peaks.Length; i++) if (peaks[i] > p) p = peaks[i];
            double bh = Math.Max(1.5, Math.Sqrt(p) * h); // sqrt: quiet parts stay visible
            double x = b * (barW + gap);
            dc.DrawRectangle(x < playedX ? PlayedBrush : RestBrush, null, new Rect(x, mid - bh / 2, barW, bh));
        }

        // parts excluded by the trim
        double s = Math.Clamp(StartFraction, 0, 1) * w, e = Math.Clamp(EndFraction, 0, 1) * w;
        if (s > 0) dc.DrawRectangle(DimBrush, null, new Rect(0, 0, s, h));
        if (e < w) dc.DrawRectangle(DimBrush, null, new Rect(e, 0, w - e, h));
    }
}
