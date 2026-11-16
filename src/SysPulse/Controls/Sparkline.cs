using System.Windows;
using System.Windows.Media;

namespace SysPulse.Controls;

/// <summary>Lightweight scrolling line graph (one or two series), newest value on the right.</summary>
public sealed class Sparkline : FrameworkElement
{
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1));

    public static readonly DependencyProperty ValuesProperty = Reg<IReadOnlyList<double>?>(nameof(Values), null);
    public static readonly DependencyProperty Values2Property = Reg<IReadOnlyList<double>?>(nameof(Values2), null);
    public static readonly DependencyProperty StrokeProperty = Reg<Brush?>(nameof(Stroke), Brushes.DeepSkyBlue);
    public static readonly DependencyProperty Stroke2Property = Reg<Brush?>(nameof(Stroke2), null);
    /// <summary>Fixed top of the scale (e.g. 100 for percentages). 0 = auto-scale to the data.</summary>
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), 0.0);
    public static readonly DependencyProperty CapacityProperty = Reg(nameof(Capacity), 60);

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? Values2 { get => (IReadOnlyList<double>?)GetValue(Values2Property); set => SetValue(Values2Property, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Stroke2 { get => (Brush?)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        double max = Maximum;
        if (max <= 0)
        {
            double peak = Math.Max(Peak(Values), Peak(Values2));
            max = Math.Max(peak * 1.15, 1024); // auto-scale, floor of 1 KB so idle lines sit at the bottom
        }

        DrawSeries(dc, Values, Stroke, max, w, h);
        DrawSeries(dc, Values2, Stroke2, max, w, h);
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double>? v, Brush? stroke, double max, double w, double h)
    {
        if (v == null || v.Count < 2 || stroke == null) return;

        double step = w / Math.Max(Capacity - 1, 1);
        double x0 = w - (v.Count - 1) * step;
        var pts = new Point[v.Count];
        for (int i = 0; i < v.Count; i++)
            pts[i] = new Point(x0 + i * step, h - 1 - Math.Clamp(v[i] / max, 0, 1) * (h - 2));

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, h), isFilled: true, isClosed: true);
            ctx.PolyLineTo(pts, isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(pts[^1].X, h), false, false);
        }
        area.Freeze();

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo(pts[1..], isStroked: true, isSmoothJoin: true);
        }
        line.Freeze();

        Brush fill = stroke is SolidColorBrush sc
            ? Freeze(new SolidColorBrush(Color.FromArgb(40, sc.Color.R, sc.Color.G, sc.Color.B)))
            : Brushes.Transparent;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }

    private static double Peak(IReadOnlyList<double>? v) => v is { Count: > 0 } ? v.Max() : 0;

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(Sparkline),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));
}
