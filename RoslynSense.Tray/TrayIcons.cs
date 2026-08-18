using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RoslynSense.Tray;

/// <summary>
/// Draws the notification icon at runtime instead of shipping an <c>.ico</c>.
/// </summary>
/// <remarks>
/// Three states have to be distinguishable at 16 pixels, which rules out detail: the badge is a
/// solid rounded square (colour carries "a host is loaded"), and a corner dot carries "apps are
/// running". Drawing it means the icon renders at whatever size the shell asks for, so it stays
/// sharp on a 200% display without a multi-resolution asset to maintain.
/// </remarks>
internal static class TrayIcons
{
    private static readonly Color Active = Color.FromArgb(0x68, 0x21, 0x7A);   // Roslyn purple
    private static readonly Color Idle = Color.FromArgb(0x6E, 0x6E, 0x6E);
    private static readonly Color Running = Color.FromArgb(0x3F, 0xB9, 0x50);  // "apps up" dot

    private static readonly Dictionary<(bool, bool), Icon> s_cache = [];

    public static Icon For(bool hostLoaded, bool appsRunning)
    {
        var key = (hostLoaded, appsRunning);
        if (s_cache.TryGetValue(key, out var cached))
            return cached;

        var icon = Render(hostLoaded, appsRunning, SystemInformation.SmallIconSize.Width);
        s_cache[key] = icon;
        return icon;
    }

    public static void DisposeAll()
    {
        foreach (var icon in s_cache.Values)
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
        s_cache.Clear();
    }

    private static Icon Render(bool hostLoaded, bool appsRunning, int size)
    {
        // Drawn at 4x and handed to the shell at that size: the letter's curves and the dot's
        // edge are what fall apart when GDI+ rasterises them directly into a 16px cell.
        int px = Math.Max(16, size) * 4;
        using var bitmap = new Bitmap(px, px);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float inset = px * 0.06f;
            var body = new RectangleF(inset, inset, px - inset * 2, px - inset * 2);
            using (var path = RoundedRect(body, px * 0.22f))
            using (var brush = new SolidBrush(hostLoaded ? Active : Idle))
                g.FillPath(brush, path);

            using (var font = new Font("Segoe UI", px * 0.52f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat
                   { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("R", font, Brushes.White, body, format);

            if (appsRunning)
            {
                float d = px * 0.38f;
                var dot = new RectangleF(px - d - inset, px - d - inset, d, d);
                // A ring of the background colour so the dot reads as separate from the badge
                // even against a taskbar of the same tone.
                using (var pen = new Pen(Color.White, px * 0.07f))
                using (var brush = new SolidBrush(Running))
                {
                    g.FillEllipse(brush, dot);
                    g.DrawEllipse(pen, dot);
                }
            }
        }

        // Icon.FromHandle does not own the handle; DisposeAll destroys it.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks, which
    // is a large door to open for one blittable call.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
