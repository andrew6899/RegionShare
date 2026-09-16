using System.Diagnostics;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Vortice.Direct3D11;
using static RegionShare.Native;

namespace RegionShare;

/// The window Teams shares. It mirrors the selected region 1:1 but normally lives off-screen ("parked"):
/// Windows Graphics Capture reads a window's composited surface no matter where the window is, so Teams
/// sees the region while nothing on your monitor changes except the green border around it.
sealed class ViewerForm : Form
{
    const int HkSelect = 1, HkCenter = 2, HkSnap = 3, HkSnapSize = 4, HkFrame = 5, HkPreview = 6;
    const int HkNudge = 10;   // + 0..3 (20 px), + 4..7 (1 px): left, right, up, down
    const int HkLast = HkNudge + 8;
    static readonly int[] NudgeKeys = { VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN };
    static readonly Point[] NudgeDirs = { new(-1, 0), new(1, 0), new(0, -1), new(0, 1) };

    readonly Settings _s = Settings.Load();
    readonly FrameForm _frame = new();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem[] _fpsItems = new ToolStripMenuItem[3];
    ToolStripMenuItem _frameItem = null!, _previewItem = null!;
    Renderer? _renderer;
    MonitorCapture? _capture;
    Rectangle _region;        // screen coordinates; Empty = nothing selected
    Rectangle _monBounds;
    IntPtr _hmon;
    long _lastFrame;
    bool _preview;            // false = parked off-screen, true = shown on-screen so you can see what Teams sees

    bool Live => _capture != null && !_region.IsEmpty;

    public ViewerForm()
    {
        Text = "Region Share";
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        KeyPreview = true;
        Bounds = new Rectangle(ParkPoint(), new Size(640, 360));
        ContextMenuStrip = BuildMenu();

        _tray = new NotifyIcon { Icon = Icon, Text = "Region Share", ContextMenuStrip = ContextMenuStrip, Visible = true };
        _tray.DoubleClick += (_, _) => SelectRegion();
    }

    /// Just past the right edge of the desktop.
    static Point ParkPoint()
    {
        var vs = SystemInformation.VirtualScreen;
        return new Point(vs.Right + 256, vs.Top);
    }

