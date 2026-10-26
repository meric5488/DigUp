using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;

namespace Fouineur.Core.Connectors.Zimbra;

public sealed class ZimbraException(string code, string message) : Exception($"Zimbra {code} : {message}")
{
    public string Code { get; } = code;
}

/// <summary>
/// Client SOAP (format JSON) pour Zimbra 8.8 → 10.x.
/// Les comptes délégués sont lus avec le jeton de l'utilisateur, en nommant le compte cible dans le contexte.
/// </summary>
public sealed class ZimbraClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly string _secretPrefix;
    private readonly Func<CancellationToken, Task<string?>>? _askTwoFactorCode;
    private string? _token;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public string BaseUrl { get; }
    public string UserName { get; }

    public ZimbraClient(string baseUrl, string userName, ISecretStore secrets, string secretPrefix,
        Func<CancellationToken, Task<string?>>? askTwoFactorCode = null, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        UserName = userName;
        _secrets = secrets; _secretPrefix = secretPrefix;
        _askTwoFactorCode = askTwoFactorCode;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fouineur/2.0");
    }

    public string PasswordKey => _secretPrefix + ":password";
    private string TokenKey => _secretPrefix + ":token";

    public void SetPassword(string? password)
    {
        _secrets.Set(PasswordKey, password);
        _secrets.Set(TokenKey, null);
        _token = null;
    }

    public string? AuthToken => _token;

    /// <summary>Authentification (avec double facteur si le compte l'exige). Le jeton est conservé chiffré.</summary>
    public async Task<string> EnsureAuthAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _token is not null) return _token;
        await _authLock.WaitAsync(ct);
        try
        {
            if (!force && _token is not null) return _token;
            if (!force)
            {
                var saved = _secrets.Get(TokenKey);
                if (saved is not null)
                {
                    var parts = saved.Split('|', 2);
                    if (parts.Length == 2 && long.TryParse(parts[0], out var exp) && DateTimeOffset.FromUnixTimeSeconds(exp) > DateTimeOffset.UtcNow.AddMinutes(5))
                        return _token = parts[1];
                }
            }
            var password = _secrets.Get(PasswordKey) ?? throw new ZimbraException("auth.NO_PASSWORD", "mot de passe non renseigné");
            var req = new JsonObject
            {
                ["_jsns"] = "urn:zimbraAccount",
                ["account"] = new JsonObject { ["by"] = "name", ["_content"] = UserName },
                ["password"] = new JsonObject { ["_content"] = password },
            };
            var resp = await CallAsync("AuthRequest", req, account: null, ct, auth: false);
            if (resp["twoFactorAuthRequired"] is JsonNode tf && Truthy(tf))
            {
                if (_askTwoFactorCode is null) throw new ZimbraException("auth.TWO_FACTOR_REQUIRED", "code de double authentification requis");
                var temp = FirstContent(resp["authToken"]) ?? throw new ZimbraException("auth.FAILED", "jeton temporaire absent");
                var code = await _askTwoFactorCode(ct);
                if (string.IsNullOrWhiteSpace(code)) throw new ZimbraException("auth.TWO_FACTOR_REQUIRED", "code de double authentification non saisi");
                var req2 = new JsonObject
                {
                    ["_jsns"] = "urn:zimbraAccount",
                    ["account"] = new JsonObject { ["by"] = "name", ["_content"] = UserName },
                    ["authToken"] = new JsonObject { ["_content"] = temp, ["verifyAccount"] = "1" },
                    ["twoFactorCode"] = new JsonObject { ["_content"] = code.Trim() },
                    ["trustedDevice"] = new JsonObject { ["_content"] = "1" },
                };
                resp = await CallAsync("AuthRequest", req2, account: null, ct, auth: false);
            }
            var token = FirstContent(resp["authToken"]) ?? throw new ZimbraException("auth.FAILED", "jeton absent de la réponse");
            long lifetimeMs = FirstLong(resp["lifetime"]) ?? 12 * 3600 * 1000L;
            var expiry = DateTimeOffset.UtcNow.AddMilliseconds(lifetimeMs).ToUnixTimeSeconds();
            _secrets.Set(TokenKey, $"{expiry}|{token}");
            return _token = token;
        }
        finally { _authLock.Release(); }
    }

    private static bool Truthy(JsonNode n)
    {
        var s = n is JsonObject o ? o["_content"]?.ToString() : n.ToString();
        return s is "true" or "1" or "True";
    }

    internal static string? FirstContent(JsonNode? n) => n switch
    {
        null => null,
        JsonArray a => a.Count > 0 ? FirstContent(a[0]) : null,
        JsonObject o => o["_content"]?.ToString(),
        _ => n.ToString(),
    };

    private static long? FirstLong(JsonNode? n)
    {
        var s = FirstContent(n);
        return long.TryParse(s, out var l) ? l : null;
    }

    /// <summary>Appelle « {name} » et renvoie le corps de « {name sans Request}Response ».</summary>
    public async Task<JsonObject> CallAsync(string requestName, JsonObject request, string? account, CancellationToken ct, bool auth = true)
    {
        for (int attempt = 0; ; attempt++)
        {
            var context = new JsonObject
            {
                ["_jsns"] = "urn:zimbra",
                ["userAgent"] = new JsonObject { ["name"] = "Fouineur", ["version"] = "2.0" },
                ["format"] = new JsonObject { ["type"] = "js" },
            };
            if (auth) context["authToken"] = await EnsureAuthAsync(ct);
            if (account is not null && !account.Equals(UserName, StringComparison.OrdinalIgnoreCase))
                context["account"] = new JsonObject { ["by"] = "name", ["_content"] = account };

            var env = new JsonObject
            {
                ["Header"] = new JsonObject { ["context"] = context },
                ["Body"] = new JsonObject { [requestName] = request.DeepClone() },
            };
            using var content = new StringContent(env.ToJsonString(), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try { resp = await _http.PostAsync(BaseUrl + "/service/soap/" + requestName, content, ct); }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                continue;
            }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                JsonNode? root;
                try { root = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException)
                {
                    throw new ZimbraException(((int)resp.StatusCode).ToString(), "réponse non JSON (adresse du serveur ?)");
                }
                var body = root?["Body"] as JsonObject;
                if (body?["Fault"] is JsonObject fault)
                {
                    var code = fault["Detail"]?["Error"]?["Code"]?.ToString() ?? "service.FAILURE";
                    var reason = fault["Reason"]?["Text"]?.ToString() ?? text;
                    if (auth && attempt == 0 && code is "service.AUTH_EXPIRED" or "service.AUTH_REQUIRED")
                    {
                        _token = null; _secrets.Set(TokenKey, null);
                        continue;
                    }
                    throw new ZimbraException(code, reason);
                }
                var respName = requestName.EndsWith("Request") ? requestName[..^7] + "Response" : requestName + "Response";
                return body?[respName] as JsonObject ?? new JsonObject();
            }
        }
    }

    /// <summary>Message brut (MIME), via l'URL REST /service/home.</summary>
    public async Task<byte[]> DownloadRawAsync(string? account, string messageId, CancellationToken ct, bool retried = false)
    {
        var token = await EnsureAuthAsync(ct);
        var who = account is null || account.Equals(UserName, StringComparison.OrdinalIgnoreCase) ? "~" : Uri.EscapeDataString(account);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/service/home/{who}/?id={Uri.EscapeDataString(messageId)}");
        req.Headers.Add("Cookie", "ZM_AUTH_TOKEN=" + token);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized && !retried)
        {
            await EnsureAuthAsync(ct, force: true);
            return await DownloadRawAsync(account, messageId, ct, retried: true);
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose() { _http.Dispose(); _authLock.Dispose(); }
}
