using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using KoalaDeck.Interop;

namespace KoalaDeck.Services;

/// <summary>
/// GDI+ rendering of the Koala Deck mark for the tray icon, with a connection-status dot.
/// Geometry uses a 256-unit grid shared with the XAML logo (Themes/Icons.xaml) and tools/make-icon.ps1.
/// </summary>
internal static class KoalaArt
{
    public static readonly Color ConnectedColor = Color.FromArgb(34, 197, 94);
    public static readonly Color SearchingColor = Color.FromArgb(245, 158, 11);
    public static readonly Color DisconnectedColor = Color.FromArgb(239, 68, 68);

    public static Icon CreateIcon(int size, Color? statusColor)
    {
        using Bitmap bitmap = Render(size, statusColor);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone(); // owns its own handle
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public static Bitmap Render(int size, Color? statusColor)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 256f, size / 256f);

        Ellipse(g, "#8D99AE", 56, 83, 48, 48);   // ears
        Ellipse(g, "#8D99AE", 200, 83, 48, 48);
        Ellipse(g, "#E7C6D4", 60, 87, 27, 27);   // inner ears
        Ellipse(g, "#E7C6D4", 196, 87, 27, 27);
        Ellipse(g, "#AEB8C9", 128, 141, 88, 80); // head
        Ellipse(g, "#1C2029", 92, 131, 9, 9);    // eyes
        Ellipse(g, "#1C2029", 164, 131, 9, 9);
        Ellipse(g, "#2A2F3B", 128, 161, 25, 33); // nose
        Ellipse(g, "#4B5466", 119, 147, 6, 9);   // nose highlight

        if (statusColor is Color dot)
        {
            // Punch a transparent ring around the dot so it stays readable on light and dark taskbars.
            g.CompositingMode = CompositingMode.SourceCopy;
            Ellipse(g, Color.Transparent, 200, 200, 56, 56);
            g.CompositingMode = CompositingMode.SourceOver;
            Ellipse(g, dot, 200, 200, 42, 42);
        }

        return bitmap;
    }

    private static void Ellipse(Graphics g, string hex, float cx, float cy, float rx, float ry) =>
        Ellipse(g, ColorTranslator.FromHtml(hex), cx, cy, rx, ry);

    private static void Ellipse(Graphics g, Color color, float cx, float cy, float rx, float ry)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - rx, cy - ry, rx * 2, ry * 2);
    }
}
