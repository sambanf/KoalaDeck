using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace KoalaDeck.Controls;

/// <summary>Semi-transparent snapshot of a dragged element that follows the cursor (drawn on the window's adorner layer).</summary>
public sealed class DragGhostAdorner : Adorner
{
    private readonly Rectangle _ghost;
    private readonly Point _grabOffset;
    private Point _position;

    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source, Point grabOffset)
        : base(adornedElement)
    {
        _grabOffset = grabOffset;
        IsHitTestVisible = false;
        _ghost = new Rectangle
        {
            Width = source.ActualWidth,
            Height = source.ActualHeight,
            Fill = new ImageBrush(Snapshot(source)),
            Opacity = 0.9,
            RadiusX = 8,
            RadiusY = 8,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5 },
        };
        AddVisualChild(_ghost);
    }

    protected override int VisualChildrenCount => 1;

    /// <summary>Moves the ghost; <paramref name="position"/> is relative to the adorned element.</summary>
    public void MoveTo(Point position)
    {
        _position = position;
        InvalidateArrange();
    }

    protected override Visual GetVisualChild(int index) => _ghost;

    protected override Size MeasureOverride(Size constraint)
    {
        _ghost.Measure(constraint);
        return base.MeasureOverride(constraint);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ghost.Arrange(new Rect(new Point(_position.X - _grabOffset.X, _position.Y - _grabOffset.Y), _ghost.DesiredSize));
        return finalSize;
    }

    /// <summary>Freezes the current look of the element (before it is dimmed as the drag source).</summary>
    private static BitmapSource Snapshot(FrameworkElement source)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(source);
        int width = Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // Render through a VisualBrush so the element's own offset in its parent does not shift the snapshot.
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, source.ActualWidth, source.ActualHeight);
            context.DrawRoundedRectangle((Brush)source.FindResource("Surface3Brush"), null, bounds, 8, 8);
            context.DrawRectangle(new VisualBrush(source), null, bounds);
        }

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
