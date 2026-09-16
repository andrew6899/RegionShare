using System.Text.Json;
using System.Text.Json.Serialization;

namespace RegionShare;

/// Persisted in %AppData%\RegionShare\settings.json
sealed class Settings
{
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionW { get; set; }
    public int RegionH { get; set; }

    public bool ShowFrame { get; set; } = true;
    public bool CaptureCursor { get; set; } = true;
    /// 0 = unlimited
    public int MaxFps { get; set; } = 60;

    [JsonIgnore]
    public Rectangle Region
    {
        get => new(RegionX, RegionY, RegionW, RegionH);
        set { RegionX = value.X; RegionY = value.Y; RegionW = value.Width; RegionH = value.Height; }
    }

    static readonly string File = Path.Combine(Log.Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File), Json) ?? new Settings();
        }
        catch (Exception e) { Log.Error(e); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) { Log.Error(e); }
    }
}
