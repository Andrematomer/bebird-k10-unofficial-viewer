using System.IO;
using System.Text.Json;

namespace BebirdScope;

internal sealed class Settings
{
    public bool AutoLevel { get; set; } = true;
    public bool Mirror { get; set; } = false;
    public bool Circle { get; set; } = true;
    /// <summary>Flip the rotation direction (in case the sensor axis is mirrored).</summary>
    public bool Invert { get; set; } = false;
    /// <summary>Where photos and videos are saved. Empty = Pictures\K10 Captures.</summary>
    public string SaveFolder { get; set; } = null;
    /// <summary>The slow-picture (USB 2) warning stays quiet until this moment.</summary>
    public DateTime? Usb2WarnSnoozeUntil { get; set; } = null;
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 820;

    public string EffectiveSaveFolder() =>
        string.IsNullOrWhiteSpace(SaveFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "K10 Captures")
            : SaveFolder;

    static string FilePath => Path.Combine(Log.Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
    }
}
