using System.Drawing;
using System.Drawing.Drawing2D;

namespace VolumeBridge.Ui;

/// <summary>Speaker glyph drawn at runtime (no binary assets in the repo).</summary>
static class AppIcon
{
    public static Icon Create(int size, bool ok)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using var bg = new SolidBrush(ok ? Color.FromArgb(0x1F, 0x6F, 0xEB) : Color.FromArgb(0x8A, 0x8F, 0x98));
            g.FillEllipse(bg, 1 * s, 1 * s, 30 * s, 30 * s);
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white, [
                new PointF(7 * s, 12.5f * s), new PointF(11.5f * s, 12.5f * s), new PointF(17 * s, 8 * s),
                new PointF(17 * s, 24 * s), new PointF(11.5f * s, 19.5f * s), new PointF(7 * s, 19.5f * s)]);
            using var pen = new Pen(Color.White, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 15 * s, 11 * s, 7 * s, 10 * s, -50, 100);
            g.DrawArc(pen, 15 * s, 7.5f * s, 11 * s, 17 * s, -50, 100);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
