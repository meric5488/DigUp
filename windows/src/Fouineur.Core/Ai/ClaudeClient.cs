using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Fouineur.Core.Ai;

public sealed class ClaudeException(int status, string type, string message) : Exception($"Claude ({status} {type}) : {message}")
{
    public int Status { get; } = status;
    public string Type { get; } = type;
}

/// <summary>
/// Client minimal de l'API Messages d'Anthropic (https://api.anthropic.com/v1/messages) :
/// appel simple et réponse en flux (SSE), reprises sur 429/529/5xx.
/// </summary>
public sealed class ClaudeClient : IDisposable
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;

    public ClaudeClient(Func<string?> apiKey, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // les réponses en flux peuvent durer ; on annule par jeton
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey());

    /// <summary>Appel non diffusé ; renvoie le message complet.</summary>
    public async Task<JsonObject> CreateAsync(JsonObject body, CancellationToken ct)
    {
        body["stream"] = false;
        using var resp = await SendAsync(body, HttpCompletionOption.ResponseContentRead, ct);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject();
    }

    /// <summary>Appel diffusé : renvoie les morceaux de texte au fur et à mesure.</summary>
    public async IAsyncEnumerable<string> StreamTextAsync(JsonObject body, [EnumeratorCancellation] CancellationToken ct)
    {
        body["stream"] = true;
        using var resp = await SendAsync(body, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line[5..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;
            JsonNode? ev;
            try { ev = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { continue; }
            var type = ev?["type"]?.GetValue<string>();
            if (type == "content_block_delta" && ev!["delta"]?["type"]?.GetValue<string>() == "text_delta")
            {
                var text = ev["delta"]?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text)) yield return text;
            }
            else if (type == "error")
            {
                throw new ClaudeException(0, ev!["error"]?["type"]?.GetValue<string>() ?? "error",
                    ev["error"]?["message"]?.GetValue<string>() ?? json);
            }
            else if (type == "message_stop") yield break;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject body, HttpCompletionOption opt, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new ClaudeException(401, "authentication_error", "clé d'API Anthropic non renseignée (Sources › Claude).");
        var payload = body.ToJsonString();
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", key.Trim());
            req.Headers.Add("anthropic-version", ApiVersion);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Fouineur", "2.0"));
            var resp = await _http.SendAsync(req, opt, ct);
            if (resp.IsSuccessStatusCode) return resp;

            int status = (int)resp.StatusCode;
            if ((status is 429 or 529 or 500 or 502 or 503) && attempt < 4)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30, 2 << attempt));
                resp.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }
            var text = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            string type = "error", message = text;
            try
            {
                var err = JsonNode.Parse(text)?["error"];
                type = err?["type"]?.GetValue<string>() ?? type;
                message = err?["message"]?.GetValue<string>() ?? message;
            }
            catch (System.Text.Json.JsonException) { }
            throw new ClaudeException(status, type, status switch
            {
                401 => "clé d'API refusée. Vérifiez-la dans Sources › Claude.",
                403 => "accès refusé pour cette clé ou ce modèle. " + message,
                404 => "modèle introuvable. Vérifiez le nom du modèle dans Sources › Claude. " + message,
                _ => message,
            });
        }
    }

    public void Dispose() => _http.Dispose();
}
