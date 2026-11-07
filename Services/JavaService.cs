using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public static class JavaService
{
    private static readonly string[] ComponentOrder = { "jre-legacy", "java-runtime-alpha", "java-runtime-beta", "java-runtime-gamma", "java-runtime-delta" };

    public static int RecommendedMajor(string mcVersion)
    {
        var m = Regex.Match(mcVersion, @"^1\.(\d+)(?:\.(\d+))?");
        if (m.Success)
        {
            var minor = int.Parse(m.Groups[1].Value);
            var patch = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            if (minor >= 21 || (minor == 20 && patch >= 5)) return 21;
            if (minor >= 18) return 17;
            if (minor >= 17) return 16;
            return 8;
        }

        var snap = Regex.Match(mcVersion, @"^(\d{2})w");
        if (snap.Success)
        {
            var year = int.Parse(snap.Groups[1].Value);
            if (year >= 24) return 21;
            return 17;
        }

        // Новые версии Minecraft (26.x и выше) используют Java 21+
        var majorText = new string(mcVersion.TakeWhile(char.IsDigit).ToArray());
        if (int.TryParse(majorText, out var versionMajor) && versionMajor >= 2) return 21;

        return 17;
    }

    public static async Task<List<JavaInfoVm>> DetectAsync(CancellationToken ct = default)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    candidates.Add(Path.GetFullPath(path));
            }
            catch { }
        }

        Add(SettingsService.Current.JavaPath);
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) Add(Path.Combine(javaHome, "bin", "java.exe"));

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (!string.IsNullOrWhiteSpace(p)) Add(Path.Combine(p.Trim('"'), "java.exe"));
        }

        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"),
            Paths.Java,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages",
                "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"),
        };

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories))
                {
                    candidates.Add(file);
                    if (candidates.Count > 80) break;
                }
            }
            catch { }
        }

        var result = new List<JavaInfoVm>();
        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var info = await GetInfoAsync(path, ct);
            if (info != null && result.All(r => !r.Path.Equals(info.Path, StringComparison.OrdinalIgnoreCase)))
                result.Add(info);
        }

        return result.OrderByDescending(j => j.Major).ThenByDescending(j => j.Version).ToList();
    }

    public static async Task<JavaInfoVm?> GetInfoAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            var outTask = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } return null; }

            var text = await errorTask + "\n" + await outTask;
            var match = Regex.Match(text, @"version\s+""([^""]+)""");
            if (!match.Success) match = Regex.Match(text, @"openjdk\s+([0-9][^\s]*)");
            if (!match.Success) return null;

            var version = match.Groups[1].Value;
            var major = ParseMajor(version);
            if (major <= 0) return null;
            return new JavaInfoVm { Path = path, Major = major, Version = version };
        }
        catch
        {
            return null;
        }
    }

    private static int ParseMajor(string version)
    {
        if (version.StartsWith("1.", StringComparison.Ordinal))
        {
            var parts = version.Split('.', '_');
            return parts.Length > 1 && int.TryParse(parts[1], out var j8) ? j8 : 8;
        }
        var first = new string(version.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(first, out var major) ? major : 0;
    }

    public static async Task<JavaInfoVm> ResolveAsync(string mcVersion, string? preferredPath,
        bool allowDownload, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var recommended = RecommendedMajor(mcVersion);
        var javas = await DetectAsync(ct);

        JavaInfoVm? preferred = null;
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            preferred = await GetInfoAsync(preferredPath, ct);

        var exact = javas.Where(j => j.Major == recommended).OrderByDescending(j => j.Version).FirstOrDefault();

        // Пользовательская Java новее рекомендуемой может ломать загрузчики модов
        // (например, NeoForge 21.4 на Java 25). Если рекомендованная версия есть,
        // выбираем её; иначе используем выбранную пользователем.
        if (preferred != null && preferred.Major >= recommended)
        {
            if (exact != null && preferred.Major != recommended) return exact;
            return preferred;
        }

        if (exact != null) return exact;

        var newer = javas.Where(j => j.Major > recommended).OrderBy(j => j.Major).FirstOrDefault();
        if (newer != null) return newer;

        if (preferred != null) return preferred;

        if (javas.Count > 0 && recommended <= 8) return javas.OrderByDescending(j => j.Major).First();

        if (allowDownload)
        {
            var path = await DownloadRuntimeAsync(recommended, progress, ct);
            var info = await GetInfoAsync(path, ct);
            if (info != null) return info;
        }

        throw new InvalidOperationException(
            $"Не найдена Java {recommended}. Установите её или укажите путь в настройках лаунчера. " +
            $"Для Minecraft {mcVersion} рекомендуется Java {recommended}.");
    }

    public static async Task<string> DownloadRuntimeAsync(int major, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var component = major switch
        {
            <= 8 => "jre-legacy",
            9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 => "java-runtime-alpha",
            17 or 18 or 19 or 20 => "java-runtime-gamma",
            _ => "java-runtime-delta"
        };

        progress?.Report(new ProgressInfo { Stage = "Java", Fraction = 0.01, Detail = "получение списка файлов" });
        using var all = await HttpService.GetJsonDocumentAsync(
            "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json", ct);

        if (!all.RootElement.TryGetProperty("windows-x64", out var windows) ||
            !windows.TryGetProperty(component, out var entries) || entries.GetArrayLength() == 0)
            throw new InvalidOperationException($"Mojang не предоставляет runtime {component} для Windows x64.");

        var manifestUrl = entries[0].GetProperty("manifest").GetProperty("url").GetString()
                          ?? throw new InvalidOperationException("Нет URL манифеста Java.");
        var versionName = entries[0].TryGetProperty("version", out var ver) &&
                          ver.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? component : component;

        using var manifest = await HttpService.GetJsonDocumentAsync(manifestUrl, ct);
        var targetDir = Path.Combine(Paths.Java, $"{component}-{versionName}");
        Directory.CreateDirectory(targetDir);

        var files = new List<(string Path, string Url, long Size)>();
        foreach (var prop in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            if (!prop.Value.TryGetProperty("type", out var type) || type.GetString() != "file") continue;
            if (!prop.Value.TryGetProperty("downloads", out var downloads) ||
                !downloads.TryGetProperty("raw", out var raw)) continue;
            var url = raw.GetProperty("url").GetString();
            if (string.IsNullOrEmpty(url)) continue;
            var size = raw.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            files.Add((prop.Name.Replace('/', Path.DirectorySeparatorChar), url, size));
        }

        var done = 0;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct };
        await Parallel.ForEachAsync(files, parallel, async (file, token) =>
        {
            var dest = Path.Combine(targetDir, file.Path);
            if (!File.Exists(dest) || new FileInfo(dest).Length != file.Size)
                await HttpService.DownloadFileAsync(file.Url, dest, null, file.Size, token);
            var current = Interlocked.Increment(ref done);
            if (current % 5 == 0 || current == files.Count)
                progress?.Report(new ProgressInfo
                {
                    Stage = "Java",
                    Fraction = (double)current / Math.Max(1, files.Count),
                    Detail = $"скачано {current}/{files.Count} файлов"
                });
        });

        var javaExe = Path.Combine(targetDir, "bin", "java.exe");
        if (!File.Exists(javaExe))
        {
            javaExe = Directory.EnumerateFiles(targetDir, "java.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException("Java скачана, но java.exe не найден.");
        }

        progress?.Report(new ProgressInfo { Stage = "Java", Fraction = 1, Detail = "готово" });
        return javaExe;
    }
}
