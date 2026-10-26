using Fouineur.Core.Configuration;
using Fouineur.Core.Extraction;
using Fouineur.Core.Index;
using Fouineur.Core.Model;
using Fouineur.Core.Selection;

namespace Fouineur.Core.Connectors;

/// <summary>Icône d'un nœud dans le sélecteur de sources.</summary>
public enum NodeIcon { Root, Folder, Drive, Site, Team, Channel, Chat, Mailbox, MailFolder, SharedFolder, Server }

/// <summary>Nœud du sélecteur (chargé à la demande).</summary>
public sealed class SourceNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public NodeIcon Icon { get; init; } = NodeIcon.Folder;
    public bool HasChildren { get; init; } = true;
    /// <summary>Ancêtres [racine … parent].</summary>
    public IReadOnlyList<string> Ancestors { get; init; } = Array.Empty<string>();
    /// <summary>Le nœud ne peut pas être coché (simple regroupement).</summary>
    public bool Selectable { get; init; } = true;
    public string? Detail { get; init; }
    public object? Tag { get; init; }

    public IReadOnlyList<string> Chain => Ancestors.Append(Id).ToList();
}

public enum OpenKind { Url, File }

public sealed record OpenAction(OpenKind Kind, string Target, string? FallbackUrl = null);

public sealed record SyncProgress(string SourceName, string Message, int Processed = 0, int? Total = null);

/// <summary>Ce dont un connecteur a besoin pendant la synchronisation.</summary>
public sealed class SyncContext
{
    public required IndexStore Store { get; init; }
    public required SourceConfig Source { get; init; }
    public required TextExtractors Extractors { get; init; }
    public required string CacheDir { get; init; }
    public IProgress<SyncProgress>? Progress { get; init; }
    public Action<string>? Log { get; init; }

    public SelectionRules Rules => Source.Rules;
    public string SourceId => Source.Id;

    public int Added { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }
    public int Errors { get; set; }

    public void Report(string message, int processed = 0, int? total = null) =>
        Progress?.Report(new SyncProgress(Source.Name, message, processed, total));

    public void Warn(string message) { Errors++; Log?.Invoke($"[{Source.Name}] {message}"); }

    public void Upsert(IndexedItem it)
    {
        var prev = Store.GetVersion(it.Id);
        Store.Upsert(it);
        if (prev is null) Added++; else Updated++;
    }

    public void Delete(string id) { if (Store.Delete(id)) Deleted++; }

    /// <summary>Supprime les éléments de la source qui ne sont plus couverts par la sélection.</summary>
    public int Prune()
    {
        int n = 0;
        using var _ = Store.Batch();
        foreach (var (id, anc) in Store.GetAncestors(SourceId))
            if (!Rules.IsIncluded(anc)) { Store.Delete(id); n++; }
        Deleted += n;
        return n;
    }

    /// <summary>
    /// Vrai si les règles d'un périmètre ont changé depuis la dernière synchro (il faut alors le relire en entier).
    /// Met à jour l'empreinte.
    /// </summary>
    public bool ScopeChanged(string scopeKey, IReadOnlyList<string> chain)
    {
        var fp = Rules.Fingerprint(chain);
        var key = "rules:" + scopeKey;
        var prev = Store.GetState(SourceId, key);
        if (prev == fp) return false;
        Store.SetState(SourceId, key, fp);
        return prev is not null;
    }
}

public interface ISourceConnector
{
    SourceConfig Config { get; }

    /// <summary>Enfants d'un nœud du sélecteur (null = racines).</summary>
    Task<IReadOnlyList<SourceNode>> GetChildrenAsync(SourceNode? parent, CancellationToken ct);

    Task SyncAsync(SyncContext ctx, CancellationToken ct);

    /// <summary>Comment ouvrir l'élément depuis son emplacement d'origine.</summary>
    Task<OpenAction?> GetOpenActionAsync(IndexedItem item, CancellationToken ct);

    /// <summary>Lien (ou chemin) du conteneur : dossier, bibliothèque, canal…</summary>
    Task<OpenAction?> GetContainerActionAsync(IndexedItem item, CancellationToken ct);

    /// <summary>Fichier local à prévisualiser (téléchargé dans le cache si besoin) ; null si l'aperçu est intégré (mail, Teams).</summary>
    Task<string?> GetPreviewFileAsync(IndexedItem item, CancellationToken ct);

    /// <summary>Miniature (PNG/JPEG) fournie par le service, si disponible.</summary>
    Task<byte[]?> GetThumbnailAsync(IndexedItem item, CancellationToken ct);
}

/// <summary>Valeurs par défaut pour les connecteurs.</summary>
public abstract class ConnectorBase : ISourceConnector
{
    protected ConnectorBase(SourceConfig config) { Config = config; }
    public SourceConfig Config { get; }
    public abstract Task<IReadOnlyList<SourceNode>> GetChildrenAsync(SourceNode? parent, CancellationToken ct);
    public abstract Task SyncAsync(SyncContext ctx, CancellationToken ct);
    public virtual Task<OpenAction?> GetOpenActionAsync(IndexedItem item, CancellationToken ct) =>
        Task.FromResult<OpenAction?>(string.IsNullOrEmpty(item.OpenUri) ? null : new OpenAction(OpenKind.Url, item.OpenUri));
    public virtual Task<OpenAction?> GetContainerActionAsync(IndexedItem item, CancellationToken ct) => Task.FromResult<OpenAction?>(null);
    public virtual Task<string?> GetPreviewFileAsync(IndexedItem item, CancellationToken ct) => Task.FromResult<string?>(null);
    public virtual Task<byte[]?> GetThumbnailAsync(IndexedItem item, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    protected static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var r = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        if (r.Length > 120) r = r[..120];
        return r.Length == 0 ? "document" : r;
    }

    protected static string CacheKey(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