    // ---------------- lifecycle ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { _renderer = new Renderer(Handle, ClientSize.Width, ClientSize.Height); }
        catch (Exception ex)
        {
            Log.Error(ex);
            MessageBox.Show("Could not initialise Direct3D:\n" + ex.Message, "Region Share", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RegisterHotkeys();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!MonitorCapture.IsSupported)
        {
            MessageBox.Show("Windows Graphics Capture is not available on this machine (needs Windows 10 1903 or later).",
                            "Region Share", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        await RequestBorderlessAsync();
        if (_s.RegionW > 0 && _s.RegionH > 0)
        {
            ApplyRegion(_s.Region);
        }
        else
        {
            _tray.ShowBalloonTip(8000, "Region Share",
                "Running in the tray. Press Ctrl+Alt+R at any time to choose the part of the screen to share.", ToolTipIcon.Info);
            SelectRegion();
        }
    }

    /// Asks Windows not to draw the yellow "being captured" border around the monitor. Best effort.
    static async Task RequestBorderlessAsync()
    {
        try
        {
            if (ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
            {
                var r = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                Log.Info("borderless access: " + r);
            }
        }
        catch (Exception e) { Log.Info("borderless access request failed: " + e.Message); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        for (int id = 1; id < HkLast; id++) UnregisterHotKey(Handle, id);
        _capture?.Dispose(); _capture = null;
        _renderer?.Dispose(); _renderer = null;
        _frame.Close();
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized && ClientSize.Width > 0 && ClientSize.Height > 0)
            _renderer?.Resize(ClientSize.Width, ClientSize.Height);
        UpdateTitle();
    }

    protected override void OnMove(EventArgs e) { base.OnMove(e); UpdateTitle(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _preview) { SetPreview(false); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // While live the swap chain owns the client area; don't let GDI paint over it.
    protected override void OnPaintBackground(PaintEventArgs e) { if (!Live) base.OnPaintBackground(e); }
    protected override void OnPaint(PaintEventArgs e) { if (!Live) base.OnPaint(e); }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_HOTKEY:
                OnHotkey((int)m.WParam);
                return;
            case WM_NCHITTEST when _preview:
                // Borderless preview: drag it by grabbing anywhere.
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT) m.Result = HTCAPTION;
                return;
            case WM_NCRBUTTONUP when _preview:
                ContextMenuStrip!.Show(CursorPos());
                return;
        }
        base.WndProc(ref m);
    }

    // ---------------- region ----------------

    void ApplyRegion(Rectangle r)
    {
        r = ClampToMonitor(r, out var hmon, out var mb);
        if (r.Width < 16 || r.Height < 16 || _renderer == null) return;

        bool restart = _capture == null || _hmon != hmon;
        _region = r; _hmon = hmon; _monBounds = mb;
        try
        {
            _renderer.SetRegionSize(r.Width, r.Height);
            if (restart)
            {
                _capture?.Dispose();
                _capture = new MonitorCapture(_renderer.Device, hmon, _s.CaptureCursor);
                _capture.FrameArrived += OnFrame;
            }
        }
        catch (Exception e)
        {
            Log.Error(e);
            _region = Rectangle.Empty;
            MessageBox.Show("Screen capture failed to start:\n" + e.Message, "Region Share", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _frame.SetRegion(r);
        _frame.Visible = _s.ShowFrame;
        if (_preview) FitPreview();
        else Bounds = new Rectangle(ParkPoint(), r.Size);   // parked: always 1:1 with the region
        _s.Region = r;
        _s.Save();
        UpdateTitle();
        Log.Info($"region {r}");
    }

    void OnFrame(ID3D11Texture2D tex)
    {
        if (_s.MaxFps > 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _lastFrame < Stopwatch.Frequency / _s.MaxFps) return;
            _lastFrame = now;
        }
        var region = _region; var mon = _monBounds;
        _renderer?.Render(tex, region.X - mon.X, region.Y - mon.Y);
    }

    static Rectangle ClampToMonitor(Rectangle r, out IntPtr hmon, out Rectangle mb)
    {
        hmon = MonitorNear(r);
        mb = MonitorBounds(hmon);
        r.Width = Math.Min(r.Width, mb.Width);
        r.Height = Math.Min(r.Height, mb.Height);
        r.X = Math.Clamp(r.X, mb.Left, mb.Right - r.Width);
        r.Y = Math.Clamp(r.Y, mb.Top, mb.Bottom - r.Height);
        return r;
    }

    void SelectRegion()
    {
        var hmon = MonitorAt(CursorPos());
        var mb = MonitorBounds(hmon);
        _frame.Visible = false;
        using var picker = new RegionPickerForm(mb, !_region.IsEmpty && mb.Contains(_region.Location) ? _region : null);
        picker.ShowDialog();
        if (picker.Result is Rectangle r) ApplyRegion(r);
        else if (!_region.IsEmpty) _frame.Visible = _s.ShowFrame;
    }

    void CenterOnMouse()
    {
        var p = CursorPos();
        var size = _region.IsEmpty ? RegionPickerForm.Presets[0] : _region.Size;
        ApplyRegion(new Rectangle(p.X - size.Width / 2, p.Y - size.Height / 2, size.Width, size.Height));
    }

    void SnapToWindow(bool matchSize)
    {
        var p = CursorPos();
        var hwnd = GetAncestor(WindowFromPoint(new POINT { X = p.X, Y = p.Y }), GA_ROOT);
        if (hwnd == IntPtr.Zero || hwnd == Handle || hwnd == _frame.Handle) return;
        var wb = WindowBounds(hwnd);
        var size = matchSize || _region.IsEmpty ? wb.Size : _region.Size;
        ApplyRegion(new Rectangle(wb.Location, size));
    }

    void SetRegionSize(Size size)
    {
        if (_region.IsEmpty) { var p = CursorPos(); ApplyRegion(new Rectangle(p.X - size.Width / 2, p.Y - size.Height / 2, size.Width, size.Height)); }
        else ApplyRegion(new Rectangle(_region.Location, size));
    }

    void Nudge(int dx, int dy)
    {
        if (_region.IsEmpty) return;
        var r = _region; r.Offset(dx, dy);
        ApplyRegion(r);
    }

    void ToggleFrame() => _frameItem.Checked = !_frameItem.Checked;

    // ---------------- parked / preview ----------------

    void SetPreview(bool on)
    {
        if (on && _region.IsEmpty) { _previewItem.Checked = false; return; }
        _preview = on;
        if (on) { FitPreview(); Activate(); }
        else Bounds = new Rectangle(ParkPoint(), _region.IsEmpty ? ClientSize : _region.Size);
        if (_previewItem.Checked != on) _previewItem.Checked = on;
        UpdateTitle();
    }

    /// Puts the preview beside the region at 1:1, or shrunk into the wider free band if 1:1 won't fit.
    void FitPreview()
    {
        const int gap = 12;
        var wa = Screen.FromRectangle(_region).WorkingArea;
        var size = _region.Size;
        Rectangle? spot = null;
        foreach (var p in new[] { new Point(_region.Right + gap, _region.Top), new Point(_region.Left - gap - size.Width, _region.Top),
                                  new Point(_region.Left, _region.Bottom + gap), new Point(_region.Left, _region.Top - gap - size.Height) })
        {
            var c = new Rectangle(p, size);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(c))) { spot = c; break; }
        }
        if (spot == null)
        {
            int right = wa.Right - _region.Right - gap, left = _region.Left - wa.Left - gap;
            int band = Math.Max(right, left);
            if (band >= 320)
            {
                float s = Math.Min(band / (float)_region.Width, wa.Height / (float)_region.Height);
                var sz = new Size((int)(_region.Width * s), (int)(_region.Height * s));
                spot = new Rectangle(right >= left ? _region.Right + gap : _region.Left - gap - sz.Width, _region.Top, sz.Width, sz.Height);
            }
        }
        var b = spot ?? new Rectangle(wa.X + (wa.Width - size.Width) / 2, wa.Y + (wa.Height - size.Height) / 2, size.Width, size.Height);
        b.X = Math.Clamp(b.X, wa.Left, Math.Max(wa.Left, wa.Right - b.Width));
        b.Y = Math.Clamp(b.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - b.Height));
        Bounds = b;
    }

