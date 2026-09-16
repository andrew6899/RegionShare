using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;

namespace RegionShare;

/// Streams one monitor through Windows.Graphics.Capture. Frames arrive on a worker thread as GPU textures.
sealed class MonitorCapture : IDisposable
{
    const string SessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";

    readonly IDirect3DDevice _device;
    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    SizeInt32 _size;
    volatile bool _disposed;

    public IntPtr Monitor { get; }

    /// Raised on the capture thread. The texture is only valid for the duration of the call.
    public event Action<ID3D11Texture2D>? FrameArrived;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public MonitorCapture(ID3D11Device d3d, IntPtr hmon, bool captureCursor)
    {
        Monitor = hmon;
        _device = CaptureInterop.CreateWinRtDevice(d3d);
        _item = CaptureInterop.CreateItemForMonitor(hmon);
        _size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(_item);
        SetCursor(captureCursor);
        if (ApiInformation.IsPropertyPresent(SessionType, "IsBorderRequired"))
        {
            try { _session.IsBorderRequired = false; }
            catch (Exception e) { Log.Info("IsBorderRequired=false refused: " + e.Message); }
        }
        _session.StartCapture();
        Log.Info($"capture started on monitor {hmon} {_size.Width}x{_size.Height}");
    }

    public void SetCursor(bool on)
    {
        if (!ApiInformation.IsPropertyPresent(SessionType, "IsCursorCaptureEnabled")) return;
        try { _session.IsCursorCaptureEnabled = on; }
        catch (Exception e) { Log.Info("IsCursorCaptureEnabled: " + e.Message); }
    }

    void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        SizeInt32 content;
        using (var frame = sender.TryGetNextFrame())
        {
            if (frame == null) return;
            content = frame.ContentSize;
            if (content.Width == _size.Width && content.Height == _size.Height)
            {
                try
                {
                    using var tex = CaptureInterop.GetTexture(frame.Surface);
                    FrameArrived?.Invoke(tex);
                }
                catch (Exception e) { Log.Error(e); }
                return;
            }
        }
        // Monitor resolution changed: rebuild the pool at the new size (the stale frame has been released above).
        _size = content;
        sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
        Log.Info($"capture resized to {content.Width}x{content.Height}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.FrameArrived -= OnFrame;
        _session.Dispose();
        _pool.Dispose();
        _device.Dispose();
    }
}
