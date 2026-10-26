using Fouineur.Core.Configuration;
using Fouineur.Core.Connectors;
using Fouineur.Core.Embedding;
using Fouineur.Core.Extraction;
using Fouineur.Core.Index;
using Fouineur.Core.Model;

namespace Fouineur.Core.Sync;

public enum SyncPhase { Idle, Syncing, Embedding }

public sealed record SyncStatus(SyncPhase Phase, string Message, double? Fraction = null);

/// <summary>Fabrique les connecteurs à partir de la configuration (l'application fournit l'authentification).</summary>
public interface IConnectorFactory
{
    ISourceConnector Create(SourceConfig config);
}

/// <summary>
/// Orchestration : synchronise les sources une par une, puis vectorise en arrière-plan.
/// Une seule synchronisation à la fois ; un appel pendant une synchro est ignoré.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly IndexStore _store;
    private readonly AppSettings _settings;
    private readonly IConnectorFactory _factory;
    private readonly TextExtractors _extractors;
    private readonly AppPaths _paths;
    private readonly Dictionary<string, ISourceConnector> _connectors = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private CancellationTokenSource? _cts;
    private Timer? _timer;

    public IEmbedder Embedder { get; set; } = NoEmbedder.Instance;
    public event Action<SyncStatus>? StatusChanged;
    public event Action? IndexChanged;
    public event Action<string>? Log;
    public SyncStatus Status { get; private set; } = new(SyncPhase.Idle, "Prêt");
    public bool IsRunning => _running.CurrentCount == 0;

    public SyncService(IndexStore store, AppSettings settings, IConnectorFactory factory, TextExtractors extractors, AppPaths paths)
    {
        _store = store; _settings = settings; _factory = factory; _extractors = extractors; _paths = paths;
    }

    public ISourceConnector GetConnector(SourceConfig cfg)
    {
        lock (_connectors)
        {
            if (!_connectors.TryGetValue(cfg.Id, out var c) || !ReferenceEquals(c.Config, cfg))
            {
                (c as IDisposable)?.Dispose();
                c = _factory.Create(cfg);
                _connectors[cfg.Id] = c;
            }
            return c;
        }
    }

    public ISourceConnector? GetConnector(string sourceId)
    {
        var cfg = _settings.Sources.FirstOrDefault(s => s.Id == sourceId);
        return cfg is null ? null : GetConnector(cfg);
    }

    /// <summary>À appeler après modification d'une source (identifiants, serveur…) pour recréer son connecteur.</summary>
    public void ResetConnector(string sourceId)
    {
        lock (_connectors)
            if (_connectors.Remove(sourceId, out var c)) (c as IDisposable)?.Dispose();
    }

    public void StartTimer()
    {
        _timer?.Dispose();
        var period = TimeSpan.FromMinutes(Math.Max(5, _settings.SyncIntervalMinutes));
        _timer = new Timer(_ => _ = SyncAllAsync(), null, _settings.SyncOnStartup ? TimeSpan.FromSeconds(5) : period, period);
    }

    public void Cancel() => _cts?.Cancel();

    public async Task SyncAllAsync(string? onlySourceId = null)
    {
        if (!await _running.WaitAsync(0)) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var sources = _settings.Sources.Where(s => s.Enabled && (onlySourceId is null || s.Id == onlySourceId)).ToList();
            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var src = sources[i];
                SetStatus(new SyncStatus(SyncPhase.Syncing, $"{src.Name} : démarrage…", (double)i / sources.Count));
                var ctx = new SyncContext
                {
                    Store = _store, Source = src, Extractors = _extractors, CacheDir = _paths.Cache,
                    Progress = new InlineProgress<SyncProgress>(p => SetStatus(new SyncStatus(SyncPhase.Syncing, $"{p.SourceName} : {p.Message}",
                        p.Total is > 0 ? ((double)i + (double)p.Processed / p.Total.Value) / sources.Count : (double)i / sources.Count))),
                    Log = m => Log?.Invoke(m),
                };
                try
                {
                    await GetConnector(src).SyncAsync(ctx, ct);
                    src.LastSync = DateTimeOffset.Now;
                    src.LastError = ctx.Errors > 0 ? $"{ctx.Errors} avertissement(s), voir le journal" : null;
                    Log?.Invoke($"[{src.Name}] +{ctx.Added} ajoutés, {ctx.Updated} mis à jour, -{ctx.Deleted} supprimés.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    src.LastError = e.Message;
                    Log?.Invoke($"[{src.Name}] Échec : {e}");
                }
                IndexChanged?.Invoke();
            }
            await EmbedPendingAsync(ct);
            _store.Optimize();
            SetStatus(new SyncStatus(SyncPhase.Idle, $"À jour — {DateTime.Now:HH:mm}"));
        }
        catch (OperationCanceledException)
        {
            SetStatus(new SyncStatus(SyncPhase.Idle, "Synchronisation interrompue"));
        }
        finally
        {
            try { _settings.Save(_paths.Settings); } catch (IOException) { }
            _running.Release();
            IndexChanged?.Invoke();
        }
    }

    private async Task EmbedPendingAsync(CancellationToken ct)
    {
        if (Embedder is NoEmbedder || !Embedder.IsReady) return;
        var worker = new EmbeddingWorker(_store, Embedder);
        try
        {
            await worker.RunAsync(new InlineProgress<(int Done, int Total)>(p =>
                SetStatus(new SyncStatus(SyncPhase.Embedding, $"Analyse du sens : {p.Done}/{p.Total}", p.Total > 0 ? (double)p.Done / p.Total : null))), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log?.Invoke($"Vectorisation interrompue : {e.Message}"); }
    }

    /// <summary>Retire immédiatement de l'index ce qui n'est plus sélectionné (décocher = effet immédiat).</summary>
    public int PruneSource(SourceConfig src)
    {
        var ctx = new SyncContext { Store = _store, Source = src, Extractors = _extractors, CacheDir = _paths.Cache };
        var n = ctx.Prune();
        if (n > 0) IndexChanged?.Invoke();
        return n;
    }

    public void RemoveSource(SourceConfig src)
    {
        ResetConnector(src.Id);
        _store.DeleteSource(src.Id);
        IndexChanged?.Invoke();
    }

    private void SetStatus(SyncStatus s) { Status = s; StatusChanged?.Invoke(s); }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Cancel();
        lock (_connectors) foreach (var c in _connectors.Values) (c as IDisposable)?.Dispose();
    }
}

/// <summary>Rapport de progression synchrone (Progress&lt;T&gt; poste en différé et mélange l'ordre des messages).</summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>Recherche : vectorise la requête (si un modèle est prêt), puis interroge l'index.</summary>
public sealed class SearchService
{
    private readonly IndexStore _store;
    private readonly Func<IEmbedder> _embedder;
    private readonly Func<float> _minSim;

    public SearchService(IndexStore store, Func<IEmbedder> embedder, Func<float>? minSimilarity = null)
    {
        _store = store; _embedder = embedder; _minSim = minSimilarity ?? (() => 0.35f);
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest req, CancellationToken ct)
    {
        float[]? vec = null;
        var parsed = QueryParser.Parse(req.Text);
        var emb = _embedder();
        if (req.Semantic && emb.IsReady && parsed.SemanticText.Length >= 3)
        {
            try { vec = await emb.EmbedQueryAsync(parsed.SemanticText, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { vec = null; }
        }
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => _store.Search(req, vec, _minSim()), ct);
    }
}
