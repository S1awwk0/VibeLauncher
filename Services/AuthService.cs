using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeLauncher.Models;

namespace VibeLauncher.Services;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, string Message);

public static class AuthService
{
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    public static async Task<Account> LoginMicrosoftAsync(string clientId, Func<DeviceCodeInfo, Task> showCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Не указан Azure Client ID. Добавьте его в настройках лаунчера.");

        var deviceResponse = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "XboxLive.signin offline_access"
        }, ct);

        using (deviceResponse.Document)
        {
            var root = deviceResponse.Document.RootElement;
            if (!deviceResponse.Success)
                throw new InvalidOperationException("Не удалось начать вход Microsoft: " +
                    (GetError(root) ?? deviceResponse.Raw));

            var deviceCode = root.GetProperty("device_code").GetString()!;
            var userCode = root.GetProperty("user_code").GetString()!;
            var verificationUri = root.TryGetProperty("verification_uri", out var vu)
                ? vu.GetString()!
                : "https://microsoft.com/devicelogin";
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
            var interval = root.TryGetProperty("interval", out var intEl) ? intEl.GetInt32() : 5;
            var expiresIn = root.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 900;

            await showCode(new DeviceCodeInfo(userCode, verificationUri, message));

            var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, interval)), ct);

                var tokenResponse = await PostFormAsync(TokenUrl, new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId,
                    ["device_code"] = deviceCode
                }, ct);

                using (tokenResponse.Document)
                {
                    var tokenRoot = tokenResponse.Document.RootElement;
                    if (tokenResponse.Success)
                    {
                        var accessToken = tokenRoot.GetProperty("access_token").GetString()!;
                        var refreshToken = tokenRoot.TryGetProperty("refresh_token", out var rt)
                            ? rt.GetString() ?? ""
                            : "";

                        return await ExchangeForMinecraftAsync(accessToken, refreshToken, clientId, ct);
                    }

                    var error = tokenRoot.TryGetProperty("error", out var err) ? err.GetString() : null;
                    switch (error)
                    {
                        case "authorization_pending":
                            continue;
                        case "slow_down":
                            interval += 5;
                            continue;
                        case "authorization_declined":
                            throw new OperationCanceledException("Вход Microsoft отклонён пользователем.");
                        case "expired_token":
                            throw new InvalidOperationException("Код входа Microsoft истёк. Попробуйте снова.");
                        default:
                            throw new InvalidOperationException("Ошибка входа Microsoft: " +
                                (GetError(tokenRoot) ?? error ?? "неизвестная ошибка"));
                    }
                }
            }
        }

        throw new InvalidOperationException("Время ожидания входа Microsoft истекло.");
    }

    public static Account CreateOffline(string username)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Введите никнейм.");
        if (username.Length < 3 || username.Length > 16)
            throw new InvalidOperationException("Никнейм должен содержать от 3 до 16 символов.");
        if (username.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new InvalidOperationException("Никнейм может содержать только буквы, цифры и «_».");

        return new Account
        {
            Type = "offline",
            Username = username,
            Uuid = OfflineUuid(username),
            AccessToken = Guid.NewGuid().ToString("N"),
            ExpiresAtUtc = DateTime.MaxValue,
            SkinModel = "classic"
        };
    }

    public static string OfflineUuid(string username)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        return new Guid(bytes).ToString("N");
    }

    public static async Task EnsureValidAsync(Account account, CancellationToken ct = default)
    {
        if (!account.IsMicrosoft) return;
        if (account.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2)) return;

        var clientId = SettingsService.Current.MicrosoftClientId;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(account.RefreshToken))
            throw new InvalidOperationException("Сессия Microsoft истекла. Войдите в аккаунт заново.");

        var response = await PostFormAsync(TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = account.RefreshToken,
            ["scope"] = "XboxLive.signin offline_access"
        }, ct);

        using (response.Document)
        {
            if (!response.Success)
                throw new InvalidOperationException("Не удалось обновить сессию Microsoft. Войдите заново. " +
                    (GetError(response.Document.RootElement) ?? ""));

            var root = response.Document.RootElement;
            var accessToken = root.GetProperty("access_token").GetString()!;
            var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : account.RefreshToken;

            var updated = await ExchangeForMinecraftAsync(accessToken, refresh, clientId, ct);
            account.AccessToken = updated.AccessToken;
            account.RefreshToken = updated.RefreshToken;
            account.ExpiresAtUtc = updated.ExpiresAtUtc;
            account.Username = string.IsNullOrWhiteSpace(updated.Username) ? account.Username : updated.Username;
            account.Uuid = string.IsNullOrWhiteSpace(updated.Uuid) ? account.Uuid : updated.Uuid;
            SettingsService.Save();
        }
    }

    private static async Task<Account> ExchangeForMinecraftAsync(string msAccessToken, string refreshToken,
        string clientId, CancellationToken ct)
    {
        // 1. Xbox Live
        var xblResponse = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msAccessToken
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        }, ct);

        using (xblResponse.Document)
        {
            if (!xblResponse.Success)
                throw new InvalidOperationException("Xbox Live отклонил вход: " + xblResponse.Raw);

            var root = xblResponse.Document.RootElement;
            var xblToken = root.GetProperty("Token").GetString()!;
            var uhs = root.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;

            // 2. XSTS
            var xstsResponse = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["SandboxId"] = "RETAIL",
                    ["UserTokens"] = new JsonArray(xblToken)
                },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT"
            }, ct);

            using (xstsResponse.Document)
            {
                var xstsRoot = xstsResponse.Document.RootElement;
                if (!xstsResponse.Success)
                {
                    var xerr = xstsRoot.TryGetProperty("XErr", out var x) ? x.ToString() : "";
                    var reason = xerr switch
                    {
                        "2148916233" => "У аккаунта Microsoft нет профиля Xbox. Создайте его на xbox.com.",
                        "2148916238" => "Аккаунт несовершеннолетний и не добавлен в семейную группу Microsoft.",
                        "2148916235" => "Xbox Live недоступен в регионе аккаунта.",
                        "2148916237" => "Аккаунт Xbox заблокирован для сетевых функций.",
                        "2148916236" => "Требуется проверка возраста аккаунта.",
                        _ => "XSTS ошибка " + xerr
                    };
                    throw new InvalidOperationException(reason);
                }

                var xstsToken = xstsRoot.GetProperty("Token").GetString()!;
                var xstsUhs = xstsRoot.GetProperty("DisplayClaims").GetProperty("xui")[0]
                    .GetProperty("uhs").GetString()!;
                _ = uhs;

                // 3. Minecraft
                var mcResponse = await PostJsonAsync(
                    "https://api.minecraftservices.com/authentication/login_with_xbox", new JsonObject
                    {
                        ["identityToken"] = $"XBL3.0 x={xstsUhs};{xstsToken}"
                    }, ct);

                using (mcResponse.Document)
                {
                    if (!mcResponse.Success)
                        throw new InvalidOperationException("Minecraft Services отклонили вход: " + mcResponse.Raw);

                    var mcRoot = mcResponse.Document.RootElement;
                    var mcToken = mcRoot.GetProperty("access_token").GetString()!;
                    var expiresIn = mcRoot.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 86400;

                    // 4. Профиль
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        "https://api.minecraftservices.com/minecraft/profile");
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
                    using var profileResponse = await HttpService.Client.SendAsync(request, ct);
                    var profileRaw = await profileResponse.Content.ReadAsStringAsync(ct);

                    if (!profileResponse.IsSuccessStatusCode)
                    {
                        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
                            throw new InvalidOperationException(
                                "На аккаунте Microsoft нет купленной Minecraft: Java Edition.");
                        throw new InvalidOperationException("Не удалось получить профиль Minecraft: " + profileRaw);
                    }

                    using var profile = JsonDocument.Parse(profileRaw);
                    var profileRoot = profile.RootElement;
                    var name = profileRoot.GetProperty("name").GetString()!;
                    var uuid = profileRoot.GetProperty("id").GetString()!;

                    return new Account
                    {
                        Type = "microsoft",
                        Username = name,
                        Uuid = uuid,
                        AccessToken = mcToken,
                        RefreshToken = refreshToken,
                        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 120)),
                        SkinModel = "classic"
                    };
                }
            }
        }
    }

    private static async Task<(bool Success, JsonDocument Document, string Raw)> PostFormAsync(string url,
        Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = HttpService.Form(form);
        using var response = await HttpService.Client.PostAsync(url, content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, ParseSafe(raw), raw);
    }

    private static async Task<(bool Success, JsonDocument Document, string Raw)> PostJsonAsync(string url,
        JsonObject body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await HttpService.Client.PostAsync(url, content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, ParseSafe(raw), raw);
    }

    private static JsonDocument ParseSafe(string raw)
    {
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? GetError(JsonElement root)
    {
        if (root.TryGetProperty("error_description", out var desc)) return desc.GetString();
        if (root.TryGetProperty("error", out var err)) return err.GetString();
        if (root.TryGetProperty("message", out var msg)) return msg.GetString();
        return null;
    }
}
