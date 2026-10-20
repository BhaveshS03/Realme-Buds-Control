using System.Text.Json;

namespace RealmeBuds;

/// <summary>Remembers the last connection choices in %LOCALAPPDATA%\RealmeBuds\settings.json.</summary>
public sealed class AppSettings
{
    public ulong DeviceAddress { get; set; }
    public string Connection { get; set; } = "";
    public int Channel { get; set; }
    public int LenMode { get; set; }
    public bool EnableLog { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RealmeBuds", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* settings are a convenience only */ }
    }
}
