using System.Text.RegularExpressions;
using Fouineur.Core.Configuration;
using Fouineur.Core.Model;

namespace Fouineur.Core.Connectors.Local;

/// <summary>
/// Dossiers locaux et partages réseau (chemins UNC \\serveur\partage ou lecteurs mappés).
/// Identifiants de nœuds = chemins complets normalisés (minuscules sous Windows).
/// </summary>
public sealed class LocalFilesConnector : ConnectorBase
{
    public LocalFilesConnector(SourceConfig config) : base(config) { }

    public static string NodeId(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 2 && full[1] == ':') full += "\\";
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>Ajoute un dossier racine et le coche.</summary>
    public void AddRoot(string path)
    {
        var id = NodeId(path);
        if (!Config.Paths.Any(p => NodeId(p) == id)) Config.Paths.Add(Path.GetFullPath(path));
        Config.Rules.Set(Array.Empty<string>(), id, true, path);
    }

    public void RemoveRoot(string path)
    {
        var id = NodeId(path);
        Config.Paths.RemoveAll(p => NodeId(p) == id);
        Config.Rules.Set(Array.Empty<string>(), id, false);
    }

    public override Task<IReadOnlyList<SourceNode>> GetChildrenAsync(SourceNode? parent, CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<SourceNode>>(() =>
        {
            if (parent is null)
                return Config.Paths.Select(p => new SourceNode
                {
                    Id = NodeId(p), Name = p, Icon = p.StartsWith(@"\\") ? NodeIcon.Server : NodeIcon.Drive,
                    Detail = Directory.Exists(p) ? null : "inaccessible",
                }).ToList();

            var dir = parent.Tag as string ?? parent.Id;
            if (parent.Ancestors.Count == 0)
                dir = Config.Paths.FirstOrDefault(p => NodeId(p) == parent.Id) ?? dir;
            try
            {
                var excl = new Excluder(Config.ExcludePatterns);
                return new DirectoryInfo(dir).EnumerateDirectories()
                    .Where(d => !excl.IsExcluded(d.Name) && (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(d => new SourceNode
                    {
                        Id = NodeId(d.FullName), Name = d.Name, Icon = NodeIcon.Folder, Ancestors = parent.Chain, Tag = d.FullName,
                        HasChildren = HasSubdirs(d),
                    }).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<SourceNode>();
            }
        }, ct);
    }

    private static bool HasSubdirs(DirectoryInfo d)
    {
        try { return d.EnumerateDirectories().Any(); } catch { return false; }
    }

    public override async Task SyncAsync(SyncContext ctx, CancellationToken ct)
    {
        await Task.Run(() => SyncCore(ctx, ct), ct);
    }

    private void SyncCore(SyncContext ctx, CancellationToken ct)
    {
        ctx.Prune();
        var excl = new Excluder(Config.ExcludePatterns);
        long maxBytes = (long)Config.MaxFileSizeMb * 1024 * 1024;
        int processed = 0;

        foreach (var root in Config.Paths.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rootId = NodeId(root);
            if (!ctx.Rules.AnyIncludedUnder(Array.Empty<string>(), rootId)) continue;
            if (!Directory.Exists(root))
            {
                // Partage hors ligne : on ne supprime rien, on réessaiera plus tard.
                ctx.Warn($"Dossier inaccessible, ignoré pour cette fois : {root}");
                continue;
            }

            var prefix = $"file:{rootId}";
            var known = ctx.Store.GetVersions(ctx.SourceId, prefix);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var incomplete = new List<string>(); // dossiers illisibles : on garde leurs éléments
            ctx.Report($"Parcours de {root}…");

            var stack = new Stack<(string Path, List<string> Chain)>();
            stack.Push((root, new List<string> { rootId }));
            // Pas de transaction longue ici : l'extraction est lente et la recherche doit rester disponible.
            {
                int sinceCommit = 0;
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (dir, chain) = stack.Pop();
                    bool dirIncluded = ctx.Rules.IsIncluded(chain);
                    IEnumerable<FileSystemInfo> entries;
                    try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        incomplete.Add(NodeId(dir));
                        ctx.Warn($"Lecture impossible : {dir} ({e.Message})");
                        continue;
                    }
                    foreach (var e in entries)
                    {
                        if (excl.IsExcluded(e.Name)) continue;
                        if ((e.Attributes & FileAttributes.System) != 0) continue;
                        if (e is DirectoryInfo d)
                        {
                            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; // évite les boucles (jonctions)
                            var childChain = new List<string>(chain) { NodeId(d.FullName) };
                            if (ctx.Rules.AnyIncludedUnder(chain, childChain[^1])) stack.Push((d.FullName, childChain));
                            continue;
                        }
                        if (!dirIncluded || e is not FileInfo f) continue;
                        if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                        var id = "file:" + NodeId(f.FullName);
                        seen.Add(id);
                        var version = $"{f.LastWriteTimeUtc.Ticks:x}-{f.Length:x}";
                        if (known.TryGetValue(id, out var v) && v == version) continue;
                        // Fichier dans le cloud OneDrive non téléchargé : on n'ouvre pas son contenu (évite un téléchargement).
                        bool offline = ((int)f.Attributes & 0x00400000) != 0 || (f.Attributes & FileAttributes.Offline) != 0;
                        ctx.Upsert(BuildItem(ctx, f, chain, version, maxBytes, offline, ct));
                        processed++;
                        if (++sinceCommit >= 200)
                        {
                            sinceCommit = 0;
                            ctx.Report($"{root} : {processed} fichiers traités", processed);
                        }
                    }
                }
            }

            // Balayage : éléments disparus (sauf sous des dossiers illisibles).
            using (ctx.Store.Batch())
            {
                foreach (var id in known.Keys)
                {
                    if (seen.Contains(id)) continue;
                    var path = id[5..];
                    if (incomplete.Any(p => path.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal))) continue;
                    ctx.Delete(id);
                }
            }
        }
        ctx.Report($"Terminé : {processed} fichiers mis à jour", processed);
    }

    private IndexedItem BuildItem(SyncContext ctx, FileInfo f, List<string> chain, string version, long maxBytes, bool offline, CancellationToken ct)
    {
        var ext = f.Extension.ToLowerInvariant();
        string body = "";
        if (!offline && f.Length <= maxBytes && ctx.Extractors.CanExtract(ext))
        {
            try { body = ctx.Extractors.ExtractFile(f.FullName, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { ctx.Warn($"Extraction impossible : {f.FullName} ({e.GetType().Name}: {e.Message})"); }
        }
        var rootPath = Config.Paths.FirstOrDefault(p => NodeId(p) == chain[0]) ?? "";
        var rel = Path.GetRelativePath(rootPath, f.DirectoryName ?? "");
        string author = "";
        return new IndexedItem
        {
            Id = "file:" + NodeId(f.FullName),
            SourceId = ctx.SourceId,
            Kind = ItemKind.Fichier,
            Title = f.Name,
            Author = author,
            Date = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
            Location = rel == "." ? rootPath : Path.Combine(rootPath, rel),
            OpenUri = f.FullName,
            Extension = ext,
            Size = f.Length,
            Ancestors = chain.ToList(),
            Version = version,
            Body = body,
        }.SetExtra("folder", f.DirectoryName);
    }

    public override Task<OpenAction?> GetOpenActionAsync(IndexedItem item, CancellationToken ct) =>
        Task.FromResult<OpenAction?>(new OpenAction(OpenKind.File, item.OpenUri));

    public override Task<OpenAction?> GetContainerActionAsync(IndexedItem item, CancellationToken ct) =>
        Task.FromResult<OpenAction?>(new OpenAction(OpenKind.File, item.GetExtra("folder") ?? Path.GetDirectoryName(item.OpenUri) ?? item.OpenUri));

    public override Task<string?> GetPreviewFileAsync(IndexedItem item, CancellationToken ct) =>
        Task.FromResult<string?>(File.Exists(item.OpenUri) ? item.OpenUri : null);
}

/// <summary>Motifs d'exclusion simples : nom exact ou jokers * et ?.</summary>
public sealed class Excluder
{
    private readonly List<Regex> _patterns;
    public Excluder(IEnumerable<string> patterns)
    {
        _patterns = patterns.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex("^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }
    public bool IsExcluded(string name) => _patterns.Any(r => r.IsMatch(name));
}