    void UpdateTitle()
    {
        string t = _region.IsEmpty ? "Region Share" : $"Region Share — {_region.Width}×{_region.Height}";
        if (_preview && !_region.IsEmpty && IsHandleCreated && WindowBounds(Handle).IntersectsWith(_region))
            t = "⚠ Preview overlaps the shared region  —  " + t;
        if (Text != t) Text = t;
        if (_tray != null) _tray.Text = t.Length > 63 ? t[..63] : t;
    }

    // ---------------- hotkeys ----------------

    void RegisterHotkeys()
    {
        uint ca = MOD_CONTROL | MOD_ALT, cas = ca | MOD_SHIFT;
        Reg(HkSelect, ca | MOD_NOREPEAT, 'R');
        Reg(HkCenter, ca | MOD_NOREPEAT, 'M');
        Reg(HkSnap, ca | MOD_NOREPEAT, 'W');
        Reg(HkSnapSize, cas | MOD_NOREPEAT, 'W');
        Reg(HkFrame, ca | MOD_NOREPEAT, 'B');
        Reg(HkPreview, ca | MOD_NOREPEAT, 'P');
        for (int i = 0; i < 4; i++)
        {
            Reg(HkNudge + i, ca, NudgeKeys[i]);
            Reg(HkNudge + 4 + i, cas, NudgeKeys[i]);
        }
    }

    void Reg(int id, uint mods, int vk)
    {
        if (!RegisterHotKey(Handle, id, mods, (uint)vk)) Log.Info($"hotkey {id} (vk 0x{vk:X}) already taken by another app");
    }

