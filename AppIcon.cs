using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RegionShare;

/// A little monitor with a green region on it, drawn at runtime so there's no .ico to ship.
static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var screen = new SolidBrush(Color.FromArgb(36, 41, 51));
            using var stand = new SolidBrush(Color.FromArgb(90, 96, 110));
            using var region = new Pen(Color.FromArgb(0, 200, 90), 3f);
            g.FillRectangle(screen, 1, 3, 30, 21);
            g.FillRectangle(stand, 11, 25, 10, 2);
            g.FillRectangle(stand, 7, 27, 18, 2);
            g.DrawRectangle(region, 5, 7, 13, 13);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
