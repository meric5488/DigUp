using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;

namespace Fouineur.Core.Connectors.Glpi;

public sealed class GlpiException(int status, string code, string message, string? path = null)
    : Exception($"GLPI {status} {code}{(path is null ? "" : " sur « " + path + " »")} : {message}")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Path { get; } = path;
}

/// <summary>
/// API REST « classique » de GLPI (apirest.php), présente en 9.5, 10.x et 11.x.
/// Session ouverte avec le jeton d'API de l'utilisateur (+ App-Token facultatif), fermée en fin de synchro.
/// </summary>
public sealed class GlpiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly string _prefix;
    private string? _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Adresse de GLPI (ex. https://glpi.exemple.fr), sans « /apirest.php ».</summary>
    public string BaseUrl { get; }
    public string ApiUrl => BaseUrl + "/apirest.php";
    public int PageSize { get; set; } = 200;

    public GlpiClient(string url, ISecretStore secrets, string secretPrefix, HttpMessageHandler? handler = null)
    {
        BaseUrl = NormalizeBase(url);
        _secrets = secrets; _prefix = secretPrefix;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fouineur/2.0");
    }

    /// <summary>
    /// Ramène l'adresse saisie à la racine de GLPI : on accepte « …/apirest.php », mais aussi l'URL de la
    /// nouvelle API (« …/api.php/v2.x ») ou d'une page (« …/front/central.php »).
    /// </summary>
    public static string NormalizeBase(string url)
    {
        url = url.Trim();
        foreach (var marker in new[] { "/apirest.php", "/api.php", "/front/", "/index.php" })
        {
            int i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) { url = url[..i]; break; }
        }
        return url.TrimEnd('/');
    }

    /// <summary>
    /// Certaines installations (GLPI 11, API v2 activée) interprètent tout en-tête « Authorization » comme un
    /// jeton OAuth : on passe alors le jeton utilisateur en paramètre d'initSession, comme le permet l'API.
    /// </summary>
    private bool _tokenInQuery;

    /// <summary>
    /// Idem pour le jeton de session : certains frontaux (GLPI 11 via api.php/v1, proxy inverse) perdent l'en-tête
    /// « Session-Token ». On le passe alors en paramètre (jeton temporaire, fermé en fin de synchro).
    /// </summary>
    private bool _sessionInQuery;

    public string UserTokenKey => _prefix + ":usertoken";
    public string AppTokenKey => _prefix + ":apptoken";

    private string? AppToken => _secrets.Get(AppTokenKey) is { Length: > 0 } t ? t : null;

    public async Task<string> EnsureSessionAsync(CancellationToken ct, bool force = false)
    {
        if (_session is not null && !force) return _session;
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is not null && !force) return _session;
            var userToken = _secrets.Get(UserTokenKey);
            if (string.IsNullOrWhiteSpace(userToken)) throw new GlpiException(401, "ERROR_LOGIN_PARAMETERS_MISSING", "jeton d'API GLPI non renseigné");
            HttpResponseMessage resp;
            string text;
            while (true)
            {
                // Mode « paramètres » : exactement comme un navigateur (aucun en-tête d'authentification), car
                // certains GLPI 11 plantent (500) dès qu'un en-tête Authorization ou App-Token est présent.
                var url = _tokenInQuery
                    ? ApiUrl + "/initSession?user_token=" + Uri.EscapeDataString(userToken.Trim()) + AppTokenParam("&")
                    : ApiUrl + "/initSession?get_full_session=false";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!_tokenInQuery)
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "user_token " + userToken.Trim());
                    if (AppToken is { } app) req.Headers.TryAddWithoutValidation("App-Token", app);
                }
                resp = await _http.SendAsync(req, ct);
                text = await resp.Content.ReadAsStringAsync(ct);
                // En-tête refusé (OAuth) ou serveur en erreur (500) : on réessaie avec le jeton en paramètre.
                if (!resp.IsSuccessStatusCode && !_tokenInQuery && (IsOAuthRejection(text) || (int)resp.StatusCode >= 500))
                {
                    resp.Dispose();
                    _tokenInQuery = true;
                    _sessionInQuery = true;
                    continue;
                }
                break;
            }
            using var _r = resp;
            if (!resp.IsSuccessStatusCode) throw Error(resp.StatusCode, text, "initSession");
            var token = (JsonNode.Parse(text) as JsonObject)?["session_token"]?.GetValue<string>()
                        ?? throw new GlpiException((int)resp.StatusCode, "NO_SESSION", "réponse sans session_token (adresse de l'API ?)");
            return _session = token;
        }
        finally { _gate.Release(); }
    }

    public async Task KillSessionAsync()
    {
        if (_session is null) return;
        try
        {
            using var req = Request(HttpMethod.Get, "killSession", _session);
            using var _ = await _http.SendAsync(req);
        }
        catch (Exception) { /* sans importance */ }
        _session = null;
    }

    private HttpRequestMessage Request(HttpMethod m, string path, string session)
    {
        var url = ApiUrl + "/" + path;
        if (_sessionInQuery)
        {
            var sep = path.Contains('?') ? "&" : "?";
            url += sep + "session_token=" + Uri.EscapeDataString(session) + AppTokenParam("&");
        }
        var req = new HttpRequestMessage(m, url);
        if (!_sessionInQuery)
        {
            req.Headers.TryAddWithoutValidation("Session-Token", session);
            if (AppToken is { } app) req.Headers.TryAddWithoutValidation("App-Token", app);
        }
        return req;
    }

    /// <summary>Résumé pour le journal : adresse, longueur des jetons (jamais leur valeur), mode d'envoi.</summary>
    public string Describe()
    {
        var u = _secrets.Get(UserTokenKey)?.Trim() ?? "";
        var a = AppToken?.Trim() ?? "";
        return $"adresse {ApiUrl}, jeton utilisateur {(u.Length == 0 ? "absent" : u.Length + " caractères")}, "
             + $"App-Token {(a.Length == 0 ? "absent" : a.Length + " caractères")}, jetons en {(_tokenInQuery ? "paramètres" : "en-têtes")}";
    }

    private string AppTokenParam(string sep) => AppToken is { } app ? sep + "app_token=" + Uri.EscapeDataString(app) : "";

    /// <summary>Mode utilisé (pour les tests et le journal).</summary>
    internal bool ParameterMode => _tokenInQuery;

    /// <summary>GET relatif à apirest.php ; renvoie le JSON et la plage totale (Content-Range).</summary>
    public async Task<(JsonNode? Json, int? Total)> GetAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var session = await EnsureSessionAsync(ct);
            using var req = Request(HttpMethod.Get, path, session);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct); continue; }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    int? total = null;
                    if (resp.Content.Headers.TryGetValues("Content-Range", out var cr) || resp.Headers.TryGetValues("Content-Range", out cr))
                    {
                        var v = cr.FirstOrDefault() ?? "";
                        int slash = v.LastIndexOf('/');
                        if (slash >= 0 && int.TryParse(v[(slash + 1)..], out var t)) total = t;
                    }
                    return (text.Length == 0 ? null : JsonNode.Parse(text), total);
                }
                var err = Error(resp.StatusCode, text, SafePath(path));
                if (err.Code == "ERROR_SESSION_TOKEN_INVALID" && attempt == 0) { _session = null; continue; }
                if (err.Code == "ERROR_SESSION_TOKEN_MISSING" && !_sessionInQuery) { _sessionInQuery = true; continue; }
                // Plage vide : GLPI répond 400 ERROR_RANGE_EXCEED_TOTAL quand on dépasse la fin.
                if (err.Code == "ERROR_RANGE_EXCEED_TOTAL") return (new JsonArray(), 0);
                // 500 = erreur PHP de GLPI, en général reproductible : une seule nouvelle tentative. 502/503/504 : passerelle, on insiste.
                int code = (int)resp.StatusCode;
                if (code >= 500 && attempt < (code == 500 ? 1 : 3)) { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct); continue; }
                throw err;
            }
        }
    }

    /// <summary>Parcourt une liste d'objets page par page. <paramref name="query"/> sans « range ».</summary>
    public async IAsyncEnumerable<JsonObject> ListAsync(string itemtype, string query, [EnumeratorCancellation] CancellationToken ct)
    {
        int start = 0;
        while (true)
        {
            var sep = query.Length > 0 ? "&" : "";
            var (json, total) = await GetAsync($"{itemtype}?{query}{sep}range={start}-{start + PageSize - 1}", ct);
            var arr = json as JsonArray ?? new JsonArray();
            foreach (var o in arr.OfType<JsonObject>()) yield return o;
            start += PageSize;
            if (arr.Count < PageSize || (total is { } t && start >= t)) yield break;
        }
    }

    /// <summary>Chemin sans aucun jeton, pour les messages et le journal.</summary>
    internal static string SafePath(string path)
    {
        int q = path.IndexOf('?');
        return q < 0 ? path : path[..q] + "?" + string.Join("&", path[(q + 1)..].Split('&')
            .Where(p => !p.StartsWith("session_token=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("user_token=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("app_token=", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Page d'erreur HTML de GLPI → titre et texte utile, courts.</summary>
    internal static string HtmlErrorText(string body)
    {
        var title = System.Text.RegularExpressions.Regex.Match(body, @"<title>\s*(.*?)\s*</title>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
        var main = System.Text.RegularExpressions.Regex.Replace(body, @"<(script|style|head)[^>]*>.*?</\1>", " ", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        main = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(main, "<[^>]+>", " "));
        main = System.Text.RegularExpressions.Regex.Replace(main, @"\s+", " ").Trim();
        var text = string.Join(" — ", new[] { title, main }.Where(x => x.Length > 0).Distinct());
        if (text.Length > 240) text = text[..240] + "…";
        return text + " (détail dans les journaux du serveur GLPI : files/_log/php-errors.log)";
    }

    private static bool IsOAuthRejection(string body) =>
        body.Contains("OAuth", StringComparison.OrdinalIgnoreCase) || body.Contains("JWT", StringComparison.OrdinalIgnoreCase);

    internal static GlpiException Error(HttpStatusCode status, string body, string? path = null)
    {
        string code = status.ToString(), message = body;
        if (body.TrimStart().StartsWith('<')) message = HtmlErrorText(body);
        else try
        {
            var json = JsonNode.Parse(body);
            // API classique : ["ERROR_CODE", "message"]
            if (json is JsonArray a && a.Count > 0)
            {
                code = a[0]?.ToString() ?? code;
                message = a.Count > 1 ? a[1]?.ToString() ?? "" : "";
            }
            // Nouvelle API (v2) : {"status":"ERROR_…","title":"…","detail":"…"}
            else if (json is JsonObject o && o["status"] is JsonValue)
            {
                code = o["status"]!.ToString();
                message = string.Join(" — ", new[] { o["title"]?.ToString(), o["detail"]?.ToString() }.Where(x => !string.IsNullOrEmpty(x)));
                if (IsOAuthRejection(body))
                    message = "le serveur a répondu avec la nouvelle API OAuth (v2) au lieu de l'API REST classique. Vérifiez l'adresse "
                              + "(racine de GLPI, ex. https://glpi.exemple.fr) et que « API REST » est activée dans Configuration › Générale › API. (" + message + ")";
            }
        }
        catch (System.Text.Json.JsonException) { }
        if (message.Length > 300) message = message[..300];
        return new GlpiException((int)status, code, code switch
        {
            "ERROR_LOGIN_PARAMETERS_MISSING" or "ERROR_GLPI_LOGIN_USER_TOKEN" => "jeton d'API refusé : vérifiez-le dans les réglages de la source GLPI.",
            "ERROR_WRONG_APP_TOKEN_PARAMETER" or "ERROR_APP_TOKEN_PARAMETERS_MISSING" => "App-Token refusé ou exigé par GLPI (Configuration › Générale › API).",
            "ERROR_NOT_ALLOWED_IP" => "l'adresse IP de ce poste n'est pas autorisée pour ce client API dans GLPI.",
            "ERROR_API_DISABLED" or "ERROR_LOGIN_WITH_CREDENTIALS_DISABLED" => "API REST désactivée dans GLPI (Configuration › Générale › API).",
            _ => message,
        }, path);
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
