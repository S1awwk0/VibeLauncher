using System.Text.Json.Serialization;

namespace VibeLauncher.Models;

public sealed class LauncherSettings
{
    public int RamMb { get; set; } = 4096;
    public string JvmArgs { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string MicrosoftClientId { get; set; } = "";
    public bool ShowSnapshots { get; set; }
    public bool CloseOnLaunch { get; set; }
    public bool Fullscreen { get; set; }
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public string SelectedVersion { get; set; } = "";
    public string SelectedInstance { get; set; } = "";
    public string? SelectedAccountId { get; set; }
    public string SkinProvider { get; set; } = "Mojang";
    public string SkinNickname { get; set; } = "";
    public string SkinModel { get; set; } = "classic";
    public string LocalSkinPath { get; set; } = "";
    public string LocalCapePath { get; set; } = "";
    public List<Account> Accounts { get; set; } = new();
}

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>microsoft | offline</summary>
    public string Type { get; set; } = "offline";
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; } = DateTime.MinValue;
    public string SkinModel { get; set; } = "classic";

    [JsonIgnore]
    public bool IsMicrosoft => Type.Equals("microsoft", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string TypeLabel => IsMicrosoft ? "Microsoft" : "Офлайн";

    public string Initial => string.IsNullOrEmpty(Username) ? "?" : Username[..1].ToUpperInvariant();
}

public sealed class VersionManifest
{
    public LatestInfo Latest { get; set; } = new();
    public List<VersionEntry> Versions { get; set; } = new();
}

public sealed class LatestInfo
{
    public string Release { get; set; } = "";
    public string Snapshot { get; set; } = "";
}

public sealed class VersionEntry
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public string Url { get; set; } = "";
    public DateTime ReleaseTime { get; set; }
    public string? Sha1 { get; set; }

    public string TypeLabel => Type switch
    {
        "release" => "Релиз",
        "snapshot" => "Снапшот",
        "old_beta" => "Бета",
        "old_alpha" => "Альфа",
        _ => Type
    };
}

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public OsRule? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    public string? Name { get; set; }
    public string? Arch { get; set; }
    public string? Version { get; set; }
}

public sealed class ArgumentItem
{
    public string? Single { get; set; }
    public List<string>? Multiple { get; set; }
    public List<Rule>? Rules { get; set; }

    public IEnumerable<string> Values => Single != null ? new[] { Single } : Multiple ?? new List<string>();
}

public sealed class VersionArguments
{
    public List<ArgumentItem> Game { get; set; } = new();
    public List<ArgumentItem> Jvm { get; set; } = new();
}

public sealed class DownloadInfo
{
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Path { get; set; }
}

public sealed class AssetIndexInfo
{
    public string Id { get; set; } = "";
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
}

public sealed class LibraryDownloads
{
    public DownloadInfo? Artifact { get; set; }
    public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

public sealed class ExtractRule
{
    public List<string>? Exclude { get; set; }
}

public sealed class Library
{
    public string Name { get; set; } = "";
    /// <summary>Базовый URL Maven-репозитория (Fabric/Quilt указывают его вместо downloads).</summary>
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public LibraryDownloads? Downloads { get; set; }
    public List<Rule>? Rules { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public ExtractRule? Extract { get; set; }
}

public sealed class JavaVersionInfo
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class LoggingFile
{
    public string? Id { get; set; }
    public string? Url { get; set; }
    public DownloadInfo? File { get; set; }
    public string? Argument { get; set; }
    public string? Type { get; set; }
}

public sealed class LoggingConfig
{
    public LoggingFile? Client { get; set; }
}

public sealed class VersionDetails
{
    public string Id { get; set; } = "";
    public string? InheritsFrom { get; set; }
    public string? MainClass { get; set; }
    public string? Assets { get; set; }
    public string? Type { get; set; }
    public string? MinecraftArguments { get; set; }
    public ArgumentList Arguments { get; set; } = new();
    public AssetIndexInfo? AssetIndex { get; set; }
    public Dictionary<string, DownloadInfo>? Downloads { get; set; }
    public List<Library> Libraries { get; set; } = new();
    public JavaVersionInfo? JavaVersion { get; set; }
    public LoggingConfig? Logging { get; set; }
    public string? ReleaseTime { get; set; }

