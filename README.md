# Region Share

Share *part* of a screen in Microsoft Teams (or anything else that can share a window) — like Zoom's
"share a portion of screen", with nothing extra on your monitor.

You draw a rectangle. A thin green border marks it. In Teams you share the window called **Region Share**.
Viewers see exactly what's inside the rectangle: Visual Studio, a browser, a Remote Desktop session —
whatever you put there, used exactly as you normally would. The rest of your monitor is private:
notes, chat, whatever, and none of it is shared.

## How it works (and why there's no visible window)

Windows can't share "what's behind a transparent window" — window capture only ever sees a window's own
pixels. So Region Share does keep a window that mirrors the region on the GPU, but it **parks that window
off-screen**, past the right edge of the desktop. Windows Graphics Capture (what Teams uses) reads a window's
composited surface regardless of where it is, so Teams captures it fine while you never see it.

Verified on a 5120×1440 setup: the parked window is capturable, updates live, and shows up in a Chromium
share picker with a proper thumbnail. See `--grab` below to check the same thing on another machine.

## Using it

Run `RegionShare.exe`. It lives in the tray (green-square icon). First run opens the region picker.

| Action | How |
|---|---|
| Select / adjust the region | **Ctrl+Alt+R** (or double-click the tray icon) — drag a rectangle, Enter to confirm |
| Size presets in the picker | **1** = 1920×1080, **2** = 1600×900, **3** = 1280×720, **4** = 2560×1440 |
| Move the region to the mouse | **Ctrl+Alt+M** |
| Snap the region to the window under the mouse | **Ctrl+Alt+W** (keeps size) · **Ctrl+Alt+Shift+W** (matches the window's size too) |
| Nudge the region | **Ctrl+Alt+Arrows** = 20 px · add **Shift** = 1 px |
| Show / hide the green border | **Ctrl+Alt+B** |
| Peek at what Teams sees | **Ctrl+Alt+P** brings the mirror on-screen next to the region; Esc or the same key parks it again |
| Everything else | Right-click the tray icon |

Hotkeys are global. If one is already taken by another program it's skipped (noted in the log) and the
tray menu still does everything.

**In Teams:** Share → *Window* → **Region Share**.

Things to know:

- The region can't span two monitors. Ctrl+Alt+R opens the picker on the monitor the mouse is on.
- Don't minimize the Region Share window (it's in the taskbar / Alt-Tab because Teams needs it to be a real
  window). Minimized windows stop rendering and Teams would freeze. Closing it exits the app.
- The green border is drawn just *outside* the region and is excluded from capture, so it never appears in
  the stream. Ctrl+Alt+B hides it if it bothers you.
- Teams' "give control" works on window coordinates, so it won't behave sensibly with a parked window.
- Settings and a log live in `%AppData%\RegionShare\`.

## Checking capture on a new machine

```
RegionShare.exe --grab "Region Share" C:\temp\grab.png
```

While Region Share is running, this captures its (parked) window the same way Teams does and writes
`grab.png` plus `grab.png.txt` with a verdict: *live and updating*, *static*, or *not capturable*.

## Building

Requires the .NET 8 SDK (or newer — .NET 9/10 SDKs build this fine) and Windows 10 2004+.
Windows 11 22H2+ additionally lets it suppress the yellow "screen is being captured" outline.

```
dotnet build -c Release
```

Self-contained single exe (nothing to install on the target machine):

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Output: `publish\RegionShare.exe` (~75 MB — it carries the .NET runtime).

## Code map

| File | Role |
|---|---|
| `ViewerForm.cs` | The window Teams shares: parking/preview, hotkeys, tray menu, region logic |
| `RegionPickerForm.cs` | Drag-to-select overlay (per-pixel-alpha layered window) |
| `FrameForm.cs` | Green border, click-through, excluded from capture |
| `MonitorCapture.cs` | Windows.Graphics.Capture session for a monitor |
| `Renderer.cs` | D3D11 device, swap chain, GPU crop (`CopySubresourceRegion`) and Direct2D present |
| `CaptureInterop.cs` | COM glue between WinRT capture and D3D11 |
| `GrabTool.cs` | The `--grab` diagnostic |
| `Native.cs`, `AppIcon.cs`, `Settings.cs`, `Log.cs` | Win32 declarations, runtime-drawn icon, `%AppData%\RegionShare\` |
