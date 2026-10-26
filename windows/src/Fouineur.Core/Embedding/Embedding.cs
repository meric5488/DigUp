using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fouineur.Core.Index;

namespace Fouineur.Core.Embedding;

/// <summary>Transforme des textes en vecteurs normalisés (cosinus = produit scalaire).</summary>
public interface IEmbedder
{
    /// <summary>Identifiant stable (modèle + dimension) : s'il change, l'index est revectorisé.</summary>
    string ModelId { get; }
    int Dimensions { get; }
    bool IsReady { get; }
    Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<(string Title, string Text)> docs, CancellationToken ct);
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct);
}

/// <summary>Pas de modèle : recherche texte seule.</summary>
public sealed class NoEmbedder : IEmbedder
{
    public static readonly NoEmbedder Instance = new();
    public string ModelId => "none";
    public int Dimensions => 0;
    public bool IsReady => false;
    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<(string Title, string Text)> docs, CancellationToken ct) => Task.FromResult(Array.Empty<float[]>());
    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) => Task.FromResult(Array.Empty<float>());
}

public static class EmbeddingGemmaPrompts
{
    // Gabarits recommandés pour EmbeddingGemma (recherche).
    public static string Query(string q) => $"task: search result | query: {q}";
    public static string Document(string title, string text) => $"title: {(string.IsNullOrWhiteSpace(title) ? "none" : title)} | text: {text}";

    /// <summary>Tronque (Matryoshka) puis renormalise.</summary>
    public static float[] Truncate(ReadOnlySpan<float> v, int dims)
    {
        int n = dims <= 0 || dims > v.Length ? v.Length : dims;
        var r = v[..n].ToArray();
        double norm = 0;
        foreach (var x in r) norm += x * x;
        norm = Math.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < r.Length; i++) r[i] = (float)(r[i] / norm);
        return r;
    }
}

/// <summary>Modèle servi par Ollama (http://localhost:11434), par exemple « embeddinggemma ».</summary>
public sealed class OllamaEmbedder : IEmbedder, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _dims;
    private bool? _ready;

    public OllamaEmbedder(string baseUrl, string model, int dims, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(5);
        _model = model; _dims = dims;
    }

    public string ModelId => $"ollama:{_model}:{_dims}";
    public int Dimensions => _dims;
    public bool IsReady => _ready ?? true;

    public async Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<(string Title, string Text)> docs, CancellationToken ct)
        => await EmbedAsync(docs.Select(d => EmbeddingGemmaPrompts.Document(d.Title, d.Text)).ToList(), ct);

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
        => (await EmbedAsync(new[] { EmbeddingGemmaPrompts.Query(query) }, ct))[0];

    private async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = _model, ["input"] = new JsonArray(inputs.Select(i => (JsonNode)i!).ToArray()), ["truncate"] = true };
        using var resp = await _http.PostAsJsonAsync("api/embed", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _ready = false;
            throw new InvalidOperationException($"Ollama : {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        }
        _ready = true;
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var arr = json?["embeddings"] as JsonArray ?? throw new InvalidOperationException("Réponse Ollama inattendue.");
        return arr.Select(e => EmbeddingGemmaPrompts.Truncate(((JsonArray)e!).Select(x => x!.GetValue<float>()).ToArray(), _dims)).ToArray();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Découpe un texte en passages qui se chevauchent, coupés sur des espaces.</summary>
public static class Chunker
{
    public static List<(int Start, int Length)> Split(string text, int size = 1200, int overlap = 150, int maxChunks = 24)
    {
        var list = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(text)) { list.Add((0, 0)); return list; }
        int pos = 0;
        while (pos < text.Length && list.Count < maxChunks)
        {
            int end = Math.Min(text.Length, pos + size);
            if (end < text.Length)
            {
                int cut = text.LastIndexOfAny(new[] { '\n', '.', ' ' }, end - 1, Math.Min(end - pos, 200));
                if (cut > pos + size / 2) end = cut + 1;
            }
            list.Add((pos, end - pos));
            if (end >= text.Length) break;
            pos = Math.Max(end - overlap, pos + 1);
            while (pos < text.Length && !char.IsWhiteSpace(text[pos - 1]) && pos < end) pos++;
        }
        return list;
    }
}

/// <summary>Vectorise en arrière-plan les éléments marqués « à faire ».</summary>
public sealed class EmbeddingWorker
{
    private readonly IndexStore _store;
    private readonly IEmbedder _embedder;

    public EmbeddingWorker(IndexStore store, IEmbedder embedder) { _store = store; _embedder = embedder; }

    /// <summary>Si le modèle a changé depuis la dernière fois, remet tous les vecteurs à zéro.</summary>
    public void EnsureModel()
    {
        if (!_embedder.IsReady && _embedder is NoEmbedder) return;
        var prev = _store.GetMeta("embed_model");
        if (prev != _embedder.ModelId)
        {
            _store.ResetEmbeddings();
            _store.SetMeta("embed_model", _embedder.ModelId);
        }
    }

    public async Task<int> RunAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct, int batchItems = 16)
    {
        if (_embedder is NoEmbedder) return 0;
        EnsureModel();
        int done = 0;
        int total = _store.PendingEmbeddingCount();
        while (!ct.IsCancellationRequested)
        {
            var batch = _store.GetPendingEmbeddings(batchItems);
            if (batch.Count == 0) break;
            var inputs = new List<(string, string)>();
            var spans = new List<(int ItemIndex, int Start, int Length)>();
            for (int i = 0; i < batch.Count; i++)
            {
                foreach (var (s, l) in Chunker.Split(batch[i].Body))
                {
                    inputs.Add((batch[i].Title, l == 0 ? batch[i].Title : batch[i].Body.Substring(s, l)));
                    spans.Add((i, s, l));
                }
            }
            var vecs = await _embedder.EmbedDocumentsAsync(inputs, ct);
            for (int i = 0; i < batch.Count; i++)
            {
                var chunks = spans.Select((sp, k) => (sp, k)).Where(x => x.sp.ItemIndex == i)
                    .Select(x => (x.sp.Start, x.sp.Length, vecs[x.k])).ToList();
                _store.SaveEmbeddings(batch[i].RowId, chunks);
            }
            done += batch.Count;
            progress?.Report((done, Math.Max(total, done)));
        }
        return done;
    }
}
