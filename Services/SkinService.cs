using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class SkinService
{
    public const string TLauncherSkinsUrl = "https://tlauncher.org/en/catalog/skins/nickname/";
    public const string TLauncherLoginUrl = "https://tlauncher.org/en/#login";
    public const string TLauncherProfileUrl = "https://tlauncher.org/en/my/";
    public const string TLauncherRegisterUrl = "https://tlauncher.org/en/reg/";
    public const string TLauncherTextureApi = "https://auth.tlauncher.org/skin/profile/texture/login/";
    public const string ElySkinsUrl = "https://ely.by/skins";
    public const string ElyLoginUrl = "https://ely.by/login";
    public const string ElyProfileUrl = "https://ely.by/profile";

    // Сайт TLauncher отдаёт этот PNG для любого ника без скина.
    private static readonly HashSet<string> TLauncherDefaultHashes =
        new(StringComparer.OrdinalIgnoreCase) { "FD5D1E367087D7EA27C0C38315A466A6" };

    public static async Task<SkinProfile> FetchMojangAsync(string nickname, CancellationToken ct = default)
    {
        nickname = nickname.Trim();
        if (string.IsNullOrWhiteSpace(nickname))
            throw new InvalidOperationException("Введите никнейм.");

        var userJson = await HttpService.TryGetStringAsync(
            $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(nickname)}", ct)
            ?? throw new InvalidOperationException($"Игрок «{nickname}» не найден в Mojang.");

        using var userDoc = JsonDocument.Parse(userJson);
        var uuid = userDoc.RootElement.GetProperty("id").GetString()
                   ?? throw new InvalidOperationException("Mojang не вернул UUID игрока.");
        var realName = userDoc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? nickname : nickname;

        var profileJson = await HttpService.TryGetStringAsync(
            $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}", ct)
            ?? throw new InvalidOperationException("Не удалось получить профиль игрока (возможно, лимит запросов).");

        using var profile = JsonDocument.Parse(profileJson);
        var profileRoot = profile.RootElement;
        var name = profileRoot.TryGetProperty("name", out var pn) ? pn.GetString() ?? realName : realName;

        string? skinUrl = null, capeUrl = null;
        var model = "classic";

        if (profileRoot.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateArray())
            {
                if (prop.TryGetProperty("name", out var pname) && pname.GetString() == "textures" &&
                    prop.TryGetProperty("value", out var valueEl))
                {
                    var base64 = valueEl.GetString();
                    if (string.IsNullOrWhiteSpace(base64)) continue;
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    using var textures = JsonDocument.Parse(decoded);
                    if (!textures.RootElement.TryGetProperty("textures", out var tex)) continue;

                    if (tex.TryGetProperty("SKIN", out var skin) && skin.TryGetProperty("url", out var skinUrlEl))
                        skinUrl = skinUrlEl.GetString();
                    if (skin.TryGetProperty("metadata", out var meta) &&
                        meta.TryGetProperty("model", out var modelEl) &&
                        modelEl.GetString() == "slim")
                        model = "slim";

                    if (tex.TryGetProperty("CAPE", out var cape) && cape.TryGetProperty("url", out var capeUrlEl))
                        capeUrl = capeUrlEl.GetString();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(skinUrl))
            throw new InvalidOperationException($"У игрока «{name}» не установлен скин Mojang.");

        var profileResult = new SkinProfile
        {
            Provider = "Mojang",
            Nickname = name,
            SkinUrl = skinUrl,
            CapeUrl = capeUrl,
            Model = model
        };

        await CacheImagesAsync(profileResult, ct);
        return profileResult;
    }

    public static async Task<SkinProfile> FetchElyAsync(string nickname, CancellationToken ct = default)
    {
        nickname = nickname.Trim();
        if (string.IsNullOrWhiteSpace(nickname))
            throw new InvalidOperationException("Введите никнейм Ely.by.");

        var json = await HttpService.TryGetStringAsync(
            $"https://skinsystem.ely.by/textures/{Uri.EscapeDataString(nickname)}", ct);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException(
                $"Игрок «{nickname}» не найден в Ely.by (или у него нет скина).");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var skinUrl = ReadTexturesUrl(root, "SKIN");
        var capeUrl = ReadTexturesUrl(root, "CAPE");
        var model = "classic";
        if (root.TryGetProperty("MODEL", out var modelEl))
        {
            if (modelEl.ValueKind == JsonValueKind.String) model = modelEl.GetString() ?? "classic";
            else if (modelEl.ValueKind == JsonValueKind.Object && modelEl.TryGetProperty("type", out var typeEl))
                model = typeEl.GetString() ?? "classic";
        }

        if (string.IsNullOrWhiteSpace(skinUrl) && string.IsNullOrWhiteSpace(capeUrl))
            throw new InvalidOperationException($"У игрока «{nickname}» нет скина или плаща Ely.by.");

        var profile = new SkinProfile
        {
            Provider = "Ely.by",
            Nickname = nickname,
            SkinUrl = skinUrl,
            CapeUrl = capeUrl,
            Model = model.Equals("slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic"
        };

        await CacheImagesAsync(profile, ct);
        return profile;
    }

    /// <summary>
    /// Загружает скин TLauncher. Сначала официальное API auth.tlauncher.org (URL + модель),
    /// при недоступности — каталог tlauncher.org.
    /// </summary>
    public static async Task<SkinProfile> FetchTLauncherAsync(string nickname, CancellationToken ct = default)
    {
        nickname = nickname.Trim();
        if (string.IsNullOrWhiteSpace(nickname))
            throw new InvalidOperationException("Введите никнейм TLauncher.");
        if (nickname.Length > 64 || nickname.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Никнейм TLauncher не должен содержать пробелы.");

        string? apiFailure = null;

        // 1) Официальное API скинов TLauncher: {"SKIN":{"url":"…","metadata":{"model":"slim"}}}
        //    Пустой объект {} означает, что у ника нет скина.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var json = await HttpService.GetStringAsync(
                TLauncherTextureApi + Uri.EscapeDataString(nickname), timeout.Token);

            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var skinUrl = ReadTexturesUrl(root, "SKIN");

                if (!string.IsNullOrWhiteSpace(skinUrl))
                {
                    skinUrl = UpgradeToHttps(skinUrl);
                    var profile = new SkinProfile
                    {
                        Provider = "TLauncher",
                        Nickname = nickname,
                        SkinUrl = skinUrl,
                        CapeUrl = ReadTexturesUrl(root, "CAPE"),
                        Model = ReadTLauncherModel(root),
                        ModelDetected = true
                    };
                    await CacheImagesAsync(profile, ct);
                    return profile;
                }

                throw new InvalidOperationException(
                    $"У ника «{nickname}» нет скина на TLauncher. Загрузите скин на сайте TLauncher и повторите.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            apiFailure = "API TLauncher не ответило за 15 секунд";
        }
        catch (InvalidOperationException)
        {
            // API ответило корректно, но скина у ника нет — каталог не поможет.
            throw;
        }
        catch (Exception ex)
        {
            apiFailure = ex.Message;
        }

        // 2) Резервный вариант: картинка из каталога TLauncher.
        var url = $"https://tlauncher.org/catalog/nickname/download/{Uri.EscapeDataString(nickname)}.png";
        var safe = Paths.SafeName(nickname);
        var dest = Path.Combine(Paths.Skins, $"TLauncher-{safe}-{DateTime.Now.Ticks}.png");
        Directory.CreateDirectory(Paths.Skins);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await HttpService.DownloadFileAsync(url, dest, null, 0, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            CleanupDownload(dest);
            throw new InvalidOperationException(
                $"Сайт TLauncher не ответил за 20 секунд. Попробуйте позже или импортируйте PNG вручную.");
        }
        catch (Exception ex)
        {
            CleanupDownload(dest);
            var reason = string.IsNullOrWhiteSpace(apiFailure) ? "" : $" ({apiFailure})";
            throw new InvalidOperationException(
                $"Не удалось скачать скин TLauncher для «{nickname}»{reason}: {ex.Message}", ex);
        }

        if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
            throw new InvalidOperationException($"Сайт TLauncher не вернул скин для «{nickname}».");

        return new SkinProfile
        {
            Provider = "TLauncher",
            Nickname = nickname,
            SkinUrl = url,
            LocalSkinPath = dest,
            Model = "classic",
            ModelDetected = false,
            IsDefault = IsTLauncherDefaultSkin(dest)
        };
    }

    private static string UpgradeToHttps(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["http://".Length..]
            : url;

    private static string ReadTLauncherModel(JsonElement root)
    {
        if (root.TryGetProperty("SKIN", out var skin) &&
            skin.ValueKind == JsonValueKind.Object &&
            skin.TryGetProperty("metadata", out var meta) &&
            meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.String &&
            string.Equals(model.GetString(), "slim", StringComparison.OrdinalIgnoreCase))
            return "slim";

        return "classic";
    }

    private static void CleanupDownload(string destination)
    {
        try { if (File.Exists(destination)) File.Delete(destination); } catch { }
        try { if (File.Exists(destination + ".part")) File.Delete(destination + ".part"); } catch { }
    }

    private static bool IsTLauncherDefaultSkin(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(MD5.HashData(stream));
            return TLauncherDefaultHashes.Contains(hash);
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadTexturesUrl(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("url", out var url)) return url.GetString();
        return null;
    }

    private static async Task CacheImagesAsync(SkinProfile profile, CancellationToken ct)
    {
        var stamp = DateTime.Now.Ticks;
        var safe = Paths.SafeName(profile.Nickname);

        if (!string.IsNullOrWhiteSpace(profile.SkinUrl))
        {
            var dest = Path.Combine(Paths.Skins, $"{profile.Provider}-{safe}-{stamp}.png");
            await HttpService.DownloadFileAsync(profile.SkinUrl, dest, null, 0, ct);
            profile.LocalSkinPath = dest;
        }

        if (!string.IsNullOrWhiteSpace(profile.CapeUrl))
        {
            var dest = Path.Combine(Paths.Skins, $"{profile.Provider}-{safe}-cape-{stamp}.png");
            await HttpService.DownloadFileAsync(profile.CapeUrl, dest, null, 0, ct);
            profile.LocalCapePath = dest;
        }
    }

    public static SkinProfile ImportLocal(string sourcePath, string? kind = null)
    {
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException("Файл не найден.");

        var ext = Path.GetExtension(sourcePath);
        if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Скин должен быть PNG-файлом.");

        var dest = Path.Combine(Paths.Skins,
            $"Local-{Path.GetFileNameWithoutExtension(sourcePath)}-{DateTime.Now.Ticks}.png");
        File.Copy(sourcePath, dest, true);

        var profile = new SkinProfile { Provider = "Локальный", Nickname = "local", Model = "classic" };
        if (kind == "cape") profile.LocalCapePath = dest;
        else profile.LocalSkinPath = dest;
        return profile;
    }

    /// <summary>Ищет skin.png / cape.png в локальных папках TLauncher.</summary>
    public static SkinProfile? ImportFromTLauncher()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".tlauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tlauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tlauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "tlauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "TLauncher")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var pngs = Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories)
                    .Where(p => p.Contains("skin", StringComparison.OrdinalIgnoreCase) ||
                                p.Contains("cape", StringComparison.OrdinalIgnoreCase))
                    .Take(50)
                    .ToList();

                var skin = pngs.FirstOrDefault(p => p.Contains("skin", StringComparison.OrdinalIgnoreCase));
                var cape = pngs.FirstOrDefault(p => p.Contains("cape", StringComparison.OrdinalIgnoreCase));
                if (skin == null && cape == null) continue;

                var profile = new SkinProfile { Provider = "TLauncher", Nickname = "local", Model = "classic" };
                if (skin != null) profile.LocalSkinPath = CopySkinToStore(skin, "TLauncher-skin");
                if (cape != null) profile.LocalCapePath = CopySkinToStore(cape, "TLauncher-cape");
                return profile;
            }
            catch { }
        }

        return null;
    }

    private static string CopySkinToStore(string source, string prefix)
    {
        var dest = Path.Combine(Paths.Skins, $"{prefix}-{DateTime.Now.Ticks}.png");
        File.Copy(source, dest, true);
        return dest;
    }

    public static Task ApplyToInstanceAsync(InstanceInfo instance, Account account, SkinProfile profile,
        CancellationToken ct = default)
    {
        // CustomSkinLoader 15.x (Legacy LocalSkin) ищет файлы по путям:
        //   CustomSkinLoader/LocalSkin/skins/<ник>.png
        //   CustomSkinLoader/LocalSkin/capes/<ник>.png
        // Конфигурацию мода дополняем источниками TLauncher/Ely.by/LocalSkin,
        // не удаляя пользовательские настройки.
        EnsureCslConfig(instance);

        var localRoot = Path.Combine(instance.Directory, "CustomSkinLoader", "LocalSkin");
        var skinsDir = Path.Combine(localRoot, "skins");
        var capesDir = Path.Combine(localRoot, "capes");
        Directory.CreateDirectory(skinsDir);
        Directory.CreateDirectory(capesDir);

        var userName = Paths.SafeName(string.IsNullOrWhiteSpace(account.Username) ? "Player" : account.Username);

        if (!string.IsNullOrWhiteSpace(profile.LocalSkinPath) && File.Exists(profile.LocalSkinPath))
            File.Copy(profile.LocalSkinPath, Path.Combine(skinsDir, userName + ".png"), true);
        if (!string.IsNullOrWhiteSpace(profile.LocalCapePath) && File.Exists(profile.LocalCapePath))
            File.Copy(profile.LocalCapePath, Path.Combine(capesDir, userName + ".png"), true);

        return Task.CompletedTask;
    }

    /// <summary>Установлен ли CustomSkinLoader в папку mods сборки.</summary>
    public static bool IsCustomSkinLoaderInstalled(InstanceInfo instance) =>
        GetCustomSkinLoaderFile(instance) != null;

    public static string? GetCustomSkinLoaderFile(InstanceInfo instance)
    {
        var modsDir = Path.Combine(instance.Directory, "mods");
        if (!Directory.Exists(modsDir)) return null;

        try
        {
            return Directory.EnumerateFiles(modsDir, "*.jar")
                .Where(p => Path.GetFileName(p).Contains("customskinloader", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Устанавливает CustomSkinLoader, если его ещё нет, и гарантирует источники
    /// TLauncher/Ely.by/LocalSkin в CustomSkinLoader.json.
    /// </summary>
    public static async Task<string> EnsureCustomSkinLoaderAsync(InstanceInfo instance,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (instance.Loader == "vanilla")
            throw new InvalidOperationException(
                "CustomSkinLoader нельзя установить в ванильную сборку. Создайте сборку с Fabric, Quilt, Forge или NeoForge.");

        var existing = GetCustomSkinLoaderFile(instance);
        if (existing != null)
        {
            progress?.Report("CustomSkinLoader уже установлен — проверяем конфигурацию…");
            EnsureCslConfig(instance);
            return Path.GetFileName(existing);
        }

        progress?.Report("Установка CustomSkinLoader с Modrinth…");
        var file = await InstallCustomSkinLoaderAsync(instance, progress, ct);
        progress?.Report("Настройка источников скинов TLauncher/Ely.by…");
        EnsureCslConfig(instance);
        return file;
    }

    public static string CslConfigPath(InstanceInfo instance) =>
        Path.Combine(instance.Directory, "CustomSkinLoader", "CustomSkinLoader.json");

    /// <summary>
    /// Создаёт или дополняет CustomSkinLoader.json источниками TLauncher, Ely.by и LocalSkin.
    /// Существующие записи и настройки пользователя сохраняются.
    /// </summary>
    public static string EnsureCslConfig(InstanceInfo instance)
    {
        var rootDir = Path.Combine(instance.Directory, "CustomSkinLoader");
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(Path.Combine(rootDir, "LocalSkin", "skins"));
        Directory.CreateDirectory(Path.Combine(rootDir, "LocalSkin", "capes"));
        Directory.CreateDirectory(Path.Combine(rootDir, "LocalSkin", "elytras"));

        var path = CslConfigPath(instance);
        JsonObject config;

        if (File.Exists(path))
        {
            try
            {
                config = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                try
                {
                    File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                }
                catch { }
                config = new JsonObject();
            }
        }
        else
        {
            config = new JsonObject();
        }

        // Значения по умолчанию нужны только при создании нового файла.
        SetIfMissing(config, "version", "");
        SetIfMissing(config, "buildNumber", 0);
        SetIfMissing(config, "enableTransparentSkin", true);
        SetIfMissing(config, "forceLoadAllTextures", true);
        SetIfMissing(config, "enableCape", true);
        SetIfMissing(config, "threadPoolSize", 8);
        SetIfMissing(config, "enableLogStdOut", false);
        SetIfMissing(config, "cacheExpiry", 30);
        SetIfMissing(config, "forceUpdateSkull", false);
        SetIfMissing(config, "enableLocalProfileCache", false);
        SetIfMissing(config, "enableCacheAutoClean", false);
        SetIfMissing(config, "forceDisableCache", false);

        var loadlist = config["loadlist"] as JsonArray;
        if (loadlist == null)
        {
            loadlist = new JsonArray();
            config["loadlist"] = loadlist;
        }

        // Mojang — добавляем только при создании нового конфига.
        if (!HasEntry(loadlist, "Mojang", "MojangAPI"))
        {
            loadlist.Add(new JsonObject
            {
                ["name"] = "Mojang",
                ["type"] = "MojangAPI",
                ["apiRoot"] = "https://api.mojang.com/",
                ["sessionRoot"] = "https://sessionserver.mojang.com/"
            });
        }

        EnsureJsonApiEntry(loadlist, "ElyBy", "http://skinsystem.ely.by/textures/");
        EnsureJsonApiEntry(loadlist, "TLauncher", TLauncherTextureApi);
        EnsureLocalSkinEntry(loadlist);

        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static void EnsureJsonApiEntry(JsonArray loadlist, string name, string root)
    {
        var entry = FindEntry(loadlist, name);
        if (entry == null)
        {
            entry = new JsonObject { ["name"] = name };
            loadlist.Add(entry);
        }

        entry["type"] = "ElyByAPI";
        entry["root"] = root;
    }

    private static void EnsureLocalSkinEntry(JsonArray loadlist)
    {
        var entry = FindEntry(loadlist, "LocalSkin");
        if (entry == null)
        {
            entry = new JsonObject { ["name"] = "LocalSkin" };
            loadlist.Add(entry);
        }

        entry["type"] = "Legacy";
        SetIfMissing(entry, "checkPNG", false);
        SetIfMissing(entry, "model", "auto");
        SetIfMissing(entry, "skin", "LocalSkin/skins/{USERNAME}.png");
        SetIfMissing(entry, "cape", "LocalSkin/capes/{USERNAME}.png");
        SetIfMissing(entry, "elytra", "LocalSkin/elytras/{USERNAME}.png");
    }

    private static JsonObject? FindEntry(JsonArray loadlist, string name) =>
        loadlist.OfType<JsonObject>().FirstOrDefault(o =>
            string.Equals((string?)o["name"], name, StringComparison.OrdinalIgnoreCase));

    private static bool HasEntry(JsonArray loadlist, string name, string type) =>
        loadlist.OfType<JsonObject>().Any(o =>
            string.Equals((string?)o["name"], name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals((string?)o["type"], type, StringComparison.OrdinalIgnoreCase));

    private static void SetIfMissing(JsonObject obj, string key, JsonNode value)
    {
        if (!obj.ContainsKey(key) || obj[key] == null)
            obj[key] = value;
    }

    public static async Task<string> InstallCustomSkinLoaderAsync(InstanceInfo instance,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var file = await ModrinthService.InstallProjectAsync(instance, "customskinloader", progress, ct);
        EnsureCslConfig(instance);
        return file;
    }
}
