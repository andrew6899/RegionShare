using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2D1 = Vortice.Direct2D1.D2D1;
using D3D11 = Vortice.Direct3D11.D3D11;

namespace RegionShare;

/// Owns the D3D11 device, the swap chain on the viewer window, and the crop → scale → present step.
/// Render() runs on the capture thread; Resize()/SetRegionSize() on the UI thread. One lock covers all of it.
sealed class Renderer : IDisposable
{
    static readonly Vortice.DCommon.PixelFormat Bgra = new(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore);

    public ID3D11Device Device { get; }
    readonly ID3D11DeviceContext _ctx;
    readonly IDXGISwapChain1 _swap;
    readonly ID2D1Factory1 _d2dFactory;
    readonly ID2D1Device _d2dDevice;
    readonly ID2D1DeviceContext _d2d;
    ID2D1Bitmap1? _target;
    ID3D11Texture2D? _regionTex;
    ID2D1Bitmap1? _regionBmp;
    readonly object _gate = new();
    int _w, _h;

    public Renderer(IntPtr hwnd, int width, int height)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0 },
            out ID3D11Device? device, out ID3D11DeviceContext? ctx).CheckError();
        Device = device!;
        _ctx = ctx!;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        _w = Math.Max(1, width);
        _h = Math.Max(1, height);
        var desc = new SwapChainDescription1
        {
            Width = (uint)_w,
            Height = (uint)_h,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 3,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        _swap = factory.CreateSwapChainForHwnd(Device, hwnd, desc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        // 2, not 1: a single-frame latency limit makes Present block on the previous frame often enough
        // to stall the capture thread. One extra frame of latency is invisible next to Teams' own encode.
        using (var dxgiDevice1 = Device.QueryInterface<IDXGIDevice1>()) dxgiDevice1.MaximumFrameLatency = 2;

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        CreateTarget();
    }

    void CreateTarget()
    {
        using var surface = _swap.GetBuffer<IDXGISurface>(0);
        _target = _d2d.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(Bgra, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        _d2d.Target = _target;
    }

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1) return;
        lock (_gate)
        {
            if (width == _w && height == _h) return;
            _w = width; _h = height;
            _d2d.Target = null;
            _target?.Dispose();
            _target = null;
            _swap.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
            CreateTarget();
        }
    }

    /// Allocates the intermediate texture the region is cropped into.
    public void SetRegionSize(int width, int height)
    {
        lock (_gate)
        {
            _regionBmp?.Dispose(); _regionBmp = null;
            _regionTex?.Dispose(); _regionTex = null;
            if (width < 1 || height < 1) return;
            _regionTex = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
            using var surface = _regionTex.QueryInterface<IDXGISurface>();
            _regionBmp = _d2d.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(Bgra, 96, 96, BitmapOptions.None));
        }
    }

    /// Crops (rx, ry, regionW, regionH) out of the monitor frame and presents it, letterboxed, in the window.
    /// Returns false if the frame could not be presented (the swap chain was still busy with the previous one).
    public bool Render(ID3D11Texture2D frame, int rx, int ry)
    {
        lock (_gate)
        {
            if (_target == null || _regionTex == null || _regionBmp == null) return false;

            var fd = frame.Description;
            var rd = _regionTex.Description;
            int x0 = Math.Clamp(rx, 0, (int)fd.Width), y0 = Math.Clamp(ry, 0, (int)fd.Height);
            int x1 = Math.Clamp(rx + (int)rd.Width, 0, (int)fd.Width), y1 = Math.Clamp(ry + (int)rd.Height, 0, (int)fd.Height);
            if (x1 <= x0 || y1 <= y0) return false;
            _ctx.CopySubresourceRegion(_regionTex, 0, 0, 0, 0, frame, 0, new Box(x0, y0, 0, x1, y1, 1));

            float rw = rd.Width, rh = rd.Height;
            float scale = Math.Min(_w / rw, _h / rh);
            float dw = rw * scale, dh = rh * scale;
            var dest = new Rect((_w - dw) / 2, (_h - dh) / 2, dw, dh);
            var mode = Math.Abs(scale - 1f) < 0.001f ? Vortice.Direct2D1.InterpolationMode.NearestNeighbor : Vortice.Direct2D1.InterpolationMode.HighQualityCubic;

            _d2d.BeginDraw();
            _d2d.Clear(new Color4(0, 0, 0, 1));
            _d2d.DrawBitmap(_regionBmp, dest, 1f, mode, null, null);
            _d2d.EndDraw();

            // No vsync wait — the capture already arrives at the compositor's cadence, so waiting on vblank
            // here would only add latency. Don't pass DoNotWait: it makes DXGI discard the frame outright
            // whenever the queue is momentarily busy, which reads as stutter in the shared stream.
            var hr = _swap.Present(0, PresentFlags.None);
            if (hr.Code == unchecked((int)0x887A000A) /* DXGI_ERROR_WAS_STILL_DRAWING */) return false;
            if (hr.Failure) Log.Info("Present: " + hr);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _regionBmp?.Dispose();
            _regionTex?.Dispose();
            _d2d.Target = null;
            _target?.Dispose();
            _d2d.Dispose();
            _d2dDevice.Dispose();
            _d2dFactory.Dispose();
            _swap.Dispose();
            _ctx.Dispose();
            Device.Dispose();
        }
    }
}
