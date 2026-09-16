using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using static RegionShare.Native;

namespace RegionShare;

/// Full-monitor overlay for drawing or adjusting the shared region.
/// Per-pixel-alpha layered window: dimmed outside the selection, clear inside, mouse input everywhere.
sealed class RegionPickerForm : Form
{
    public static readonly Size[] Presets = { new(1920, 1080), new(1600, 900), new(1280, 720), new(2560, 1440) };
    const int HandleSize = 10;
    const int MinSide = 16;

    readonly Rectangle _mon;
    Rectangle _sel;                         // monitor-local coordinates
    enum Mode { None, New, Move, Resize }
    Mode _mode;
    string _edge = "";
    Point _anchor;
    Rectangle _orig;
    Bitmap? _bmp;
    Graphics? _g;
    long _lastDraw;

    /// Screen coordinates; null if cancelled.
    public Rectangle? Result { get; private set; }

    public RegionPickerForm(Rectangle monitor, Rectangle? current)
    {
        _mon = monitor;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Bounds = monitor;
        if (current is Rectangle c)
        {
            c.Offset(-monitor.X, -monitor.Y);
            _sel = Rectangle.Intersect(c, Local);
        }
    }

    Rectangle Local => new(0, 0, _mon.Width, _mon.Height);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Draw(true);
    }

    // Layered window: everything is painted through UpdateLayeredWindow, never WM_PAINT.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _g?.Dispose();
        _bmp?.Dispose();
        base.OnFormClosed(e);
    }

    // ---------------- mouse ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _orig = _sel;
        _anchor = e.Location;
        _edge = _sel.IsEmpty ? "" : HitEdge(e.Location);
        if (_edge != "") _mode = Mode.Resize;
        else if (_sel.Contains(e.Location)) _mode = Mode.Move;
        else { _mode = Mode.New; _sel = new Rectangle(e.Location, Size.Empty); }
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.Location;
        switch (_mode)
        {
            case Mode.None:
                Cursor = CursorFor(_sel.IsEmpty ? "" : HitEdge(p), _sel.Contains(p));
                return;
            case Mode.New:
                _sel = Normalize(_anchor, p);
                break;
            case Mode.Move:
                _sel = new Rectangle(_orig.X + p.X - _anchor.X, _orig.Y + p.Y - _anchor.Y, _orig.Width, _orig.Height);
                break;
            case Mode.Resize:
                int l = _orig.Left, t = _orig.Top, r = _orig.Right, b = _orig.Bottom;
                if (_edge.Contains('w')) l = p.X;
                if (_edge.Contains('e')) r = p.X;
                if (_edge.Contains('n')) t = p.Y;
                if (_edge.Contains('s')) b = p.Y;
                _sel = Normalize(new Point(l, t), new Point(r, b));
                break;
        }
        Clamp();
        Draw(false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_mode == Mode.None) return;
        _mode = Mode.None;
        Capture = false;
        Clamp();
        Draw(true);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _sel.Contains(e.Location)) Confirm();
    }

    // ---------------- keyboard ----------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.Shift ? 1 : 10;
        e.Handled = true;
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); break;
            case Keys.Enter: Confirm(); break;
            case Keys.D1 or Keys.NumPad1: SetPreset(0); break;
            case Keys.D2 or Keys.NumPad2: SetPreset(1); break;
            case Keys.D3 or Keys.NumPad3: SetPreset(2); break;
            case Keys.D4 or Keys.NumPad4: SetPreset(3); break;
            case Keys.Left:  Nudge(-step, 0); break;
            case Keys.Right: Nudge(step, 0); break;
            case Keys.Up:    Nudge(0, -step); break;
            case Keys.Down:  Nudge(0, step); break;
            default: e.Handled = false; break;
        }
    }

    void SetPreset(int i)
    {
        var s = Presets[i];
        var center = _sel.IsEmpty ? new Point(_mon.Width / 2, _mon.Height / 2) : new Point(_sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2);
        _sel = new Rectangle(center.X - s.Width / 2, center.Y - s.Height / 2, s.Width, s.Height);
        _mode = Mode.Move; Clamp(); _mode = Mode.None;   // shift into view rather than crop
        Draw(true);
    }

    void Nudge(int dx, int dy)
    {
        if (_sel.IsEmpty) return;
        _sel.Offset(dx, dy);
        _mode = Mode.Move; Clamp(); _mode = Mode.None;
        Draw(true);
    }

    void Confirm()
    {
        if (_sel.Width < MinSide || _sel.Height < MinSide) return;
        Result = new Rectangle(_sel.X + _mon.X, _sel.Y + _mon.Y, _sel.Width, _sel.Height);
        Close();
    }

    // ---------------- geometry ----------------

    static Rectangle Normalize(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    void Clamp()
    {
        if (_mode == Mode.Move)
        {
            _sel.Width = Math.Min(_sel.Width, _mon.Width);
            _sel.Height = Math.Min(_sel.Height, _mon.Height);
            _sel.X = Math.Clamp(_sel.X, 0, _mon.Width - _sel.Width);
            _sel.Y = Math.Clamp(_sel.Y, 0, _mon.Height - _sel.Height);
        }
        else
        {
            _sel = Rectangle.Intersect(_sel, Local);
        }
    }

    /// "", or a compass edge/corner name ("n", "sw", ...) when p is on the selection's border.
    string HitEdge(Point p)
    {
        const int t = 8;
        if (p.X < _sel.Left - t || p.X > _sel.Right + t || p.Y < _sel.Top - t || p.Y > _sel.Bottom + t) return "";
        string s = "";
        if (Math.Abs(p.Y - _sel.Top) <= t) s += "n"; else if (Math.Abs(p.Y - _sel.Bottom) <= t) s += "s";
        if (Math.Abs(p.X - _sel.Left) <= t) s += "w"; else if (Math.Abs(p.X - _sel.Right) <= t) s += "e";
        return s;
    }

    static Cursor CursorFor(string edge, bool inside) => edge switch
    {
        "n" or "s" => Cursors.SizeNS,
        "w" or "e" => Cursors.SizeWE,
        "nw" or "se" => Cursors.SizeNWSE,
        "ne" or "sw" => Cursors.SizeNESW,
        _ => inside ? Cursors.SizeAll : Cursors.Cross,
    };

    IEnumerable<Rectangle> HandleRects()
    {
        int cx = _sel.X + _sel.Width / 2, cy = _sel.Y + _sel.Height / 2, h = HandleSize / 2;
        foreach (var (x, y) in new[] { (_sel.Left, _sel.Top), (cx, _sel.Top), (_sel.Right, _sel.Top), (_sel.Right, cy),
                                       (_sel.Right, _sel.Bottom), (cx, _sel.Bottom), (_sel.Left, _sel.Bottom), (_sel.Left, cy) })
            yield return new Rectangle(x - h, y - h, HandleSize, HandleSize);
    }

    // ---------------- drawing ----------------

    void Draw(bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastDraw < 12) return;   // ~80 Hz cap while dragging
        _lastDraw = now;

        if (_bmp == null || _bmp.Width != Width || _bmp.Height != Height)
        {
            _g?.Dispose();
            _bmp?.Dispose();
            _bmp = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            _g = Graphics.FromImage(_bmp);
        }
        var g = _g!;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.FromArgb(120, 0, 0, 0));
        if (!_sel.IsEmpty)
        {
            // Alpha 1, not 0: fully transparent pixels would let mouse input fall through to the desktop.
            using var clear = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
            g.FillRectangle(clear, _sel);
        }
        g.CompositingMode = CompositingMode.SourceOver;

        if (!_sel.IsEmpty)
        {
            using var pen = new Pen(Color.FromArgb(76, 141, 255), 2);
            using var white = new SolidBrush(Color.White);
            g.DrawRectangle(pen, _sel.X - 1, _sel.Y - 1, _sel.Width + 1, _sel.Height + 1);
            foreach (var h in HandleRects()) { g.FillRectangle(white, h); g.DrawRectangle(pen, h); }
            var label = $"{_sel.Width} × {_sel.Height}    at {_sel.X + _mon.X}, {_sel.Y + _mon.Y}";
            Box(g, label, new Point(_sel.X, _sel.Y >= 36 ? _sel.Y - 34 : _sel.Y + 8), 12f);
        }

        var hint = "Drag to select the area to share   ·   Enter = confirm   ·   Esc = cancel   ·   1–4 = size presets   ·   Arrows = nudge (Shift = 1 px)";
        var size = g.MeasureString(hint, HintFont);
        Box(g, hint, new Point((int)((Width - size.Width) / 2) - 12, 24), 12f);

        Push();
    }

    static readonly Font HintFont = new("Segoe UI", 12f);

    static void Box(Graphics g, string text, Point at, float fontSize)
    {
        using var font = new Font("Segoe UI", fontSize);
        var sz = g.MeasureString(text, font);
        var r = new Rectangle(at.X, at.Y, (int)sz.Width + 24, (int)sz.Height + 12);
        using var bg = new SolidBrush(Color.FromArgb(225, 20, 24, 31));
        using var fg = new SolidBrush(Color.White);
        using var path = Rounded(r, 6);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(bg, path);
        g.SmoothingMode = SmoothingMode.None;
        g.DrawString(text, font, fg, r.X + 12, r.Y + 6);
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void Push()
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = _bmp!.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { Cx = Width, Cy = Height };
            var src = new POINT();
            var dst = new POINT { X = Left, Y = Top };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
