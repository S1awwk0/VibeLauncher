using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class LoaderService
{
    public static readonly string[] Loaders = { "vanilla", "fabric", "quilt", "forge", "neoforge" };

    private const string NeoForgeMaven = "https://maven.neoforged.net/releases";
    private const string NeoForgeMirror = "https://maven.creeperhost.net";

    public static string LoaderLabel(string loader) => loader switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        _ => "Vanilla"
    };

    // ───────────────────────────── FABRIC / QUILT ─────────────────────────────

    public static async Task<List<string>> GetFabricLoaderVersionsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await HttpService.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e =>
                {
                    var version = e.TryGetProperty("version", out var v) ? v.GetString() : null;
                    var stable = e.TryGetProperty("stable", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? s.GetBoolean()
                        : version != null && IsStableVersion(version);
                    return new { Version = version, Stable = stable };
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Version))
                .OrderByDescending(x => x.Stable)
                .Select(x => x.Version!)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static async Task<List<string>> GetQuiltLoaderVersionsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await HttpService.GetStringAsync("https://meta.quiltmc.org/v3/versions/loader", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("version", out var v) ? v.GetString() : null)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .OrderByDescending(IsStableVersion)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Отсеивает бета-, альфа- и прочие предварительные версии загрузчиков.</summary>
    public static bool IsStableVersion(string version) =>
        !Regex.IsMatch(version, @"(?i)(?:^|[-_.])(alpha|beta|rc|pre|preview|snapshot|dev|test)(?:[-_.]|\d|$)");

    public static async Task<string> InstallFabricAsync(string mcVersion, string? loaderVersion,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var versions = await GetFabricLoaderVersionsAsync(ct);
            loaderVersion = versions.FirstOrDefault()
                            ?? throw new InvalidOperationException("Не удалось получить список версий Fabric.");
        }

        progress?.Report(new ProgressInfo { Stage = "Fabric", Fraction = 0.05, Detail = "загрузка профиля" });
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/" +
                  $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var json = await HttpService.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString()
                 ?? $"fabric-loader-{loaderVersion}-{mcVersion}";

        var dir = Path.Combine(Paths.Versions, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".json");
        await File.WriteAllTextAsync(path, json, ct);

        await VersionService.InstallAsync(id, progress, ct);
        return id;
    }

    public static async Task<string> InstallQuiltAsync(string mcVersion, string? loaderVersion,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var versions = await GetQuiltLoaderVersionsAsync(ct);
            loaderVersion = versions.FirstOrDefault()
                            ?? throw new InvalidOperationException("Не удалось получить список версий Quilt.");
        }

        progress?.Report(new ProgressInfo { Stage = "Quilt", Fraction = 0.05, Detail = "загрузка профиля" });
        var url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}/" +
                  $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var json = await HttpService.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString()
                 ?? $"quilt-loader-{loaderVersion}-{mcVersion}";

        var dir = Path.Combine(Paths.Versions, id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), json, ct);

        await VersionService.InstallAsync(id, progress, ct);
        return id;
    }

    // ───────────────────────────── FORGE / NEOFORGE ─────────────────────────────

    public static async Task<List<string>> GetForgeVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        try
        {
            var xml = await HttpService.GetStringAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct);
            var doc = XDocument.Parse(xml);
            var prefix = mcVersion + "-";
            return doc.Descendants("version")
                .Select(e => e.Value)
                .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(v => v[prefix.Length..])
                .OrderByDescending(IsStableVersion)
                .ThenByDescending(v => v, VersionComparer.Instance)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static async Task<List<string>> GetNeoForgeVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        var (xml, _) = await GetNeoForgeMetadataAsync(ct);
        return ParseNeoForgeVersions(xml, mcVersion);
    }

    private static async Task<(string? Xml, bool FromMirror)> GetNeoForgeMetadataAsync(CancellationToken ct)
    {
        var primary = await GetWithTimeoutAsync(
            $"{NeoForgeMaven}/net/neoforged/neoforge/maven-metadata.xml", ct);
        if (!string.IsNullOrWhiteSpace(primary)) return (primary, false);

        var mirror = await GetWithTimeoutAsync(
            $"{NeoForgeMirror}/net/neoforged/neoforge/maven-metadata.xml", ct);
        return (mirror, true);
    }

    private static List<string> ParseNeoForgeVersions(string? xml, string mcVersion)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(xml)) return new List<string>();
            var parts = mcVersion.Split('.');
            if (parts.Length < 2) return new List<string>();
            var minor = parts[1];
            var patch = parts.Length > 2 ? parts[2] : "0";
            var prefix = $"{minor}.{patch}.";

            var doc = XDocument.Parse(xml);
            return doc.Descendants("version")
                .Select(e => e.Value)
                .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(IsStableVersion)
                .ThenByDescending(v => v, VersionComparer.Instance)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static async Task<string?> GetWithTimeoutAsync(string url, CancellationToken ct,
        int seconds = 15)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            return await HttpService.TryGetStringAsync(url, timeout.Token);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> InstallForgeAsync(string mcVersion, string? loaderVersion,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var versions = await GetForgeVersionsAsync(mcVersion, ct);
        if (versions.Count == 0)
            throw new InvalidOperationException($"Для Minecraft {mcVersion} не найдено версий Forge.");

        loaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? versions[0] : loaderVersion;
        var combined = $"{mcVersion}-{loaderVersion}";
        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{combined}/" +
                  $"forge-{combined}-installer.jar";
        return await RunInstallerAsync(url, "forge", mcVersion, loaderVersion, progress, ct);
    }

    public static async Task<string> InstallNeoForgeAsync(string mcVersion, string? loaderVersion,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        // NeoForge 1.20.1 публиковался под старым артефактом net.neoforged:forge
        if (mcVersion == "1.20.1")
        {
            var legacyUrl = $"{NeoForgeMaven}/net/neoforged/forge/{mcVersion}-{loaderVersion}/" +
                            $"forge-{mcVersion}-{loaderVersion}-installer.jar";
            var legacyMirror = $"{NeoForgeMirror}/net/neoforged/forge/{mcVersion}-{loaderVersion}/" +
                               $"forge-{mcVersion}-{loaderVersion}-installer.jar";
            return await RunInstallerAsync(legacyUrl, "neoforge", mcVersion, loaderVersion!, progress, ct,
                NeoForgeMirror, legacyMirror);
        }

        var (xml, fromMirror) = await GetNeoForgeMetadataAsync(ct);
        var versions = ParseNeoForgeVersions(xml, mcVersion);
        if (versions.Count == 0)
            throw new InvalidOperationException(
                $"Для Minecraft {mcVersion} не найдено версий NeoForge. NeoForge поддерживает 1.20.2+.");

        loaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? versions[0] : loaderVersion;
        var installer = $"net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
        var url = $"{NeoForgeMaven}/{installer}";
        var mirrorUrl = $"{NeoForgeMirror}/{installer}";
        return await RunInstallerAsync(url, "neoforge", mcVersion, loaderVersion, progress, ct,
            NeoForgeMirror, mirrorUrl, prefetchMirrorLibraries: fromMirror);
    }

    /// <summary>
    /// NeoForge installer упорно качает библиотеки с maven.neoforged.net и не использует
    /// параметр --mirror для URL. Если основной Maven недоступен, заранее кладём
    /// библиотеки из официального зеркала CreeperHost в локальный кэш.
    /// </summary>
    private static async Task PrefetchNeoForgeLibrariesAsync(string installerPath, string mirror,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        try
        {
            using var archive = ZipFile.OpenRead(installerPath);
            var entry = archive.GetEntry("install_profile.json");
            if (entry == null) return;

            using var stream = entry.Open();
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Array) return;

            var files = new List<(string Path, string Url, long Size)>();
            foreach (var library in libraries.EnumerateArray())
            {
                if (!library.TryGetProperty("downloads", out var downloads) ||
                    !downloads.TryGetProperty("artifact", out var artifact)) continue;
                if (!artifact.TryGetProperty("url", out var urlEl) ||
                    !artifact.TryGetProperty("path", out var pathEl)) continue;

                var url = urlEl.GetString();
                var relative = pathEl.GetString();
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(relative)) continue;
                if (!url.StartsWith("https://maven.neoforged.net/releases/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var size = artifact.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                var dest = Path.Combine(Paths.Libraries, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dest) && (size <= 0 || new FileInfo(dest).Length == size)) continue;

                files.Add((relative, mirror.TrimEnd('/') + "/" + relative, size));
            }

            if (files.Count == 0) return;

            var done = 0;
            var total = files.Count;
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = ct
            }, async (file, token) =>
            {
                try
                {
                    await HttpService.DownloadFileAsync(file.Url, Path.Combine(Paths.Libraries,
                        file.Path.Replace('/', Path.DirectorySeparatorChar)), null, file.Size, token);
                }
                catch
                {
                    // Если зеркало отстаёт, установщик попробует скачать файл сам.
                }

                var current = Interlocked.Increment(ref done);
                if (current == total || current % 10 == 0)
                    progress?.Report(new ProgressInfo
                    {
                        Stage = "NeoForge",
                        Fraction = 0.16 + 0.08 * current / Math.Max(1, total),
                        Detail = $"библиотеки с зеркала: {current}/{total}"
                    });
            });
        }
        catch
        {
            // Установщик всё равно попробует скачать нужные файлы самостоятельно.
        }
    }

    private static async Task<string> RunInstallerAsync(string url, string loader, string mcVersion,
        string loaderVersion, IProgress<ProgressInfo>? progress, CancellationToken ct,
        string? mirror = null, string? fallbackInstallerUrl = null, bool prefetchMirrorLibraries = false)
    {
        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        var installer = Path.Combine(Paths.Cache, fileName);

        progress?.Report(new ProgressInfo { Stage = LoaderLabel(loader), Fraction = 0.05, Detail = "скачивание установщика" });
        if (!File.Exists(installer))
        {
            var usedFallback = false;
            try
            {
                await HttpService.DownloadFileAsync(url, installer, null, 0, ct);
            }
            catch when (!string.IsNullOrWhiteSpace(fallbackInstallerUrl) && !ct.IsCancellationRequested)
            {
                usedFallback = true;
                await HttpService.DownloadFileAsync(fallbackInstallerUrl!, installer, null, 0, ct);
            }

            if (usedFallback) prefetchMirrorLibraries = true;
        }

        if (prefetchMirrorLibraries && !string.IsNullOrWhiteSpace(mirror))
            await PrefetchNeoForgeLibrariesAsync(installer, mirror, progress, ct);

        // Forge installer ищет launcher_profiles.json
        var profiles = Path.Combine(Paths.Root, "launcher_profiles.json");
        if (!File.Exists(profiles))
            await File.WriteAllTextAsync(profiles,
                "{\"profiles\":{},\"settings\":{},\"version\":3}", ct);

        var java = await JavaService.ResolveAsync(mcVersion, SettingsService.Current.JavaPath, true, progress, ct);

        progress?.Report(new ProgressInfo
        {
            Stage = LoaderLabel(loader),
            Fraction = 0.15,
            Detail = $"установка {loaderVersion} (может занять несколько минут)"
        });

        var started = DateTime.Now;
        var psi = new ProcessStartInfo
        {
            FileName = java.Path,
            WorkingDirectory = Paths.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(Paths.Root);
        if (!string.IsNullOrWhiteSpace(mirror))
        {
            psi.ArgumentList.Add("--mirror");
            psi.ArgumentList.Add(mirror);
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (output) output.AppendLine(e.Data); } };

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить установщик " + LoaderLabel(loader));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new InvalidOperationException("Установка " + LoaderLabel(loader) + " была прервана или заняла слишком много времени.");
        }

        var text = output.ToString();
        var id = FindInstalledVersion(mcVersion, loader, loaderVersion, started);
        if (process.ExitCode != 0 || id == null)
        {
            var tail = string.Join("\n", text.Split('\n').TakeLast(25));
            throw new InvalidOperationException(
                $"Установщик {LoaderLabel(loader)} завершился с кодом {process.ExitCode}.\n{tail}");
        }

        progress?.Report(new ProgressInfo { Stage = LoaderLabel(loader), Fraction = 0.6, Detail = "загрузка библиотек" });
        await VersionService.InstallAsync(id, progress, ct);
        return id;
    }

    private static string? FindInstalledVersion(string mcVersion, string loader, string loaderVersion,
        DateTime since)
    {
        try
        {
            if (!Directory.Exists(Paths.Versions)) return null;

            return Directory.EnumerateFiles(Paths.Versions, "*.json", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .Where(f => f.LastWriteTime >= since.AddMinutes(-5))
                .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f.Name);
                    if (loader == "neoforge")
                        return name.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                               name.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase);
                    return name.Contains("forge", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                           name.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public sealed class VersionComparer : IComparer<string>
    {
        public static VersionComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (x == null || y == null) return string.Compare(x, y, StringComparison.Ordinal);
            var px = Regex.Split(x, @"[.\-+]").Select(s => int.TryParse(s, out var n) ? n : -1).ToArray();
            var py = Regex.Split(y, @"[.\-+]").Select(s => int.TryParse(s, out var n) ? n : -1).ToArray();
            for (var i = 0; i < Math.Max(px.Length, py.Length); i++)
            {
                var a = i < px.Length ? px[i] : 0;
                var b = i < py.Length ? py[i] : 0;
                if (a != b) return a.CompareTo(b);
            }
            return string.Compare(x, y, StringComparison.Ordinal);
        }
    }
}
