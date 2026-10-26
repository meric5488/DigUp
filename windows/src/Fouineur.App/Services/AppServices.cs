using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Fouineur.Core.Configuration;
using Fouineur.Core.Connectors;
using Fouineur.Core.Connectors.Local;
using Fouineur.Core.Connectors.M365;
using Fouineur.Core.Connectors.Zimbra;
using Fouineur.Core.Embedding;
using Fouineur.Core.Extraction;
using Fouineur.Core.Index;
using Fouineur.Core.Model;
using Fouineur.Core.Sync;
using Fouineur.App.Infrastructure;

namespace Fouineur.App.Services;

/// <summary>Assemblage de l'application : index, réglages, connecteurs, synchro, recherche.</summary>
public sealed class AppServices : IConnectorFactory, IDisposable
{
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public IndexStore Store { get; }
    public TextExtractors Extractors { get; }
    public DpapiSecretStore Secrets { get; }
    public SyncService Sync { get; }
    public SearchService Search { get; }
    public ShellThumbnails Thumbnails { get; } = new();
    public IEmbedder Embedder { get; private set; } = NoEmbedder.Instance;
    public string EmbedderStatus { get; private set; } = "Recherche par le sens désactivée";
    private readonly Dictionary<string, MsalTokenProvider> _tokens = new();

    public AppServices()
    {
        Paths = new AppPaths();
        Logger.Init(Paths.Logs);
        Settings = AppSettings.Load(Paths.Settings);
        Secrets = new DpapiSecretStore(Paths.Secrets);
        Store = new IndexStore(Paths.Index);
        Logger.Info($"Index : {Paths.Index} (SQLite {IndexStore.SqliteVersion} via {IndexStore.SqliteLibrary})");

        Extractors = TextExtractors.CreateDefault();
        Extractors.Register(new PdfExtractor());
        Extractors.Register(new IFilterExtractor(".doc", ".dot", ".xls", ".ppt", ".pps", ".msg", ".vsd", ".pub", ".one", ".xps"));

        Sync = new SyncService(Store, Settings, this, Extractors, Paths);
        Sync.Log += Logger.Info;
        Search = new SearchService(Store, () => Embedder, () => Settings.Embedding.MinSimilarity);
        ReloadEmbedder();
    }

    /// <summary>(Re)charge le modèle selon les réglages ; en cas d'échec, la recherche reste textuelle.</summary>
    public void ReloadEmbedder()
    {
        (Embedder as IDisposable)?.Dispose();
        Embedder = NoEmbedder.Instance;
        var e = Settings.Embedding;
        try
        {
            switch (e.Provider)
            {
                case "onnx":
                    var dir = string.IsNullOrWhiteSpace(e.OnnxModelDir) ? Path.Combine(Paths.Models, "embeddinggemma") : e.OnnxModelDir;
                    if (!Directory.Exists(dir))
                    {
                        EmbedderStatus = "Modèle EmbeddingGemma non installé : recherche textuelle seule";
                        break;
                    }
                    var onnx = new OnnxEmbedder(dir, e.Dimensions, e.UseDirectML);
                    Embedder = onnx;
                    EmbedderStatus = $"Recherche par le sens : EmbeddingGemma ({onnx.Device})";
                    break;
                case "ollama":
                    Embedder = new OllamaEmbedder(e.OllamaUrl, e.OllamaModel, e.Dimensions);
                    EmbedderStatus = $"Recherche par le sens : Ollama ({e.OllamaModel})";
                    break;
                default:
                    EmbedderStatus = "Recherche par le sens désactivée";
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Chargement du modèle impossible", ex);
            EmbedderStatus = "Modèle illisible : recherche textuelle seule (voir le journal)";
        }
        Sync.Embedder = Embedder;
        if (Embedder is not NoEmbedder) new EmbeddingWorker(Store, Embedder).EnsureModel();
        Logger.Info(EmbedderStatus);
    }

    // ─── Fabrique de connecteurs ───

    public ISourceConnector Create(SourceConfig cfg)
    {
        switch (cfg.Type)
        {
            case SourceType.LocalFiles:
                return new LocalFilesConnector(cfg);
            case SourceType.Microsoft365:
                {
                    var graph = new GraphClient(TokenProvider(cfg));
                    var c = new M365Connector(cfg, graph, () => Settings.OpenOfficeInDesktopApp);
                    c.SetCacheDir(Paths.Cache);
                    return c;
                }
            case SourceType.Zimbra:
                {
                    var client = new ZimbraClient(cfg.ServerUrl, cfg.UserName, Secrets, "zimbra:" + cfg.Id, AskTwoFactorCode);
                    var c = new ZimbraConnector(cfg, client);
                    c.SetCacheDir(Paths.Cache);
                    return c;
                }
            default:
                throw new NotSupportedException(cfg.Type.ToString());
        }
    }

    public MsalTokenProvider TokenProvider(SourceConfig cfg)
    {
        var clientId = string.IsNullOrWhiteSpace(cfg.ClientId) ? Settings.DefaultM365ClientId : cfg.ClientId;
        var key = $"{cfg.Id}|{clientId}|{cfg.Tenant}";
        lock (_tokens)
        {
            if (!_tokens.TryGetValue(key, out var p))
                _tokens[key] = p = new MsalTokenProvider(clientId, cfg.Tenant, Paths.Root, $"msal-{cfg.Id}.cache");
            return p;
        }
    }

    private static Task<string?> AskTwoFactorCode(CancellationToken ct) =>
        Application.Current.Dispatcher.Invoke(() =>
            Task.FromResult(Views.InputDialog.Ask(
                "Double authentification Zimbra",
                "Saisissez le code à usage unique de votre application d'authentification :",
                isSecret: false)));

    // ─── Ouverture ───

    public async Task OpenAsync(IndexedItem item, bool container = false)
    {
        var con = Sync.GetConnector(item.SourceId);
        if (con is null) return;
        OpenAction? action;
        try
        {
            action = container ? await con.GetContainerActionAsync(item, default) : await con.GetOpenActionAsync(item, default);
        }
        catch (Exception e)
        {
            Logger.Error($"Ouverture de « {item.Title} »", e);
            action = string.IsNullOrEmpty(item.OpenUri) ? null : new OpenAction(OpenKind.Url, item.OpenUri);
        }
        if (action is null) return;
        if (container && item.Kind == ItemKind.Fichier && File.Exists(item.OpenUri))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.OpenUri}\"") { UseShellExecute = true });
            return;
        }
        Launch(action.Target, action.FallbackUrl);
    }

    public static void Launch(string target, string? fallback = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            if (fallback is not null)
            {
                Logger.Warn($"Ouverture de {target} impossible ({e.Message}), essai de {fallback}");
                Process.Start(new ProcessStartInfo(fallback) { UseShellExecute = true });
            }
            else throw;
        }
    }

    public void Dispose()
    {
        Sync.Dispose();
        Thumbnails.Dispose();
        (Embedder as IDisposable)?.Dispose();
        Store.Dispose();
    }
}
