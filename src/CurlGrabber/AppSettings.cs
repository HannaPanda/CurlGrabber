using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurlGrabber;

public sealed class AppSettings
{
    public string LastFolder { get; set; } = string.Empty;
    public bool Resume { get; set; } = true;
    public bool Retry { get; set; } = true;
    public bool FailOnHttpError { get; set; } = true;
    public bool TrimJunkPrefix { get; set; } = true;
    public bool Segmented { get; set; } = true;
    public bool RemuxToMp4 { get; set; } = true;
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool Maximized { get; set; }

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CurlGrabber",
        "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Kaputte oder unlesbare Datei: mit Standardwerten weitermachen.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Einstellungen sind nicht wichtig genug, um deswegen einen Fehler zu zeigen.
        }
    }

    /// <summary>Startordner: zuletzt benutzter, sonst E:\Movies, sonst das Videos-Verzeichnis.</summary>
    public string ResolveStartFolder()
    {
        if (!string.IsNullOrWhiteSpace(LastFolder) && Directory.Exists(LastFolder))
        {
            return LastFolder;
        }

        const string preferred = @"E:\Movies";
        if (Directory.Exists(preferred))
        {
            return preferred;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }
}