    void OnHotkey(int id)
    {
        switch (id)
        {
            case HkSelect: SelectRegion(); break;
            case HkCenter: CenterOnMouse(); break;
            case HkSnap: SnapToWindow(false); break;
            case HkSnapSize: SnapToWindow(true); break;
            case HkFrame: ToggleFrame(); break;
            case HkPreview: SetPreview(!_preview); break;
            case >= HkNudge and < HkLast:
                int i = id - HkNudge, step = i < 4 ? 20 : 1;
                var d = NudgeDirs[i % 4];
                Nudge(d.X * step, d.Y * step);
                break;
        }
    }

    // ---------------- menu ----------------

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        ToolStripMenuItem Add(string text, Action onClick, string? keys = null)
        {
            var mi = new ToolStripMenuItem(text, null, (_, _) => onClick()) { ShortcutKeyDisplayString = keys };
            m.Items.Add(mi);
            return mi;
        }
        ToolStripMenuItem Check(string text, bool initial, Action<bool> onToggle, string? keys = null)
        {
            var mi = new ToolStripMenuItem(text) { Checked = initial, CheckOnClick = true, ShortcutKeyDisplayString = keys };
            mi.CheckedChanged += (_, _) => onToggle(mi.Checked);
            m.Items.Add(mi);
            return mi;
        }

        Add("Select region…", SelectRegion, "Ctrl+Alt+R");
        Add("Move region to mouse", CenterOnMouse, "Ctrl+Alt+M");
        Add("Snap region to window under mouse", () => SnapToWindow(false), "Ctrl+Alt+W");
        Add("Match window under mouse (size too)", () => SnapToWindow(true), "Ctrl+Alt+Shift+W");
        var sizes = new ToolStripMenuItem("Region size");
        foreach (var p in RegionPickerForm.Presets)
        {
            var s = p;
            sizes.DropDownItems.Add($"{s.Width} × {s.Height}", null, (_, _) => SetRegionSize(s));
        }
        m.Items.Add(sizes);
        m.Items.Add(new ToolStripSeparator());

        _previewItem = Check("Preview what Teams sees", false, on => { if (_preview != on) SetPreview(on); }, "Ctrl+Alt+P");
        _frameItem = Check("Show region border", _s.ShowFrame, on => { _s.ShowFrame = on; _frame.Visible = on && !_region.IsEmpty; _s.Save(); }, "Ctrl+Alt+B");
        Check("Capture mouse cursor", _s.CaptureCursor, on => { _s.CaptureCursor = on; _capture?.SetCursor(on); _s.Save(); });

        var fps = new ToolStripMenuItem("Max frame rate");
        int[] rates = { 30, 60, 0 };
        for (int i = 0; i < rates.Length; i++)
        {
            int r = rates[i];
            var mi = new ToolStripMenuItem(r == 0 ? "Unlimited" : $"{r} fps") { Checked = _s.MaxFps == r };
            mi.Click += (_, _) => { _s.MaxFps = r; foreach (var x in _fpsItems) x.Checked = false; mi.Checked = true; _s.Save(); };
            _fpsItems[i] = mi;
            fps.DropDownItems.Add(mi);
        }
        m.Items.Add(fps);
        m.Items.Add(new ToolStripSeparator());

        Add("Hotkeys…", ShowHotkeys);
        Add("Exit", Close);
        return m;
    }

    static void ShowHotkeys() => MessageBox.Show(
        "Global hotkeys (work from any app):\n\n" +
        "Ctrl+Alt+R\t\tSelect / adjust region\n" +
        "Ctrl+Alt+M\t\tMove region to the mouse\n" +
        "Ctrl+Alt+W\t\tSnap region to the window under the mouse\n" +
        "Ctrl+Alt+Shift+W\tMatch that window's size too\n" +
        "Ctrl+Alt+Arrows\t\tNudge region 20 px (add Shift for 1 px)\n" +
        "Ctrl+Alt+B\t\tShow / hide the region border\n" +
        "Ctrl+Alt+P\t\tPreview what Teams sees (Esc closes it)\n\n" +
        "In the region picker: Enter confirms, Esc cancels, 1–4 apply size presets.",
        "Region Share – hotkeys", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
