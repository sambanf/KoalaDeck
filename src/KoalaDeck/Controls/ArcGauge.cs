using System.Windows;
using System.Windows.Media;

namespace KoalaDeck.Controls;

/// <summary>
/// 270° ring gauge: the outer arc shows <see cref="Value"/> (volume) and the thin inner arc shows <see cref="Peak"/>
/// (live audio level). Both are 0..<see cref="Maximum"/>.
/// </summary>
public sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0);
    public static readonly DependencyProperty PeakProperty = Register(nameof(Peak), 0.0);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 100.0);
    public static readonly DependencyProperty ThicknessProperty = Register(nameof(Thickness), 14.0);
    public static readonly DependencyProperty PeakThicknessProperty = Register(nameof(PeakThickness), 5.0);
    public static readonly DependencyProperty StartAngleProperty = Register(nameof(StartAngle), 135.0);
    public static readonly DependencyProperty SweepAngleProperty = Register(nameof(SweepAngle), 270.0);
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush?>(nameof(TrackBrush), null);
    public static readonly DependencyProperty ValueBrushProperty = Register<Brush?>(nameof(ValueBrush), null);
    public static readonly DependencyProperty PeakBrushProperty = Register<Brush?>(nameof(PeakBrush), null);

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public double PeakThickness
    {
        get => (double)GetValue(PeakThicknessProperty);
        set => SetValue(PeakThicknessProperty, value);
    }

    /// <summary>Degrees, 0 = 3 o'clock, clockwise.</summary>
    public double StartAngle
    {
        get => (double)GetValue(StartAngleProperty);
        set => SetValue(StartAngleProperty, value);
    }

    public double SweepAngle
    {
        get => (double)GetValue(SweepAngleProperty);
        set => SetValue(SweepAngleProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? ValueBrush
    {
        get => (Brush?)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public Brush? PeakBrush
    {
        get => (Brush?)GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || Maximum <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2 - Thickness / 2;
        DrawArc(dc, center, outer, 1, TrackBrush, Thickness);
        DrawArc(dc, center, outer, Value / Maximum, ValueBrush, Thickness);

        double inner = outer - Thickness / 2 - 8 - PeakThickness / 2;
        if (inner > 0)
        {
            DrawArc(dc, center, inner, 1, TrackBrush, PeakThickness);
            DrawArc(dc, center, inner, Peak / Maximum, PeakBrush, PeakThickness);
        }
    }

    private void DrawArc(DrawingContext dc, Point center, double radius, double fraction, Brush? brush, double thickness)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (brush is null || fraction < 0.004 || radius <= 0)
        {
            return;
        }

        double sweep = SweepAngle * fraction;
        Point start = PointOnCircle(center, radius, StartAngle);
        Point end = PointOnCircle(center, radius, StartAngle + sweep);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(ArcGauge),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