    public sealed class ArgumentList
    {
        public List<ArgumentItem> Game { get; set; } = new();
        public List<ArgumentItem> Jvm { get; set; } = new();
    }
}

public sealed class ProgressInfo
{
    public string Stage { get; init; } = "";
    public double Fraction { get; init; }
    public string Detail { get; init; } = "";

    public override string ToString() =>
        string.IsNullOrEmpty(Detail) ? Stage : $"{Stage}: {Detail}";
}

public sealed class LaunchResult
{
    public int ExitCode { get; set; }
    public string LogPath { get; set; } = "";
    public List<CrashFinding> Findings { get; set; } = new();
    public string VersionId { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public bool Cancelled { get; set; }
}

public sealed class CrashFinding
{
    public string Severity { get; set; } = "warning";
    public string Title { get; set; } = "";
    public string Cause { get; set; } = "";
    public string Fix { get; set; } = "";
    public string? Evidence { get; set; }
    public string SeverityLabel => Severity switch
    {
        "critical" => "Критично",
        "warning" => "Внимание",
        _ => "Инфо"
    };

    [JsonIgnore]
    public string SeverityColor => Severity switch
    {
        "critical" => "#F87171",
        "warning" => "#FBBF24",
        _ => "#22D3EE"
    };
}

public sealed class InstanceInfo
{
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "vanilla";
    public string LoaderVersion { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string Directory { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string LoaderLabel => Loader switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        _ => "Vanilla"
    };

    [JsonIgnore]
    public string Subtitle => $"{McVersion} · {LoaderLabel}" +
        (string.IsNullOrWhiteSpace(LoaderVersion) ? "" : $" {LoaderVersion}");

    public override string ToString() => Name;
}

public sealed class InstalledVersionVm
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";

    public string Subtitle => string.IsNullOrEmpty(Kind) ? "Minecraft" : Kind;
}

public sealed class LaunchTarget
{
    public string Display { get; set; } = "";
    public string Key { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string GameDir { get; set; } = "";
    public bool IsInstance { get; set; }

    public override string ToString() => Display;
}

public sealed class ModrinthSearchResult
{
    [JsonPropertyName("hits")] public List<ModrinthHit> Hits { get; set; } = new();
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public sealed class ModrinthHit
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();

    [JsonIgnore]
    public string DownloadsText => Downloads >= 1_000_000
        ? $"{Downloads / 1_000_000.0:0.#} млн"
        : Downloads >= 1000 ? $"{Downloads / 1000.0:0.#} тыс." : Downloads.ToString();

    [JsonIgnore]
    public string CategoriesText => string.Join(" · ", Categories.Take(3));
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "release";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("dependency_type")] public string? DependencyType { get; set; }
}

public sealed class ModrinthFile
{
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = new();
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class ModFileVm
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string SizeText { get; set; } = "";
}

public sealed class SkinProfile
{
    public string Provider { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string? SkinUrl { get; set; }
    public string? CapeUrl { get; set; }
    public string Model { get; set; } = "classic";
    public string? LocalSkinPath { get; set; }
    public string? LocalCapePath { get; set; }
}

public sealed class LogEntry
{
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Modified { get; set; }
    public long Size { get; set; }

    public string Subtitle => $"{Modified:dd.MM.yyyy HH:mm} · {Size / 1024.0:0.#} КБ";
}

public sealed class JavaInfoVm
{
    public string Path { get; set; } = "";
    public int Major { get; set; }
    public string Version { get; set; } = "";

    public string Display => $"Java {Major}  ·  {Version}";

    public override string ToString() => Display;
}
