using System.Text;
using System.Text.Json;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class SkinService
{
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
        // Конфигурацию мода не перезаписываем: в ней уже есть LocalSkin, Mojang и Ely.by.
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

    public static async Task<string> InstallCustomSkinLoaderAsync(InstanceInfo instance,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await ModrinthService.InstallProjectAsync(instance, "customskinloader", progress, ct);
    }
}
