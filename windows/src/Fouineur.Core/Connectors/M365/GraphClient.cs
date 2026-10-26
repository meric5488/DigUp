using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Fouineur.Core.Connectors.M365;

/// <summary>Fournit un jeton d'accès Graph délégué (MSAL + WAM côté Windows).</summary>
public interface ITokenProvider
{
    Task<string> GetTokenAsync(bool interactive, CancellationToken ct);
    string? AccountName { get; }
    Task SignOutAsync();
}

public sealed class GraphException(HttpStatusCode status, string code, string message)
    : Exception($"Graph {(int)status} {code} : {message}")
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Client Graph minimal : pagination, reprises sur 429/503, lecture JSON.</summary>
public sealed class GraphClient : IDisposable
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0/";
    public static readonly string[] Scopes =
    {
        "User.Read", "Files.Read.All", "Sites.Read.All",
        "Team.ReadBasic.All", "Channel.ReadBasic.All", "ChannelMessage.Read.All", "Chat.Read",
    };

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;

    public GraphClient(ITokenProvider tokens, HttpMessageHandler? handler = null)
    {
        _tokens = tokens;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fouineur/2.0");
    }

    public async Task<JsonObject> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, url, ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return node as JsonObject ?? new JsonObject();
    }

    /// <summary>Énumère toutes les pages (@odata.nextLink). Si <paramref name="onDeltaLink"/> est fourni, reçoit le @odata.deltaLink final.</summary>
    public async IAsyncEnumerable<JsonObject> GetPagedAsync(string url, [EnumeratorCancellation] CancellationToken ct, Action<string>? onDeltaLink = null, int maxPages = int.MaxValue)
    {
        string? next = url;
        int pages = 0;
        while (next is not null && pages++ < maxPages)
        {
            var page = await GetAsync(next, ct);
            if (page["value"] is JsonArray arr)
                foreach (var v in arr)
                    if (v is JsonObject o) yield return o;
            next = page["@odata.nextLink"]?.GetValue<string>();
            var delta = page["@odata.deltaLink"]?.GetValue<string>();
            if (delta is not null) onDeltaLink?.Invoke(delta);
        }
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct, long maxBytes = long.MaxValue)
    {
        using var resp = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("Fichier trop volumineux.");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task DownloadToFileAsync(string url, string path, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        var tmp = path + ".part";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs, ct);
        File.Move(tmp, path, true);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct, HttpCompletionOption opt = HttpCompletionOption.ResponseContentRead)
    {
        bool retriedAuth = false;
        for (int attempt = 0; ; attempt++)
        {
            var token = await _tokens.GetTokenAsync(false, ct);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, opt, ct); }
            catch (HttpRequestException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                continue;
            }
            if (resp.IsSuccessStatusCode) return resp;

            if ((resp.StatusCode == (HttpStatusCode)429 || resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.BadGateway) && attempt < 6)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, 2 << attempt));
                resp.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized && !retriedAuth)
            {
                retriedAuth = true;
                resp.Dispose();
                await _tokens.GetTokenAsync(true, ct);
                continue;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            var status = resp.StatusCode;
            resp.Dispose();
            string code = status.ToString(), message = body;
            try
            {
                var err = JsonNode.Parse(body)?["error"];
                code = err?["code"]?.GetValue<string>() ?? code;
                message = err?["message"]?.GetValue<string>() ?? message;
            }
            catch { /* corps non JSON */ }
            throw new GraphException(status, code, message);
        }
    }

    public void Dispose() => _http.Dispose();
}

internal static class J
{
    public static string S(this JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    public static string? Sn(this JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static long L(this JsonNode? n, string key) =>
        n?[key] is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<string>(out var s) && long.TryParse(s, out l) ? l : 0) : 0;
    public static DateTimeOffset D(this JsonNode? n, string key) =>
        DateTimeOffset.TryParse(n.S(key), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.MinValue;
    public static bool Has(this JsonNode? n, string key) => n is JsonObject o && o.ContainsKey(key) && o[key] is not null;
}
