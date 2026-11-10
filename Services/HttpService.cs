using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VibeLauncher.Services;

public static class HttpService
{
    public const string UserAgent = "VibeLauncher/1.0 (Minecraft launcher; +https://github.com/vibe-launcher)";

    public static HttpClient Client { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static async Task<T> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        var json = await GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidOperationException("Пустой ответ API: " + url);
    }

    public static async Task<JsonDocument> GetJsonDocumentAsync(string url, CancellationToken ct = default)
    {
        var json = await GetStringAsync(url, ct);
        return JsonDocument.Parse(json);
    }

    public static async Task<JsonDocument?> TryGetJsonDocumentAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await Client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<T?> TryGetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await Client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return default;
            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return default;
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    public static async Task<string?> TryGetStringAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await Client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Скачивание файла с докачкой, повторами и атомарной заменой.</summary>
    public static async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress = null,
        long expectedSize = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Пустой URL загрузки", nameof(url));

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".part";
        Exception? last = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var total = expectedSize > 0 ? expectedSize : response.Content.Headers.ContentLength ?? 0;
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read,
                                 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long readTotal = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        readTotal += read;
                        if (total > 0) progress?.Report((double)readTotal / total);
                    }
                }

                if (expectedSize > 0)
                {
                    var size = new FileInfo(tmp).Length;
                    if (size != expectedSize)
                        throw new IOException($"Неверный размер файла: {size} вместо {expectedSize}");
                }

                if (File.Exists(destination)) File.Delete(destination);
                File.Move(tmp, destination);
                progress?.Report(1);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (attempt < 3) await Task.Delay(700 * attempt, ct);
            }
        }

        throw new IOException($"Не удалось скачать {url}: {last?.Message}", last);
    }

    public static string Combine(params string[] parts) =>
        string.Join("/", parts.Select(p => p.Trim('/')));

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public static StringContent Form(Dictionary<string, string> values) =>
        new(string.Join("&", values.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")),
            Encoding.UTF8, "application/x-www-form-urlencoded");
}
