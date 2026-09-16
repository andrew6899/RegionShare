namespace RegionShare;

static class Program
{
    static Mutex? _instanceLock;

    [STAThread]
    static int Main(string[] args)
    {
        // Diagnostic mode: capture another window the way Teams does and save a PNG.
        //   RegionShare.exe --grab "Region Share" C:\temp\grab.png
        if (args.Length >= 3 && args[0] == "--grab")
            return GrabTool.Run(args[1], args[2]);

        // One instance only. The shared window is invisible by design, so re-launching the exe is the
        // natural thing to do when you can't find it — make that re-open the region picker instead of
        // starting a duplicate that steals nothing and confuses Teams' window list.
        _instanceLock = new Mutex(true, @"Local\RegionShare.SingleInstance.v1", out bool first);
        if (!first)
        {
            Log.Info("already running – asking the live instance to open the picker");
            Native.PostMessage(Native.HWND_BROADCAST, Native.WM_SHOW_PICKER, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error(e.ExceptionObject as Exception);
        Log.Info("---- start ----");
        Application.Run(new ViewerForm());
        GC.KeepAlive(_instanceLock);
        return 0;
    }
}
