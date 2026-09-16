using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace RegionShare;

// The two COM interfaces that bridge Windows.Graphics.Capture (WinRT) and Direct3D 11.

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, ref Guid iid);
    IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface(ref Guid iid);
}

static class CaptureInterop
{
    static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    /// Wraps our D3D11 device as the WinRT IDirect3DDevice the frame pool wants.
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device d3d)
    {
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        // Windows 11 22H2+ has a first-class API; older builds go through the COM interop factory.
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsMethodPresent("Windows.Graphics.Capture.GraphicsCaptureItem", "TryCreateFromDisplayId"))
            {
                var item = GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId(unchecked((ulong)hmon.ToInt64())));
                if (item != null) return item;
            }
        }
        catch (Exception e) { Log.Info("TryCreateFromDisplayId unavailable, using interop: " + e.Message); }

        return FromInteropFactory(f => { var iid = IID_GraphicsCaptureItem; return f.CreateForMonitor(hmon, ref iid); });
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsMethodPresent("Windows.Graphics.Capture.GraphicsCaptureItem", "TryCreateFromWindowId"))
            {
                var item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId(unchecked((ulong)hwnd.ToInt64())));
                if (item != null) return item;
            }
        }
        catch (Exception e) { Log.Info("TryCreateFromWindowId unavailable, using interop: " + e.Message); }

        return FromInteropFactory(f => { var iid = IID_GraphicsCaptureItem; return f.CreateForWindow(hwnd, ref iid); });
    }

    static GraphicsCaptureItem FromInteropFactory(Func<IGraphicsCaptureItemInterop, IntPtr> create)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hstr));
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstr, ref iid, out var factoryPtr));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var itemPtr = create(interop);
                try { return GraphicsCaptureItem.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(factoryPtr); }
        }
        finally { WindowsDeleteString(hstr); }
    }

    /// The D3D11 texture behind a captured frame. Caller disposes.
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = IID_ID3D11Texture2D;
        return new ID3D11Texture2D(access.GetInterface(ref iid));
    }
}
