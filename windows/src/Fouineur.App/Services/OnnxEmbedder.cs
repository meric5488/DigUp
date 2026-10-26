using System.IO;
using Fouineur.Core.Embedding;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Fouineur.App.Services;

/// <summary>
/// EmbeddingGemma (export ONNX de onnx-community/embeddinggemma-300m-ONNX), exécuté localement
/// avec ONNX Runtime ; DirectML (GPU) si disponible, sinon processeur.
/// Le dossier doit contenir un fichier .onnx (avec son .onnx_data éventuel) et tokenizer.model.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;
    private readonly int _dims;
    private readonly object _runLock = new();
    private readonly HashSet<string> _inputs;
    private readonly string _modelName;
    public int MaxTokens { get; set; } = 512;
    public string Device { get; }

    public OnnxEmbedder(string modelDir, int dims, bool useDirectML)
    {
        var onnx = FindModel(modelDir) ?? throw new FileNotFoundException("Aucun fichier .onnx dans le dossier du modèle.", modelDir);
        var tok = Directory.EnumerateFiles(modelDir, "tokenizer.model", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new FileNotFoundException("tokenizer.model introuvable dans le dossier du modèle.", modelDir);
        _modelName = Path.GetFileNameWithoutExtension(onnx);
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        string device = "processeur";
        if (useDirectML)
        {
            try
            {
                opts.EnableMemoryPattern = false;
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opts.AppendExecutionProvider_DML(0);
                device = "GPU (DirectML)";
            }
            catch (Exception e) { Infrastructure.Logger.Warn($"DirectML indisponible, calcul sur le processeur ({e.Message})."); }
        }
        _session = new InferenceSession(onnx, opts);
        Device = device;
        _inputs = _session.InputMetadata.Keys.ToHashSet();
        using (var s = File.OpenRead(tok))
            _tokenizer = LlamaTokenizer.Create(s, addBeginOfSentence: true, addEndOfSentence: true);
        _dims = dims;
    }

    private static string? FindModel(string dir)
    {
        var all = Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories).ToList();
        // préférence : modèle complet, puis quantifié
        foreach (var name in new[] { "model.onnx", "model_fp16.onnx", "model_quantized.onnx", "model_q4.onnx" })
        {
            var f = all.FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (f is not null) return f;
        }
        return all.FirstOrDefault();
    }

    public string ModelId => $"onnx:embeddinggemma:{_modelName}:{_dims}";
    public int Dimensions => _dims;
    public bool IsReady => true;

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<(string Title, string Text)> docs, CancellationToken ct) =>
        Task.Run(() => Embed(docs.Select(d => EmbeddingGemmaPrompts.Document(d.Title, d.Text)).ToList(), ct), ct);

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) =>
        (await Task.Run(() => Embed(new[] { EmbeddingGemmaPrompts.Query(query) }, ct), ct))[0];

    private float[][] Embed(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new float[texts.Count][];
        const int batch = 8;
        for (int start = 0; start < texts.Count; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            var slice = texts.Skip(start).Take(batch).ToList();
            var ids = slice.Select(t =>
            {
                var tokens = _tokenizer.EncodeToIds(t);
                return tokens.Count > MaxTokens ? tokens.Take(MaxTokens - 1).Append(tokens[^1]).ToList() : tokens.ToList();
            }).ToList();
            int len = Math.Max(1, ids.Max(x => x.Count));
            var inputIds = new DenseTensor<long>(new[] { slice.Count, len });
            var mask = new DenseTensor<long>(new[] { slice.Count, len });
            for (int i = 0; i < ids.Count; i++)
                for (int j = 0; j < ids[i].Count; j++) { inputIds[i, j] = ids[i][j]; mask[i, j] = 1; }

            var feeds = new List<NamedOnnxValue>();
            if (_inputs.Contains("input_ids")) feeds.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIds));
            if (_inputs.Contains("attention_mask")) feeds.Add(NamedOnnxValue.CreateFromTensor("attention_mask", mask));
            if (_inputs.Contains("token_type_ids")) feeds.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new[] { slice.Count, len })));

            lock (_runLock)
            {
                using var outputs = _session.Run(feeds);
                var pooled = outputs.FirstOrDefault(o => o.Name == "sentence_embedding");
                if (pooled is not null)
                {
                    var t = pooled.AsTensor<float>();
                    int dim = t.Dimensions[1];
                    for (int i = 0; i < slice.Count; i++)
                    {
                        var v = new float[dim];
                        for (int k = 0; k < dim; k++) v[k] = t[i, k];
                        result[start + i] = EmbeddingGemmaPrompts.Truncate(v, _dims);
                    }
                }
                else
                {
                    // Repli : moyenne des états cachés pondérée par le masque.
                    var h = outputs.First().AsTensor<float>();
                    int dim = h.Dimensions[2];
                    for (int i = 0; i < slice.Count; i++)
                    {
                        var v = new float[dim];
                        int n = ids[i].Count;
                        for (int j = 0; j < n; j++) for (int k = 0; k < dim; k++) v[k] += h[i, j, k];
                        for (int k = 0; k < dim; k++) v[k] /= Math.Max(1, n);
                        result[start + i] = EmbeddingGemmaPrompts.Truncate(v, _dims);
                    }
                }
            }
        }
        return result;
    }

    public void Dispose() => _session.Dispose();
}
