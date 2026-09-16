using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11 = Vortice.Direct3D11.D3D11;

namespace RegionShare;

/// Diagnostic: `RegionShare.exe --grab "<window title prefix>" out.png`
/// Captures a window the same way Teams does (Windows.Graphics.Capture window capture), writes a PNG and a
/// .txt report next to it. Use it to prove the parked Region Share window is capturable on a given machine.
static class GrabTool
{
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    public static int Run(string titlePrefix, string outPath, int wantFrames = 4, int timeoutMs = 4000)
    {
        var report = new StringBuilder();
        try
        {
            IntPtr hwnd = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (!sb.ToString().StartsWith(titlePrefix, StringComparison.Ordinal)) return true;
                hwnd = h;
                return false;
            }, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { report.AppendLine($"no visible window titled '{titlePrefix}*'"); return 2; }
            report.AppendLine($"hwnd 0x{hwnd:X}  bounds {Native.WindowBounds(hwnd)}");

            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
            using var d3d = device!;
            using var ctx = context!;
            using var winrt = CaptureInterop.CreateWinRtDevice(d3d);
            var item = CaptureInterop.CreateItemForWindow(hwnd);
            report.AppendLine($"item '{item.DisplayName}' {item.Size.Width}x{item.Size.Height}");

            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrt, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            using var session = pool.CreateCaptureSession(item);
            var gate = new object();
            var done = new ManualResetEventSlim();
            var sw = Stopwatch.StartNew();
            var arrivals = new List<long>();
            ID3D11Texture2D? staging = null;

            pool.FrameArrived += (s, _) =>
            {
                using var frame = s.TryGetNextFrame();
                if (frame == null) return;
                lock (gate)
                {
                    arrivals.Add(sw.ElapsedMilliseconds);
                    using var tex = CaptureInterop.GetTexture(frame.Surface);
                    var d = tex.Description;
                    staging ??= d3d.CreateTexture2D(new Texture2DDescription
                    {
                        Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                    });
                    ctx.CopyResource(staging, tex);   // keep the latest frame
                    if (arrivals.Count >= wantFrames) done.Set();
                }
            };
            session.StartCapture();
            done.Wait(timeoutMs);
            session.Dispose();

            lock (gate)
            {
                report.AppendLine($"frames arrived: {arrivals.Count} within {timeoutMs} ms  (at ms: {string.Join(", ", arrivals)})");
                if (staging == null) { report.AppendLine("RESULT: no frames – window is not capturable"); return 3; }
                report.AppendLine(arrivals.Count >= wantFrames
                    ? "RESULT: window is live and updating"
                    : "RESULT: window captured but static (content not updating, or nothing changed on screen)");
                SavePng(ctx, staging, outPath);
                staging.Dispose();
            }
            report.AppendLine("saved " + outPath);
            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine("ERROR " + e);
            return 1;
        }
        finally
        {
            File.WriteAllText(outPath + ".txt", report.ToString());
        }
    }

    static unsafe void SavePng(ID3D11DeviceContext ctx, ID3D11Texture2D staging, string path)
    {
        var d = staging.Description;
        var map = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            using var bmp = new Bitmap((int)d.Width, (int)d.Height, PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = bmp.Width * 4;
                for (int y = 0; y < bmp.Height; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, (byte*)bits.Scan0 + y * bits.Stride, rowBytes, rowBytes);
            }
            finally { bmp.UnlockBits(bits); }
            bmp.Save(path, ImageFormat.Png);
        }
        finally { ctx.Unmap(staging, 0); }
    }
}
