namespace RegionShare;

/// Tiny append-only logger: %AppData%\RegionShare\log.txt
static class Log
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegionShare");
    static readonly string File = Path.Combine(Dir, "log.txt");
    static readonly object Gate = new();

    const long MaxBytes = 1_000_000;

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                // Frame stats land here every 10 s while sharing, so keep the file from growing forever.
                var fi = new FileInfo(File);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var keep = System.IO.File.ReadLines(File).Skip(200).ToArray();
                    System.IO.File.WriteAllLines(File, keep);
                }
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }

    public static void Error(Exception? e) => Info("ERROR " + e);
}
