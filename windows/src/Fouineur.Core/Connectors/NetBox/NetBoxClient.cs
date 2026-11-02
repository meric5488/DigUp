using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;

namespace Fouineur.Core.Connectors.NetBox;

public sealed class NetBoxException(int status, string message, string? path = null, NetBoxClient.CertificateInfo? certificate = null)
    : Exception($"NetBox {status}{(path is null ? "" : " sur « " + path + " »")} : {message}")
{
    public int Status { get; } = status;
    /// <summary>Certificat refusé (erreur 495), que l'utilisateur peut approuver.</summary>
    public NetBoxClient.CertificateInfo? Certificate { get; } = certificate;
}

/// <summary>
/// API REST de NetBox, en lecture seule : uniquement des GET. Jeton v2 (« nbt_… », NetBox 4.5+) envoyé en
/// « Bearer », jeton v1 en « Token ». Un jeton sans « Write enabled » suffit (et est recommandé).
/// </summary>
public sealed class NetBoxClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly string _prefix;

    /// <summary>Racine de NetBox (ex. https://netbox.exemple.fr), sans « /api ».</summary>
    public string BaseUrl { get; }
    public string ApiUrl => BaseUrl + "/api";
    public int PageSize { get; set; } = 500;

    /// <summary>
    /// Empreinte SHA-256 (hex) d'un certificat approuvé explicitement par l'utilisateur (certificat auto-signé ou
    /// d'une autorité interne absente du poste). Seul CE certificat est accepté en plus de ceux validés par Windows.
    /// </summary>
    public string? PinnedCertificate { get; }

    /// <summary>Dernier certificat refusé (pour proposer de l'approuver).</summary>
    public CertificateInfo? RejectedCertificate { get; private set; }

    public sealed record CertificateInfo(string Sha256, string Subject, string Issuer, DateTime NotAfter, SslPolicyErrors Errors, string ChainStatus, string Names = "")
    {
        public string Describe() =>
            $"certificat « {Subject} » émis par « {Issuer} », valable jusqu'au {NotAfter:dd/MM/yyyy}"
            + (Names.Length > 0 ? $", noms couverts : {Names}" : "")
            + $" — {Explain(Errors, ChainStatus)} — empreinte SHA-256 {Sha256}";
    }

    /// <summary>Noms DNS couverts par le certificat (extension SAN, sinon CN).</summary>
    private static string DnsNames(X509Certificate2 c)
    {
        var names = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        if (names.Count == 0 && c.GetNameInfo(X509NameType.DnsName, false) is { Length: > 0 } cn) names.Add(cn);
        return string.Join(", ", names.Distinct().Take(10));
    }

    public static string Explain(SslPolicyErrors e, string chain)
    {
        var parts = new List<string>();
        if (e.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) parts.Add("le nom du certificat ne correspond pas à l'adresse");
        if (e.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            parts.Add("autorité non reconnue par ce poste" + (chain.Length > 0 ? " (" + chain + ")" : ""));
        if (e.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) parts.Add("aucun certificat présenté");
        return parts.Count == 0 ? "refusé" : string.Join(", ", parts);
    }

    /// <summary>Validation : Windows d'abord ; sinon, seul le certificat épinglé (même empreinte exacte) passe.</summary>
    internal static bool Accept(SslPolicyErrors errors, string? sha256, string? pinned) =>
        errors == SslPolicyErrors.None
        || pinned is { Length: > 0 } && sha256 is not null && string.Equals(Normalize(pinned), sha256, StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string thumb) => NormalizeThumbprint(thumb);

    /// <summary>Garde les chiffres hexadécimaux (on accepte « AA:BB… », espaces, minuscules).</summary>
    public static string NormalizeThumbprint(string thumb) => new(thumb.Where(Uri.IsHexDigit).ToArray());

    public NetBoxClient(string url, ISecretStore secrets, string secretPrefix, HttpMessageHandler? handler = null, string? pinnedCertificate = null)
    {
        BaseUrl = NormalizeBase(url);
        _secrets = secrets; _prefix = secretPrefix;
        PinnedCertificate = string.IsNullOrWhiteSpace(pinnedCertificate) ? null : Normalize(pinnedCertificate);
        if (handler is null)
        {
            var h = new SocketsHttpHandler();
            h.SslOptions.RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
            {
                string? sha = cert is null ? null : Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
                if (Accept(errors, sha, PinnedCertificate)) return true;
                var status = chain is null ? "" : string.Join(", ", chain.ChainStatus.Select(c => c.StatusInformation.Trim()).Where(x => x.Length > 0).Distinct());
                RejectedCertificate = cert is null
                    ? new CertificateInfo("", "?", "?", DateTime.MinValue, errors, status)
                    : new CertificateInfo(sha!, cert.Subject, cert.Issuer, (cert as X509Certificate2)?.NotAfter ?? DateTime.Parse(cert.GetExpirationDateString()), errors, status,
                        cert is X509Certificate2 c2 ? DnsNames(c2) : "");
                return false;
            };
            _http = new HttpClient(h);
        }
        else _http = new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fouineur/2.0");
    }

    public string TokenKey => _prefix + ":token";

    public static string NormalizeBase(string url)
    {
        url = url.Trim();
        int i = url.IndexOf("/api", StringComparison.OrdinalIgnoreCase);
        if (i > 0 && (i + 4 == url.Length || url[i + 4] == '/')) url = url[..i];
        return url.TrimEnd('/');
    }

    /// <summary>« Bearer nbt_clé.secret » (v2) ou « Token secret » (v1).</summary>
    internal static string AuthHeader(string token)
    {
        token = token.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || token.StartsWith("Token ", StringComparison.OrdinalIgnoreCase)) return token;
        return token.StartsWith("nbt_", StringComparison.Ordinal) ? "Bearer " + token : "Token " + token;
    }

    /// <summary>GET d'un chemin relatif à /api/ ou d'une URL absolue (lien « next » de pagination).</summary>
    public async Task<JsonNode?> GetAsync(string pathOrUrl, CancellationToken ct)
    {
        var token = _secrets.Get(TokenKey);
        if (string.IsNullOrWhiteSpace(token)) throw new NetBoxException(401, "jeton d'API NetBox non renseigné");
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : ApiUrl + "/" + pathOrUrl.TrimStart('/');
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", AuthHeader(token));
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            HttpResponseMessage resp;
            RejectedCertificate = null;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException e) when (e.InnerException is AuthenticationException || RejectedCertificate is not null)
            {
                // Erreur de certificat : inutile de réessayer, on explique et on propose d'approuver ce certificat.
                throw new NetBoxException(495, RejectedCertificate is { } c
                    ? "connexion sécurisée refusée : " + c.Describe() + ". Si c'est bien votre serveur NetBox, cliquez sur « Approuver ce certificat »."
                    : "connexion sécurisée impossible : " + (e.InnerException?.Message ?? e.Message), Safe(url), RejectedCertificate);
            }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct); continue; }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    if (text.TrimStart().StartsWith('<'))
                        throw new NetBoxException((int)resp.StatusCode, "page HTML au lieu de JSON : vérifiez l'adresse (racine de NetBox, ex. https://netbox.exemple.fr).", Safe(url));
                    return text.Length == 0 ? null : JsonNode.Parse(text);
                }
                int code = (int)resp.StatusCode;
                if ((code == 429 || code >= 502) && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct); continue; }
                throw Error(resp.StatusCode, text, Safe(url));
            }
        }
    }

    /// <summary>Liste paginée (suit « next »). <paramref name="query"/> sans limit/offset.</summary>
    public async IAsyncEnumerable<JsonObject> ListAsync(string endpoint, string query, [EnumeratorCancellation] CancellationToken ct)
    {
        string? next = $"{endpoint.Trim('/')}/?limit={PageSize}" + (query.Length > 0 ? "&" + query : "");
        while (next is not null)
        {
            var json = await GetAsync(next, ct) as JsonObject;
            foreach (var o in (json?["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) yield return o;
            next = json?["next"] is JsonValue v && v.TryGetValue<string>(out var n) && n.Length > 0 ? n : null;
            // Derrière un proxy, « next » peut annoncer http:// ou un autre hôte : on garde notre adresse.
            if (next is not null && Uri.TryCreate(next, UriKind.Absolute, out var u) && u.PathAndQuery.IndexOf("/api/", StringComparison.Ordinal) is var i and >= 0)
                next = u.PathAndQuery[(i + 5)..];
        }
    }

    private static string Safe(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.PathAndQuery : url;

    internal static NetBoxException Error(HttpStatusCode status, string body, string? path)
    {
        string message = body;
        try
        {
            if (JsonNode.Parse(body) is JsonObject o && o["detail"] is JsonNode d) message = d.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            if (body.TrimStart().StartsWith('<')) message = "page d'erreur HTML";
        }
        if (message.Length > 300) message = message[..300];
        return new NetBoxException((int)status, (int)status switch
        {
            401 => "jeton d'API refusé (" + message + ") : vérifiez-le dans les réglages de la source NetBox.",
            403 => "accès refusé (" + message + ") : le jeton ou l'utilisateur n'a pas le droit de lecture sur cet objet.",
            404 => "introuvable (" + message + ") : vérifiez l'adresse de NetBox, ou ce module n'existe pas dans cette version.",
            _ => message,
        }, path);
    }

    public void Dispose() => _http.Dispose();
}
