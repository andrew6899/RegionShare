using static RegionShare.Native;

namespace RegionShare;

/// Thin click-through border drawn just outside the shared region so you can see what's being shared.
/// It's excluded from screen capture, so it never appears in the stream even if it overlaps the edge.
sealed class FrameForm : Form
{
    public const int Thickness = 3;
    static readonly Color BorderColor = Color.FromArgb(0, 200, 90);

    public FrameForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;   // the middle is see-through (and click-through)
        Bounds = new Rectangle(-10000, -10000, 10, 10);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE))
            Log.Info("WDA_EXCLUDEFROMCAPTURE unavailable (needs Win10 2004+); border stays outside the region so it's still not captured");
    }

    public void SetRegion(Rectangle region)
    {
        Bounds = Rectangle.Inflate(region, Thickness, Thickness);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(BorderColor);
        var g = e.Graphics;
        int t = Thickness;
        g.FillRectangle(b, 0, 0, Width, t);
        g.FillRectangle(b, 0, Height - t, Width, t);
        g.FillRectangle(b, 0, 0, t, Height);
        g.FillRectangle(b, Width - t, 0, t, Height);
    }
}
