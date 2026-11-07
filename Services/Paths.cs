namespace VibeLauncher.Services;

/// <summary>Все пути лаунчера. Данные хранятся в %APPDATA%\VibeLauncher.</summary>
public static class Paths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeLauncher");

    public static string Versions => Path.Combine(Root, "versions");
    public static string Libraries => Path.Combine(Root, "libraries");
    public static string Assets => Path.Combine(Root, "assets");
    public static string Instances => Path.Combine(Root, "instances");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Java => Path.Combine(Root, "java");
    public static string Skins => Path.Combine(Root, "skins");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string InstancesIndex => Path.Combine(Instances, "instances.json");

    static Paths()
    {
        foreach (var dir in new[] { Root, Versions, Libraries, Assets, Instances, Logs, Cache, Java, Skins })
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "instance" : result;
    }
}
