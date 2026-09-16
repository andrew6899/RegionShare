namespace RegionShare;

/// Tiny append-only logger: %AppData%\RegionShare\log.txt
static class Log
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegionShare");
    static readonly string File = Path.Combine(Dir, "log.txt");
    static readonly object Gate = new();

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }

    public static void Error(Exception? e) => Info("ERROR " + e);
}
