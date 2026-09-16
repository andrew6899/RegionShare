namespace RegionShare;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Diagnostic mode: capture another window the way Teams does and save a PNG.
        //   RegionShare.exe --grab "Region Share" C:\temp\grab.png
        if (args.Length >= 3 && args[0] == "--grab")
            return GrabTool.Run(args[1], args[2]);

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error(e.ExceptionObject as Exception);
        Log.Info("---- start ----");
        Application.Run(new ViewerForm());
        return 0;
    }
}
