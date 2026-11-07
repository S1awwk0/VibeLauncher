using System.Text.Json;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class ModrinthService
{
    private const string Api = "https://api.modrinth.com/v2";

    public static async Task<ModrinthSearchResult> SearchAsync(string query, string? mcVersion, string? loader,
        int offset = 0, int limit = 20, string projectType = "mod", CancellationToken ct = default)
    {
        var facets = new List<List<string>> { new() { "project_type:" + projectType } };
        if (!string.IsNullOrWhiteSpace(mcVersion)) facets.Add(new List<string> { "versions:" + mcVersion });
        if (!string.IsNullOrWhiteSpace(loader) && loader != "vanilla")
            facets.Add(new List<string> { "categories:" + loader });

        var url = $"{Api}/search?query={Uri.EscapeDataString(query ?? "")}" +
                  $"&limit={limit}&offset={offset}&index={(string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance")}" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";

        return await HttpService.GetJsonAsync<ModrinthSearchResult>(url, ct);
    }

    public static async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, string? mcVersion,
        string? loader, CancellationToken ct = default)
    {
        var url = $"{Api}/project/{Uri.EscapeDataString(projectId)}/version";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(mcVersion))
            query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mcVersion })));
        if (!string.IsNullOrWhiteSpace(loader) && loader != "vanilla")
            query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })));
        if (query.Count > 0) url += "?" + string.Join("&", query);

        return await HttpService.GetJsonAsync<List<ModrinthVersion>>(url, ct);
    }

    public static async Task<string> InstallModAsync(InstanceInfo instance, string projectId,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (instance.Loader == "vanilla")
            throw new InvalidOperationException("Моды можно устанавливать только в сборки с Fabric, Quilt, Forge или NeoForge.");

        progress?.Report("Поиск совместимой версии мода…");
        var versions = await GetVersionsAsync(projectId, instance.McVersion, instance.Loader, ct);
        if (versions.Count == 0)
            throw new InvalidOperationException(
                $"У мода нет версии для Minecraft {instance.McVersion} / {instance.LoaderLabel}.");

        var version = versions.FirstOrDefault(v => v.Files.Any(f => f.Primary))
                      ?? versions.First(v => v.Files.Count > 0);
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files[0];

        var modsDir = Path.Combine(instance.Directory, "mods");
        Directory.CreateDirectory(modsDir);
        var fileName = Paths.SafeName(Path.GetFileNameWithoutExtension(file.Filename)) +
                       Path.GetExtension(file.Filename);
        var dest = Path.Combine(modsDir, fileName);

        progress?.Report($"Скачивание {fileName}…");
        await HttpService.DownloadFileAsync(file.Url, dest, null, file.Size, ct);
        progress?.Report($"Установлено: {fileName}");
        return fileName;
    }

    public static async Task<string> InstallProjectAsync(InstanceInfo instance, string projectSlugOrId,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Поиск проекта…");
        var hits = await SearchAsync(projectSlugOrId, instance.McVersion, instance.Loader, 0, 5, ct: ct);
        var hit = hits.Hits.FirstOrDefault(h =>
                      h.Slug.Equals(projectSlugOrId, StringComparison.OrdinalIgnoreCase) ||
                      h.ProjectId.Equals(projectSlugOrId, StringComparison.OrdinalIgnoreCase))
                  ?? hits.Hits.FirstOrDefault();
        if (hit == null)
            throw new InvalidOperationException($"Проект «{projectSlugOrId}» не найден для {instance.McVersion}.");

        return await InstallModAsync(instance, hit.ProjectId, progress, ct);
    }

    /// <summary>Устанавливает модпак из файла .mrpack (формат Modrinth).</summary>
    public static async Task<InstanceInfo> ImportMrpackAsync(string mrpackPath, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.GetEntry("modrinth.index.json")
                         ?? throw new InvalidOperationException("В архиве нет modrinth.index.json — это не .mrpack.");

        using var stream = indexEntry.Open();
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var name = GetString(root, "name") ?? Path.GetFileNameWithoutExtension(mrpackPath);
        var summary = GetString(root, "summary") ?? "";
        var dependencies = root.TryGetProperty("dependencies", out var deps)
            ? deps.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
            : new Dictionary<string, string>();

        var mc = dependencies.GetValueOrDefault("minecraft") ?? "";
        if (string.IsNullOrWhiteSpace(mc))
            throw new InvalidOperationException("В манифесте сборки не указана версия Minecraft.");

        var loader = "vanilla";
        var loaderVersion = "";
        if (dependencies.ContainsKey("fabric-loader"))
        {
            loader = "fabric";
            loaderVersion = dependencies["fabric-loader"];
        }
        else if (dependencies.ContainsKey("quilt-loader"))
        {
            loader = "quilt";
            loaderVersion = dependencies["quilt-loader"];
        }
        else if (dependencies.ContainsKey("neoforge") || dependencies.ContainsKey("neo-forge"))
        {
            loader = "neoforge";
            loaderVersion = dependencies.GetValueOrDefault("neoforge") ?? dependencies.GetValueOrDefault("neo-forge") ?? "";
        }
        else if (dependencies.ContainsKey("forge"))
        {
            loader = "forge";
            loaderVersion = dependencies["forge"];
        }

        var instance = await InstanceService.CreateAsync(name, mc, loader, loaderVersion, progress, ct,
            installLoader: false, summary: summary);

        // Файлы сборки
        var files = root.TryGetProperty("files", out var filesEl) ? filesEl : default;
        if (files.ValueKind == JsonValueKind.Array)
        {
            var list = files.EnumerateArray().ToList();
            var done = 0;
            foreach (var file in list)
            {
                ct.ThrowIfCancellationRequested();
                var path = GetString(file, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (file.TryGetProperty("env", out var env) &&
                    env.TryGetProperty("client", out var clientEnv) &&
                    clientEnv.GetString() == "unsupported")
                {
                    done++;
                    continue;
                }

                var downloads = file.TryGetProperty("downloads", out var dls) ? dls : default;
                var url = downloads.ValueKind == JsonValueKind.Array
                    ? downloads.EnumerateArray().FirstOrDefault().GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var dest = SafeChild(instance.Directory, path);
                if (dest == null) continue;

                var size = file.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : 0;
                progress?.Report(new ProgressInfo
                {
                    Stage = "Сборка",
                    Fraction = 0.2 + 0.5 * done / Math.Max(1, list.Count),
                    Detail = Path.GetFileName(path)
                });
                await HttpService.DownloadFileAsync(url, dest, null, size, ct);
                done++;
            }
        }

        // Переопределения
        foreach (var entry in archive.Entries)
        {
            var entryName = entry.FullName.Replace('\\', '/');
            string? relative = null;
            if (entryName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                relative = entryName["overrides/".Length..];
            else if (entryName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
                relative = entryName["client-overrides/".Length..];

            if (string.IsNullOrWhiteSpace(relative) || relative.EndsWith('/')) continue;
            var dest = SafeChild(instance.Directory, relative);
            if (dest == null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }

        progress?.Report(new ProgressInfo { Stage = "Сборка", Fraction = 0.75, Detail = "установка загрузчика" });
        await InstanceService.InstallLoaderAsync(instance, progress, ct);
        return instance;
    }

    public static async Task<string?> GetProjectTitleAsync(string projectId, CancellationToken ct = default)
    {
        var json = await HttpService.TryGetStringAsync($"{Api}/project/{Uri.EscapeDataString(projectId)}", ct);
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return GetString(doc.RootElement, "title");
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeChild(string root, string relative)
    {
        relative = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
