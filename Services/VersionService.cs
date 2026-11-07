using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class VersionService
{
    private static readonly object ManifestSync = new();
    private static VersionManifest? _manifest;
    private static DateTime _manifestLoadedAt;

    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public static string VersionDir(string id) => Path.Combine(Paths.Versions, id);
    public static string VersionJsonPath(string id) => Path.Combine(VersionDir(id), id + ".json");

    public static async Task<VersionManifest> GetManifestAsync(bool refresh = false, CancellationToken ct = default)
    {
        lock (ManifestSync)
        {
            if (!refresh && _manifest != null && DateTime.UtcNow - _manifestLoadedAt < TimeSpan.FromMinutes(15))
                return _manifest;
        }

        var cacheFile = Path.Combine(Paths.Cache, "version_manifest_v2.json");
        VersionManifest? result = null;

        if (!refresh && File.Exists(cacheFile) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromHours(1))
        {
            try
            {
                result = JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(cacheFile, ct),
                    HttpService.JsonOptions);
            }
            catch { }
        }

        if (result == null || result.Versions.Count == 0)
        {
            await HttpService.DownloadFileAsync(ManifestUrl, cacheFile, null, 0, ct);
            result = JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(cacheFile, ct),
                       HttpService.JsonOptions)
                     ?? throw new InvalidOperationException("Не удалось прочитать список версий Minecraft.");
        }

        lock (ManifestSync)
        {
            _manifest = result;
            _manifestLoadedAt = DateTime.UtcNow;
        }
        return result;
    }

    public static async Task EnsureVersionJsonAsync(string id, CancellationToken ct = default)
    {
        var path = VersionJsonPath(id);
        if (File.Exists(path) && new FileInfo(path).Length > 10) return;

        var manifest = await GetManifestAsync(ct: ct);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Версия {id} не найдена в манифесте Mojang.");

        Directory.CreateDirectory(VersionDir(id));
        await HttpService.DownloadFileAsync(entry.Url, path, null, 0, ct);
    }

    public static async Task<List<VersionDetails>> LoadChainAsync(string id, CancellationToken ct = default)
    {
        var chain = new List<VersionDetails>();
        string? current = id;
        var guard = 0;

        while (!string.IsNullOrWhiteSpace(current) && guard++ < 12)
        {
            await EnsureVersionJsonAsync(current, ct);
            var details = ParseVersion(VersionJsonPath(current));
            if (string.IsNullOrWhiteSpace(details.Id)) details.Id = current;
            chain.Insert(0, details);
            current = details.InheritsFrom;
        }

        if (chain.Count == 0)
            throw new InvalidOperationException("Не удалось загрузить описание версии " + id);

        return chain;
    }

    public static async Task<VersionDetails> GetMergedAsync(string id, CancellationToken ct = default)
    {
        var chain = await LoadChainAsync(id, ct);
        return Merge(id, chain);
    }

    private static VersionDetails Merge(string id, List<VersionDetails> chain)
    {
        var merged = new VersionDetails { Id = id };
        foreach (var v in chain)
        {
            if (!string.IsNullOrWhiteSpace(v.MainClass)) merged.MainClass = v.MainClass;
            if (!string.IsNullOrWhiteSpace(v.Assets)) merged.Assets = v.Assets;
            if (v.AssetIndex != null) merged.AssetIndex = v.AssetIndex;
            if (v.JavaVersion != null) merged.JavaVersion = v.JavaVersion;
            if (!string.IsNullOrWhiteSpace(v.Type)) merged.Type = v.Type;
            if (!string.IsNullOrWhiteSpace(v.ReleaseTime)) merged.ReleaseTime = v.ReleaseTime;
            if (!string.IsNullOrWhiteSpace(v.MinecraftArguments)) merged.MinecraftArguments = v.MinecraftArguments;
            if (v.Logging != null) merged.Logging = v.Logging;
            merged.Arguments.Game.AddRange(v.Arguments.Game);
            merged.Arguments.Jvm.AddRange(v.Arguments.Jvm);
        }

        merged.Downloads = chain[0].Downloads;

        // Загрузчики (Fabric/Quilt/Forge) приносят свои версии тех же Maven-библиотек.
        // Оставляем одну версию на group:artifact[:classifier] — иначе на classpath
        // оказываются два ASM/Guava/Mixin и Fabric падает с duplicate classes.
        var libraryMap = new Dictionary<string, Library>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var library in chain.SelectMany(c => c.Libraries))
        {
            var key = LibraryKey(library.Name);
            if (!libraryMap.TryGetValue(key, out var existing))
            {
                order.Add(key);
                libraryMap[key] = library;
                continue;
            }

            var currentVersion = LibraryVersion(library.Name);
            var existingVersion = LibraryVersion(existing.Name);
            if (LoaderService.VersionComparer.Instance.Compare(currentVersion, existingVersion) >= 0)
                libraryMap[key] = library;
        }
        merged.Libraries = order.Select(k => libraryMap[k]).ToList();
        return merged;
    }

    private static string LibraryKey(string name)
    {
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var parts = name.Split(':');
        if (parts.Length < 2) return name;
        var classifier = parts.Length > 3 ? parts[3] : "";
        return parts[0] + ":" + parts[1] + ":" + classifier;
    }

    private static string LibraryVersion(string name)
    {
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var parts = name.Split(':');
        return parts.Length > 2 ? parts[2] : "";
    }

    private static VersionDetails ParseVersion(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var v = new VersionDetails
        {
            Id = GetString(root, "id") ?? Path.GetFileNameWithoutExtension(path),
            InheritsFrom = GetString(root, "inheritsFrom"),
            MainClass = GetString(root, "mainClass"),
            Assets = GetString(root, "assets"),
            Type = GetString(root, "type"),
            MinecraftArguments = GetString(root, "minecraftArguments"),
            ReleaseTime = GetString(root, "releaseTime")
        };

        if (root.TryGetProperty("arguments", out var arguments))
        {
            if (arguments.TryGetProperty("game", out var game)) v.Arguments.Game = ParseArguments(game);
            if (arguments.TryGetProperty("jvm", out var jvm)) v.Arguments.Jvm = ParseArguments(jvm);
        }

        if (root.TryGetProperty("assetIndex", out var ai))
            v.AssetIndex = JsonSerializer.Deserialize<AssetIndexInfo>(ai.GetRawText(), HttpService.JsonOptions);

        if (root.TryGetProperty("downloads", out var downloads))
            v.Downloads = JsonSerializer.Deserialize<Dictionary<string, DownloadInfo>>(downloads.GetRawText(),
                HttpService.JsonOptions);

        if (root.TryGetProperty("libraries", out var libraries))
            v.Libraries = JsonSerializer.Deserialize<List<Library>>(libraries.GetRawText(), HttpService.JsonOptions)
                          ?? new List<Library>();

        if (root.TryGetProperty("javaVersion", out var javaVersion))
            v.JavaVersion = JsonSerializer.Deserialize<JavaVersionInfo>(javaVersion.GetRawText(), HttpService.JsonOptions);

        if (root.TryGetProperty("logging", out var logging))
            v.Logging = JsonSerializer.Deserialize<LoggingConfig>(logging.GetRawText(), HttpService.JsonOptions);

        return v;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static List<ArgumentItem> ParseArguments(JsonElement array)
    {
        var result = new List<ArgumentItem>();
        if (array.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(new ArgumentItem { Single = item.GetString() });
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var arg = new ArgumentItem();
                if (item.TryGetProperty("rules", out var rules))
                    arg.Rules = JsonSerializer.Deserialize<List<Rule>>(rules.GetRawText(), HttpService.JsonOptions);
                if (item.TryGetProperty("value", out var value))
                {
                    if (value.ValueKind == JsonValueKind.String) arg.Single = value.GetString();
                    else if (value.ValueKind == JsonValueKind.Array)
                        arg.Multiple = value.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString()!)
                            .ToList();
                }
                result.Add(arg);
            }
        }

        return result;
    }

    public static bool IsInstalled(string id)
    {
        try
        {
            var marker = Path.Combine(VersionDir(id), ".installed");
            if (!File.Exists(marker)) return false;

            // Проверяем цепочку наследования по локальным json-файлам, без сети.
            string? current = id;
            var guard = 0;
            while (!string.IsNullOrWhiteSpace(current) && guard++ < 12)
            {
                var json = VersionJsonPath(current);
                if (!File.Exists(json)) return false;

                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var root = doc.RootElement;
                if (root.TryGetProperty("downloads", out var downloads) &&
                    downloads.TryGetProperty("client", out _))
                {
                    var client = Path.Combine(VersionDir(current), current + ".jar");
                    if (!File.Exists(client)) return false;
                }

                current = root.TryGetProperty("inheritsFrom", out var inh) ? inh.GetString() : null;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<InstalledVersionVm> GetInstalledVersions()
    {
        var result = new List<InstalledVersionVm>();
        if (!Directory.Exists(Paths.Versions)) return result;

        foreach (var file in Directory.EnumerateFiles(Paths.Versions, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var id = GetString(doc.RootElement, "id") ?? Path.GetFileNameWithoutExtension(file);
                if (!Path.GetFileNameWithoutExtension(file).Equals(id, StringComparison.OrdinalIgnoreCase)) continue;

                var kind = "";
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var inherits))
                {
                    var parent = inherits.GetString() ?? "";
                    kind = id.Contains("fabric", StringComparison.OrdinalIgnoreCase) ? "Fabric"
                        : id.Contains("quilt", StringComparison.OrdinalIgnoreCase) ? "Quilt"
                        : id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "NeoForge"
                        : id.Contains("forge", StringComparison.OrdinalIgnoreCase) ? "Forge"
                        : "Модифицированная · " + parent;
                }

                result.Add(new InstalledVersionVm { Id = id, Kind = kind });
            }
            catch { }
        }

        return result.OrderByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ────────────────────────────── INSTALL ──────────────────────────────

    public static async Task InstallAsync(string id, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new ProgressInfo { Stage = "Подготовка", Fraction = 0.01, Detail = id });
        var chain = await LoadChainAsync(id, ct);
        var merged = Merge(id, chain);
        var root = chain[0];
        Directory.CreateDirectory(VersionDir(id));

        // 1. Клиентский jar
        if (root.Downloads != null && root.Downloads.TryGetValue("client", out var client) &&
            !string.IsNullOrWhiteSpace(client.Url))
        {
            var clientPath = Path.Combine(VersionDir(root.Id), root.Id + ".jar");
            if (!File.Exists(clientPath))
            {
                progress?.Report(new ProgressInfo { Stage = "Клиент", Fraction = 0.03, Detail = root.Id });
                await HttpService.DownloadFileAsync(client.Url, clientPath, null, client.Size, ct);
            }
        }

        // 2. Конфигурация логирования
        string? loggingPath = null;
        if (merged.Logging?.Client?.File?.Url != null)
        {
            var fileId = merged.Logging.Client.Id ?? "client";
            loggingPath = Path.Combine(Paths.Assets, "log_configs", fileId);
            if (!File.Exists(loggingPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(loggingPath)!);
                await HttpService.DownloadFileAsync(merged.Logging.Client.File.Url, loggingPath, null,
                    merged.Logging.Client.File.Size, ct);
            }
        }

        // 3. Индекс ресурсов
        var assetsFraction = 0.08;
        if (merged.AssetIndex != null)
        {
            progress?.Report(new ProgressInfo { Stage = "Ресурсы", Fraction = 0.05, Detail = "индекс " + merged.AssetIndex.Id });
            await DownloadAssetsAsync(merged.AssetIndex, p => assetsFraction = p, progress, ct);
        }

        // 4. Библиотеки
        await DownloadLibrariesAsync(merged, progress, assetsFraction, ct);

        // 5. Нативные библиотеки
        ExtractNatives(merged);

        await File.WriteAllTextAsync(Path.Combine(VersionDir(id), ".installed"),
            DateTime.UtcNow.ToString("O"), ct);

        progress?.Report(new ProgressInfo { Stage = "Готово", Fraction = 1, Detail = id });
    }

    private static async Task DownloadAssetsAsync(AssetIndexInfo index, Action<double> reportFraction,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var indexDir = Path.Combine(Paths.Assets, "indexes");
        Directory.CreateDirectory(indexDir);
        var indexPath = Path.Combine(indexDir, index.Id + ".json");

        if (!File.Exists(indexPath) && !string.IsNullOrWhiteSpace(index.Url))
            await HttpService.DownloadFileAsync(index.Url, indexPath, null, index.Size, ct);

        if (!File.Exists(indexPath)) return;

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, ct));
        var root = doc.RootElement;
        var isVirtual = root.TryGetProperty("virtual", out var virt) && virt.ValueKind == JsonValueKind.True;
        var mapToResources = root.TryGetProperty("map_to_resources", out var map) && map.ValueKind == JsonValueKind.True;

        if (!root.TryGetProperty("objects", out var objects)) return;

        var list = new List<(string Hash, long Size, string Name)>();
        foreach (var prop in objects.EnumerateObject())
        {
            if (!prop.Value.TryGetProperty("hash", out var hashEl)) continue;
            var hash = hashEl.GetString();
            if (string.IsNullOrWhiteSpace(hash)) continue;
            var size = prop.Value.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
            list.Add((hash, size, prop.Name));
        }

        var unique = list.GroupBy(x => x.Hash).Select(g => g.First()).ToList();
        var done = 0;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = 20, CancellationToken = ct };

        await Parallel.ForEachAsync(unique, parallel, async (asset, token) =>
        {
            var dest = AssetPath(asset.Hash);
            if (!File.Exists(dest) || new FileInfo(dest).Length != asset.Size)
                await HttpService.DownloadFileAsync(
                    $"https://resources.download.minecraft.net/{asset.Hash[..2]}/{asset.Hash}", dest, null,
                    asset.Size, token);

            var current = Interlocked.Increment(ref done);
            if (current % 100 == 0 || current == unique.Count)
            {
                reportFraction(0.08 + 0.22 * current / Math.Max(1, unique.Count));
                progress?.Report(new ProgressInfo
                {
                    Stage = "Ресурсы",
                    Fraction = 0.08 + 0.22 * current / Math.Max(1, unique.Count),
                    Detail = $"{current}/{unique.Count}"
                });
            }
        });

        if (isVirtual || mapToResources)
        {
            var virtualDir = Path.Combine(Paths.Assets, "virtual", index.Id);
            foreach (var asset in list)
            {
                var src = AssetPath(asset.Hash);
                if (!File.Exists(src)) continue;
                var dest = Path.Combine(virtualDir, asset.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) File.Copy(src, dest);
            }
        }
    }

    private static string AssetPath(string hash) =>
        Path.Combine(Paths.Assets, "objects", hash[..2], hash);

    private static async Task DownloadLibrariesAsync(VersionDetails merged, IProgress<ProgressInfo>? progress,
        double startFraction, CancellationToken ct)
    {
        var osFeatures = BuildFeatures();
        var allowed = merged.Libraries.Where(l => EvaluateRules(l.Rules, osFeatures)).ToList();
        var total = Math.Max(1, allowed.Count);
        var done = 0;

        foreach (var library in allowed)
        {
            ct.ThrowIfCancellationRequested();

            var artifact = ResolveArtifact(library);
            if (artifact != null && !string.IsNullOrWhiteSpace(artifact.Value.Url))
            {
                var dest = Path.Combine(Paths.Libraries, artifact.Value.Relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dest))
                    await HttpService.DownloadFileAsync(artifact.Value.Url, dest, null, artifact.Value.Size, ct);
            }

            var native = ResolveNative(library);
            if (native != null && !string.IsNullOrWhiteSpace(native.Url))
            {
                var relative = !string.IsNullOrWhiteSpace(native.Path) ? native.Path : MavenPath(library.Name, "natives-windows");
                if (!string.IsNullOrWhiteSpace(relative))
                {
                    var dest = Path.Combine(Paths.Libraries, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(dest))
                        await HttpService.DownloadFileAsync(native.Url, dest, null, native.Size, ct);
                }
            }

            done++;
            var fraction = startFraction + (1 - startFraction) * 0.9 * done / total;
            if (done % 3 == 0 || done == total)
                progress?.Report(new ProgressInfo
                {
                    Stage = "Библиотеки",
                    Fraction = fraction,
                    Detail = $"{done}/{total}"
                });
        }
    }

    /// <summary>
    /// Возвращает путь/URL основного артефакта библиотеки. Mojang-версии используют
    /// downloads.artifact, а Fabric/Quilt — поле name + базовый url репозитория.
    /// </summary>
    private static (string Relative, string Url, long Size)? ResolveArtifact(Library library)
    {
        var artifact = library.Downloads?.Artifact;
        if (artifact != null)
        {
            var relative = !string.IsNullOrWhiteSpace(artifact.Path) ? artifact.Path : MavenPath(library.Name);
            if (!string.IsNullOrWhiteSpace(relative))
            {
                if (!string.IsNullOrWhiteSpace(artifact.Url))
                    return (relative, artifact.Url, artifact.Size);

                // Установщик Forge кладёт пропатченный client.jar локально, а в json
                // оставляет пустой url. Такой артефакт всё равно должен попасть в classpath.
                var local = Path.Combine(Paths.Libraries, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(local))
                    return (relative, "", artifact.Size);
            }
        }

        if (!string.IsNullOrWhiteSpace(library.Url))
        {
            var relative = MavenPath(library.Name);
            if (!string.IsNullOrWhiteSpace(relative))
            {
                var baseUrl = library.Url.EndsWith("/", StringComparison.Ordinal)
                    ? library.Url
                    : library.Url + "/";
                return (relative, baseUrl + relative, library.Size);
            }
        }

        return null;
    }

    private static DownloadInfo? ResolveNative(Library library)
    {
        if (library.Natives == null || library.Downloads?.Classifiers == null) return null;
        if (!library.Natives.TryGetValue("windows", out var classifier) || string.IsNullOrWhiteSpace(classifier))
            return null;

        classifier = classifier.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
        return library.Downloads.Classifiers.TryGetValue(classifier, out var info) ? info : null;
    }

    private static void ExtractNatives(VersionDetails merged)
    {
        var nativesDir = Path.Combine(VersionDir(merged.Id), "natives");
        try
        {
            if (Directory.Exists(nativesDir)) Directory.Delete(nativesDir, true);
        }
        catch { }
        Directory.CreateDirectory(nativesDir);

        foreach (var library in merged.Libraries)
        {
            var native = ResolveNative(library);
            if (native == null) continue;

            var relative = !string.IsNullOrWhiteSpace(native.Path) ? native.Path : MavenPath(library.Name, "natives-windows");
            var jar = Path.Combine(Paths.Libraries, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(jar)) continue;

            try
            {
                using var archive = ZipFile.OpenRead(jar);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (library.Extract?.Exclude?.Any(ex =>
                            entry.FullName.StartsWith(ex, StringComparison.OrdinalIgnoreCase)) == true) continue;

                    var dest = Path.Combine(nativesDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, true);
                }
            }
            catch (Exception ex)
            {
                LogService.WriteCrash("ExtractNatives", $"{jar}: {ex.Message}");
            }
        }
    }

    public static string MavenPath(string name, string? forcedClassifier = null)
    {
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }

        var parts = name.Split(':');
        if (parts.Length < 3) return "";
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = forcedClassifier ?? (parts.Length > 3 ? parts[3] : null);

        var fileName = artifact + "-" + version + (classifier != null ? "-" + classifier : "") + "." + ext;
        return $"{group}/{artifact}/{version}/{fileName}";
    }

    // ────────────────────────────── LAUNCH ──────────────────────────────

    public static async Task<LaunchResult> LaunchAsync(string versionId, string gameDir, Account account,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var result = new LaunchResult { VersionId = versionId, StartedAt = DateTime.Now };
        var settings = SettingsService.Current;

        if (!IsInstalled(versionId))
        {
            progress?.Report(new ProgressInfo { Stage = "Установка", Fraction = 0, Detail = versionId });
            await InstallAsync(versionId, progress, ct);
        }

        var merged = await GetMergedAsync(versionId, ct);
        var chain = await LoadChainAsync(versionId, ct);
        var root = chain[0];
        if (string.IsNullOrWhiteSpace(merged.MainClass))
            throw new InvalidOperationException("В описании версии отсутствует mainClass.");

        var mcVersion = root.Id;
        progress?.Report(new ProgressInfo { Stage = "Java", Fraction = 0, Detail = "поиск подходящей версии" });
        var java = await JavaService.ResolveAsync(mcVersion, settings.JavaPath, true, progress, ct);

        Directory.CreateDirectory(gameDir);
        var nativesDir = Path.Combine(VersionDir(versionId), "natives");
        Directory.CreateDirectory(nativesDir);

        var osFeatures = BuildFeatures();
        osFeatures["has_custom_resolution"] = true;

        var classpath = BuildClasspath(merged, root, out var clientJarName);
        var loggingPath = merged.Logging?.Client?.File?.Url != null
            ? Path.Combine(Paths.Assets, "log_configs", merged.Logging.Client.Id ?? "client")
            : "";

        var subs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = account.Username,
            ["version_name"] = versionId,
            ["game_directory"] = gameDir,
            ["assets_root"] = Paths.Assets,
            ["assets_index_name"] = merged.Assets ?? merged.AssetIndex?.Id ?? "legacy",
            ["auth_uuid"] = account.Uuid.Replace("-", ""),
            ["auth_access_token"] = string.IsNullOrWhiteSpace(account.AccessToken) ? "0" : account.AccessToken,
            ["auth_session"] = $"token:{(string.IsNullOrWhiteSpace(account.AccessToken) ? "0" : account.AccessToken)}:{account.Uuid}",
            ["clientid"] = "vibe-launcher",
            ["auth_xuid"] = "0",
            ["user_type"] = account.IsMicrosoft ? "msa" : "legacy",
            ["version_type"] = merged.Type ?? "release",
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "VibeLauncher",
            ["launcher_version"] = "1.0.0",
            ["classpath"] = classpath,
            ["classpath_separator"] = ";",
            ["library_directory"] = Paths.Libraries,
            ["game_assets"] = Path.Combine(Paths.Assets, "virtual", merged.Assets ?? "legacy"),
            ["user_properties"] = "{}",
            ["resolution_width"] = Math.Max(320, settings.WindowWidth).ToString(),
            ["resolution_height"] = Math.Max(240, settings.WindowHeight).ToString(),
            ["path"] = loggingPath
        };

        var jvmArgs = new List<string>
        {
            $"-Xmx{Math.Max(512, settings.RamMb)}M",
            $"-Xms{Math.Min(Math.Max(512, settings.RamMb / 2), 2048)}M",
            "-Dfile.encoding=UTF-8",
            "-Dminecraft.launcher.brand=VibeLauncher",
            "-Dminecraft.launcher.version=1.0.0"
        };

        jvmArgs.AddRange(ResolveArguments(merged.Arguments.Jvm, osFeatures, subs));

        // NeoForge/Forge-загрузчики исключают базовый jar игры из слоя модулей
        // через -DignoreList. Мы добавляем в classpath jar родительской версии
        // (например 1.21.4.jar), поэтому его имя тоже должно попасть в список,
        // иначе ModuleLayer падает на конфликте пакета net.minecraft.client.main.
        if (!string.IsNullOrWhiteSpace(clientJarName))
        {
            for (var i = 0; i < jvmArgs.Count; i++)
            {
                if (!jvmArgs[i].StartsWith("-DignoreList=", StringComparison.OrdinalIgnoreCase)) continue;
                var ignored = jvmArgs[i]["-DignoreList=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (!ignored.Contains(clientJarName, StringComparer.OrdinalIgnoreCase))
                {
                    ignored.Add(clientJarName);
                    jvmArgs[i] = "-DignoreList=" + string.Join(',', ignored);
                }
            }
        }

        if (!jvmArgs.Any(a => a is "-cp" or "-classpath"))
        {
            jvmArgs.Add("-Djava.library.path=" + nativesDir);
            jvmArgs.Add("-cp");
            jvmArgs.Add(classpath);
        }

        if (!string.IsNullOrWhiteSpace(loggingPath) && File.Exists(loggingPath) &&
            !jvmArgs.Any(a => a.Contains("log4j.configurationFile", StringComparison.OrdinalIgnoreCase)))
            jvmArgs.Add("-Dlog4j.configurationFile=" + loggingPath);

        jvmArgs.AddRange(SplitArguments(settings.JvmArgs));

        var gameArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(merged.MinecraftArguments))
        {
            gameArgs.AddRange(merged.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => Substitute(a, subs)));
        }
        else
        {
            gameArgs.AddRange(ResolveArguments(merged.Arguments.Game, osFeatures, subs));
        }

        if (settings.Fullscreen && !gameArgs.Contains("--fullscreen")) gameArgs.Add("--fullscreen");

        jvmArgs.Add(merged.MainClass);
        jvmArgs.AddRange(gameArgs);

        var logPath = LogService.CreateGameLogPath(versionId);
        result.LogPath = logPath;

        var psi = new ProcessStartInfo
        {
            FileName = java.Path,
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in jvmArgs.Where(a => a != null)) psi.ArgumentList.Add(arg);

        var logBuffer = new StringBuilder();
        var logLock = new object();
        var converter = new LogService.Log4jXmlConverter();
        var writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false)) { AutoFlush = true };

        // stdout Minecraft приходит в XML (LegacyXMLLayout) — сохраняем уже читаемый текст.
        void Append(string? line)
        {
            if (line == null) return;
            foreach (var text in converter.Convert(line))
            {
                lock (logLock)
                {
                    if (logBuffer.Length < 4_000_000) logBuffer.AppendLine(text);
                    try { writer.WriteLine(text); } catch { }
                }
            }
        }

        await writer.WriteLineAsync($"# Vibe Launcher — запуск {versionId}");
        await writer.WriteLineAsync($"# Java: {java.Path} ({java.Version})");
        await writer.WriteLineAsync($"# RAM: {settings.RamMb} МБ");
        await writer.WriteLineAsync($"# Каталог игры: {gameDir}");
        await writer.WriteLineAsync($"# {java.Path} {string.Join(' ', jvmArgs.Select(QuoteForLog))}");
        await writer.WriteLineAsync(new string('=', 80));

        progress?.Report(new ProgressInfo { Stage = "Запуск", Fraction = 1, Detail = versionId });

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить процесс Java.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                try { process.Kill(true); } catch { }
            }

            result.ExitCode = process.ExitCode;
            await process.WaitForExitAsync(CancellationToken.None);

            // crash-reports и hs_err появляются в каталоге игры
            AppendExtraCrashInfo(gameDir, result.StartedAt, Append);

            await writer.WriteLineAsync(new string('=', 80));
            await writer.WriteLineAsync($"# Код выхода: {result.ExitCode}");
            await writer.FlushAsync(CancellationToken.None);
        }
        finally
        {
            writer.Dispose();
            result.EndedAt = DateTime.Now;
        }

        result.Findings = CrashAnalyzer.Analyze(logBuffer.ToString(), result.ExitCode, gameDir, result.StartedAt);
        return result;
    }

    private static string QuoteForLog(string arg) =>
        arg.Contains(' ') ? "\"" + arg + "\"" : arg;

    private static void AppendExtraCrashInfo(string gameDir, DateTime since, Action<string?> append)
    {
        try
        {
            var reports = Path.Combine(gameDir, "crash-reports");
            if (Directory.Exists(reports))
            {
                var report = Directory.EnumerateFiles(reports, "*.txt")
                    .Select(p => new FileInfo(p))
                    .Where(f => f.LastWriteTime >= since.AddMinutes(-2))
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();
                if (report != null)
                {
                    var text = File.ReadAllText(report.FullName);
                    if (text.Length > 120_000) text = text[..120_000];
                    append("\n===== CRASH REPORT: " + report.Name + " =====");
                    foreach (var line in text.Split('\n')) append(line.TrimEnd('\r'));
                }
            }

            var hsErr = Directory.EnumerateFiles(gameDir, "hs_err_pid*.log")
                .Select(p => new FileInfo(p))
                .Where(f => f.LastWriteTime >= since.AddMinutes(-2))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (hsErr != null)
            {
                append("\n===== JVM CRASH: " + hsErr.Name + " =====");
                var lines = File.ReadLines(hsErr.FullName).Take(120);
                foreach (var line in lines) append(line);
            }
        }
        catch { }
    }

    private static string BuildClasspath(VersionDetails merged, VersionDetails root, out string? clientJarName)
    {
        clientJarName = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        var osFeatures = BuildFeatures();

        foreach (var library in merged.Libraries)
        {
            if (!EvaluateRules(library.Rules, osFeatures)) continue;
            var artifact = ResolveArtifact(library);
            if (artifact == null) continue;
            var path = Path.Combine(Paths.Libraries,
                artifact.Value.Relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && seen.Add(path)) files.Add(path);
        }

        if (root.Downloads != null && root.Downloads.ContainsKey("client"))
        {
            var client = Path.Combine(VersionDir(root.Id), root.Id + ".jar");
            if (File.Exists(client) && seen.Add(client))
            {
                files.Add(client);
                clientJarName = root.Id + ".jar";
            }
        }

        return string.Join(";", files);
    }

    private static List<string> ResolveArguments(List<ArgumentItem> items, Dictionary<string, bool> features,
        Dictionary<string, string> subs)
    {
        var result = new List<string>();
        foreach (var item in items)
        {
            if (!EvaluateRules(item.Rules, features)) continue;
            foreach (var value in item.Values)
                result.Add(Substitute(value, subs));
        }
        return result;
    }

    private static string Substitute(string value, Dictionary<string, string> subs)
    {
        foreach (var kv in subs)
            value = value.Replace("${" + kv.Key + "}", kv.Value, StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    public static Dictionary<string, bool> BuildFeatures()
    {
        var osName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = false,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false,
            ["os_name"] = true,
            ["os_arch"] = true,
            ["os_version"] = true,
            ["os_" + osName] = true,
            ["os_" + arch] = true
        };
    }

    public static bool EvaluateRules(List<Rule>? rules, Dictionary<string, bool> features)
    {
        if (rules == null || rules.Count == 0) return true;

        var osName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        var allowed = false;
        foreach (var rule in rules)
        {
            var matches = true;
            if (rule.Os != null)
            {
                if (!string.IsNullOrWhiteSpace(rule.Os.Name) &&
                    !rule.Os.Name.Equals(osName, StringComparison.OrdinalIgnoreCase)) matches = false;
                if (matches && !string.IsNullOrWhiteSpace(rule.Os.Arch) &&
                    !rule.Os.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase)) matches = false;
            }

            if (matches && rule.Features != null)
            {
                foreach (var feature in rule.Features)
                {
                    if (!features.TryGetValue(feature.Key, out var value) || value != feature.Value)
                    {
                        matches = false;
                        break;
                    }
                }
            }

            if (matches)
                allowed = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }
}
