using System.Text.Json;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class InstanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static List<InstanceInfo>? _cache;

    public static string InstancesRoot => Paths.Instances;

    public static List<InstanceInfo> GetAll()
    {
        if (_cache != null) return _cache;

        try
        {
            if (File.Exists(Paths.InstancesIndex))
            {
                _cache = JsonSerializer.Deserialize<List<InstanceInfo>>(
                             File.ReadAllText(Paths.InstancesIndex), JsonOptions) ?? new List<InstanceInfo>();
            }
        }
        catch (Exception ex)
        {
            LogService.WriteCrash("Instances.Load", ex.ToString());
            _cache = null;
        }

        if (_cache == null)
        {
            _cache = new List<InstanceInfo>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(Paths.Instances, "instance.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(file), JsonOptions);
                        if (info != null) _cache.Add(info);
                    }
                    catch { }
                }
            }
            catch { }
        }

        foreach (var instance in _cache)
        {
            if (string.IsNullOrWhiteSpace(instance.Directory))
                instance.Directory = Path.Combine(Paths.Instances, Paths.SafeName(instance.Name));
        }

        return _cache;
    }

    public static void SaveAll()
    {
        try
        {
            File.WriteAllText(Paths.InstancesIndex, JsonSerializer.Serialize(GetAll(), JsonOptions));
        }
        catch (Exception ex)
        {
            LogService.WriteCrash("Instances.Save", ex.ToString());
        }
    }

    public static void Save(InstanceInfo instance)
    {
        try
        {
            Directory.CreateDirectory(instance.Directory);
            File.WriteAllText(Path.Combine(instance.Directory, "instance.json"),
                JsonSerializer.Serialize(instance, JsonOptions));
        }
        catch { }
        SaveAll();
    }

    public static InstanceInfo? Find(string name) =>
        GetAll().FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static async Task<InstanceInfo> CreateAsync(string name, string mcVersion, string loader,
        string loaderVersion, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default,
        bool installLoader = true, string summary = "")
    {
        name = string.IsNullOrWhiteSpace(name) ? $"Сборка {mcVersion}" : name.Trim();

        var folderName = Paths.SafeName(name);
        var dir = Path.Combine(Paths.Instances, folderName);
        var counter = 2;
        while (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "instance.json")))
        {
            folderName = Paths.SafeName(name) + "-" + counter++;
            dir = Path.Combine(Paths.Instances, folderName);
        }

        Directory.CreateDirectory(dir);
        foreach (var sub in new[] { "mods", "config", "resourcepacks", "shaderpacks", "saves" })
            Directory.CreateDirectory(Path.Combine(dir, sub));

        var instance = new InstanceInfo
        {
            Name = name,
            McVersion = mcVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Directory = dir,
            CreatedAt = DateTime.Now,
            VersionId = ""
        };

        GetAll().Add(instance);
        Save(instance);

        if (installLoader)
            await InstallLoaderAsync(instance, progress, ct);

        return instance;
    }

    public static async Task InstallLoaderAsync(InstanceInfo instance, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        switch (instance.Loader)
        {
            case "fabric":
                instance.VersionId = await LoaderService.InstallFabricAsync(instance.McVersion,
                    instance.LoaderVersion, progress, ct);
                break;
            case "quilt":
                instance.VersionId = await LoaderService.InstallQuiltAsync(instance.McVersion,
                    instance.LoaderVersion, progress, ct);
                break;
            case "forge":
                instance.VersionId = await LoaderService.InstallForgeAsync(instance.McVersion,
                    instance.LoaderVersion, progress, ct);
                break;
            case "neoforge":
                instance.VersionId = await LoaderService.InstallNeoForgeAsync(instance.McVersion,
                    instance.LoaderVersion, progress, ct);
                break;
            default:
                instance.Loader = "vanilla";
                instance.VersionId = instance.McVersion;
                if (!VersionService.IsInstalled(instance.McVersion))
                    await VersionService.InstallAsync(instance.McVersion, progress, ct);
                break;
        }

        // Для Forge/NeoForge установщик мог создать id, отличный от запрошенного
        if (string.IsNullOrWhiteSpace(instance.VersionId) ||
            !File.Exists(VersionService.VersionJsonPath(instance.VersionId)))
        {
            var found = VersionService.GetInstalledVersions()
                .FirstOrDefault(v => v.Id.Contains(instance.McVersion) &&
                                     v.Id.Contains(instance.LoaderVersion, StringComparison.OrdinalIgnoreCase));
            if (found != null) instance.VersionId = found.Id;
        }

        Save(instance);
    }

    public static void Delete(InstanceInfo instance)
    {
        try
        {
            GetAll().Remove(instance);
            SaveAll();
            if (Directory.Exists(instance.Directory))
                Directory.Delete(instance.Directory, true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось удалить сборку: " + ex.Message);
        }
    }

    public static List<ModFileVm> GetMods(InstanceInfo instance)
    {
        var dir = Path.Combine(instance.Directory, "mods");
        if (!Directory.Exists(dir)) return new List<ModFileVm>();

        try
        {
            return Directory.EnumerateFiles(dir, "*.jar")
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.Name)
                .Select(f => new ModFileVm
                {
                    FileName = f.Name,
                    FullPath = f.FullName,
                    SizeText = f.Length >= 1024 * 1024
                        ? $"{f.Length / 1024.0 / 1024.0:0.#} МБ"
                        : $"{f.Length / 1024.0:0.#} КБ"
                })
                .ToList();
        }
        catch
        {
            return new List<ModFileVm>();
        }
    }

    public static void RemoveMod(ModFileVm mod)
    {
        try
        {
            if (File.Exists(mod.FullPath)) File.Delete(mod.FullPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось удалить мод: " + ex.Message);
        }
    }

    public static void Invalidate() => _cache = null;
}
