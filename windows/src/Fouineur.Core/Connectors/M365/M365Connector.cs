using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;
using Fouineur.Core.Extraction;
using Fouineur.Core.Model;

namespace Fouineur.Core.Connectors.M365;

/// <summary>
/// Microsoft 365 (Graph délégué, lecture seule) : OneDrive, sites SharePoint, canaux Teams, conversations Teams.
///
/// Identifiants de nœuds :
///   od                      Mon OneDrive (racine du lecteur)
///   sites › site:{id} › drive:{id}   sites et bibliothèques
///   item:{driveId}:{itemId} dossiers
///   teams › team:{id} › channel:{teamId}:{channelId}
///   chats › chat:{id}
/// </summary>
public sealed class M365Connector : ConnectorBase, IDisposable
{
    public const string OneDriveRoot = "od", SitesRoot = "sites", TeamsRoot = "teams", ChatsRoot = "chats";

    private readonly GraphClient _graph;
    private readonly Func<bool> _openOfficeInDesktop;
    public long MaxDownloadBytes { get; set; } = 50L * 1024 * 1024;

    public M365Connector(SourceConfig config, GraphClient graph, Func<bool>? openOfficeInDesktop = null) : base(config)
    {
        _graph = graph;
        _openOfficeInDesktop = openOfficeInDesktop ?? (() => true);
    }

    public GraphClient Graph => _graph;

    // ───────────────────────────── Sélecteur ─────────────────────────────

    public override async Task<IReadOnlyList<SourceNode>> GetChildrenAsync(SourceNode? parent, CancellationToken ct)
    {
        if (parent is null)
            return new[]
            {
                new SourceNode { Id = OneDriveRoot, Name = "Mon OneDrive", Icon = NodeIcon.Drive },
                new SourceNode { Id = SitesRoot, Name = "Sites SharePoint", Icon = NodeIcon.Root, Detail = "cocher « Sites » inclut tous vos sites" },
                new SourceNode { Id = TeamsRoot, Name = "Équipes et canaux Teams", Icon = NodeIcon.Root },
                new SourceNode { Id = ChatsRoot, Name = "Conversations Teams", Icon = NodeIcon.Root },
            };

        var chain = parent.Chain;
        var id = parent.Id;
        if (id == OneDriveRoot) return await FolderChildren("me/drive/root/children", (await MyDrive(ct)).Id, chain, ct);
        if (id == SitesRoot) return (await ListSites(ct)).Select(s => SiteNode(s, chain)).ToList();
        if (id.StartsWith("site:"))
        {
            var list = new List<SourceNode>();
            await foreach (var d in _graph.GetPagedAsync($"sites/{id[5..]}/drives?$select=id,name,driveType,webUrl", ct))
                list.Add(new SourceNode { Id = "drive:" + d.S("id"), Name = d.S("name"), Icon = NodeIcon.Drive, Ancestors = chain, Tag = d.S("webUrl") });
            return list;
        }
        if (id.StartsWith("drive:")) return await FolderChildren($"drives/{id[6..]}/root/children", id[6..], chain, ct);
        if (id.StartsWith("item:"))
        {
            var (drive, item) = SplitItem(id);
            return await FolderChildren($"drives/{drive}/items/{item}/children", drive, chain, ct);
        }
        if (id == TeamsRoot)
        {
            var list = new List<SourceNode>();
            await foreach (var t in _graph.GetPagedAsync("me/joinedTeams?$select=id,displayName,description", ct))
                list.Add(new SourceNode { Id = "team:" + t.S("id"), Name = t.S("displayName"), Icon = NodeIcon.Team, Ancestors = chain, Detail = t.S("description") });
            return list.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        if (id.StartsWith("team:"))
        {
            var team = id[5..];
            var list = new List<SourceNode>();
            await foreach (var c in _graph.GetPagedAsync($"teams/{team}/channels?$select=id,displayName,membershipType", ct))
                list.Add(new SourceNode
                {
                    Id = $"channel:{team}:{c.S("id")}", Name = c.S("displayName"), Icon = NodeIcon.Channel, Ancestors = chain, HasChildren = false,
                    Detail = c.S("membershipType") == "private" ? "privé" : c.S("membershipType") == "shared" ? "partagé" : null,
                });
            return list;
        }
        if (id == ChatsRoot)
        {
            var me = await Me(ct);
            var list = new List<SourceNode>();
            await foreach (var c in _graph.GetPagedAsync("me/chats?$expand=members&$top=50", ct, maxPages: 40))
                list.Add(new SourceNode
                {
                    Id = "chat:" + c.S("id"), Name = ChatName(c, me.Id), Icon = NodeIcon.Chat, Ancestors = chain, HasChildren = false,
                    Detail = c.S("chatType") switch { "oneOnOne" => "conversation privée", "group" => "groupe", "meeting" => "réunion", _ => null },
                    Tag = c.D("lastUpdatedDateTime"),
                });
            return list.OrderByDescending(n => n.Tag is DateTimeOffset d ? d : DateTimeOffset.MinValue).ToList();
        }
        return Array.Empty<SourceNode>();
    }

    private static SourceNode SiteNode(JsonObject s, IReadOnlyList<string> chain) => new()
    {
        Id = "site:" + s.S("id"), Name = string.IsNullOrEmpty(s.S("displayName")) ? s.S("name") : s.S("displayName"),
        Icon = NodeIcon.Site, Ancestors = chain, Detail = s.S("webUrl"),
    };

    private async Task<IReadOnlyList<SourceNode>> FolderChildren(string url, string driveId, IReadOnlyList<string> chain, CancellationToken ct)
    {
        var list = new List<SourceNode>();
        await foreach (var i in _graph.GetPagedAsync(url + "?$select=id,name,folder&$top=999", ct))
            if (i.Has("folder"))
                list.Add(new SourceNode
                {
                    Id = $"item:{driveId}:{i.S("id")}", Name = i.S("name"), Icon = NodeIcon.Folder, Ancestors = chain,
                    HasChildren = i["folder"]!.L("childCount") > 0,
                });
        return list.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<List<JsonObject>> ListSites(CancellationToken ct)
    {
        var map = new Dictionary<string, JsonObject>();
        try
        {
            await foreach (var s in _graph.GetPagedAsync("me/followedSites?$select=id,displayName,name,webUrl", ct))
                map[s.S("id")] = s;
        }
        catch (GraphException) { /* comptes sans site suivi */ }
        await foreach (var s in _graph.GetPagedAsync("sites?search=*&$select=id,displayName,name,webUrl&$top=200", ct, maxPages: 25))
            map.TryAdd(s.S("id"), s);
        return map.Values.OrderBy(s => s.S("displayName"), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Recherche de sites par nom (pour le champ de recherche du sélecteur).</summary>
    public async Task<IReadOnlyList<SourceNode>> SearchSitesAsync(string text, CancellationToken ct)
    {
        var list = new List<SourceNode>();
        await foreach (var s in _graph.GetPagedAsync($"sites?search={Uri.EscapeDataString(text)}&$select=id,displayName,name,webUrl&$top=50", ct, maxPages: 2))
            list.Add(SiteNode(s, new[] { SitesRoot }));
        return list;
    }

    private static (string Drive, string Item) SplitItem(string nodeId)
    {
        var rest = nodeId[5..];
        int c = rest.IndexOf(':');
        return (rest[..c], rest[(c + 1)..]);
    }

    private record MeInfo(string Id, string Name);
    private MeInfo? _me;
    private async Task<MeInfo> Me(CancellationToken ct)
    {
        if (_me is not null) return _me;
        var m = await _graph.GetAsync("me?$select=id,displayName,userPrincipalName", ct);
        return _me = new MeInfo(m.S("id"), m.S("displayName"));
    }

    private record DriveInfo(string Id, string Name, string WebUrl, string DriveType);
    private DriveInfo? _myDrive;
    private async Task<DriveInfo> MyDrive(CancellationToken ct)
    {
        if (_myDrive is not null) return _myDrive;
        var d = await _graph.GetAsync("me/drive?$select=id,name,webUrl,driveType", ct);
        return _myDrive = new DriveInfo(d.S("id"), d.S("name"), d.S("webUrl"), d.S("driveType"));
    }

    private static string ChatName(JsonObject chat, string meId)
    {
        var topic = chat.S("topic");
        if (!string.IsNullOrWhiteSpace(topic)) return topic;
        var names = (chat["members"] as JsonArray ?? new JsonArray())
            .Where(m => m.S("userId") != meId)
            .Select(m => m.S("displayName")).Where(n => n.Length > 0).ToList();
        return names.Count == 0 ? "Conversation" : string.Join(", ", names.Take(4)) + (names.Count > 4 ? $" +{names.Count - 4}" : "");
    }

    // ───────────────────────────── Synchronisation ─────────────────────────────

    public override async Task SyncAsync(SyncContext ctx, CancellationToken ct)
    {
        ctx.Prune();
        var rules = ctx.Rules;
        var store = ctx.Store;
        store.SetTreeNode(ctx.SourceId, OneDriveRoot, null, "OneDrive");
        store.SetTreeNode(ctx.SourceId, SitesRoot, null, "SharePoint");
        store.SetTreeNode(ctx.SourceId, TeamsRoot, null, "Teams");
        store.SetTreeNode(ctx.SourceId, ChatsRoot, null, "Conversations");

        // 1) OneDrive
        if (rules.AnyIncludedUnder(Array.Empty<string>(), OneDriveRoot))
        {
            var d = await MyDrive(ct);
            await Guard(ctx, "OneDrive", () => SyncDrive(ctx, d.Id, OneDriveRoot, new[] { OneDriveRoot }, ItemKind.OneDrive, ct));
        }

        // 2) SharePoint
        if (rules.AnyIncludedUnder(Array.Empty<string>(), SitesRoot))
        {
            IEnumerable<string> siteIds;
            if (rules.IsIncluded(Array.Empty<string>(), SitesRoot))
                siteIds = (await ListSites(ct)).Select(s => "site:" + s.S("id"));
            else
                siteIds = rules.Rules.Where(r => r.Value.Include)
                    .Select(r => r.Key.StartsWith("site:") ? r.Key : r.Value.Ancestors.FirstOrDefault(a => a.StartsWith("site:")))
                    .Where(s => s is not null).Select(s => s!).Distinct();

            foreach (var siteNode in siteIds.ToList())
            {
                ct.ThrowIfCancellationRequested();
                var siteChain = new[] { SitesRoot };
                if (!rules.AnyIncludedUnder(siteChain, siteNode)) continue;
                await Guard(ctx, siteNode, async () =>
                {
                    var site = await _graph.GetAsync($"sites/{siteNode[5..]}?$select=id,displayName,name,webUrl", ct);
                    var siteName = site.S("displayName").Length > 0 ? site.S("displayName") : site.S("name");
                    store.SetTreeNode(ctx.SourceId, siteNode, SitesRoot, siteName);
                    var drives = new List<JsonObject>();
                    await foreach (var dr in _graph.GetPagedAsync($"sites/{siteNode[5..]}/drives?$select=id,name,driveType,webUrl", ct)) drives.Add(dr);
                    foreach (var dr in drives)
                    {
                        var driveNode = "drive:" + dr.S("id");
                        var chain = new[] { SitesRoot, siteNode };
                        if (!rules.AnyIncludedUnder(chain, driveNode)) continue;
                        store.SetTreeNode(ctx.SourceId, driveNode, siteNode, dr.S("name"));
                        await Guard(ctx, $"{siteName} › {dr.S("name")}", () =>
                            SyncDrive(ctx, dr.S("id"), driveNode, new[] { SitesRoot, siteNode, driveNode }, ItemKind.SharePoint, ct));
                    }
                });
            }
        }

        // 3) Canaux Teams
        if (rules.AnyIncludedUnder(Array.Empty<string>(), TeamsRoot))
        {
            var teams = new List<JsonObject>();
            await foreach (var t in _graph.GetPagedAsync("me/joinedTeams?$select=id,displayName", ct)) teams.Add(t);
            foreach (var t in teams)
            {
                var teamNode = "team:" + t.S("id");
                if (!rules.AnyIncludedUnder(new[] { TeamsRoot }, teamNode)) continue;
                store.SetTreeNode(ctx.SourceId, teamNode, TeamsRoot, t.S("displayName"));
                var channels = new List<JsonObject>();
                await Guard(ctx, t.S("displayName"), async () =>
                {
                    await foreach (var c in _graph.GetPagedAsync($"teams/{t.S("id")}/channels?$select=id,displayName", ct)) channels.Add(c);
                });
                foreach (var c in channels)
                {
                    var chNode = $"channel:{t.S("id")}:{c.S("id")}";
                    var chain = new[] { TeamsRoot, teamNode, chNode };
                    if (!rules.IsIncluded(chain)) continue;
                    store.SetTreeNode(ctx.SourceId, chNode, teamNode, c.S("displayName"));
                    await Guard(ctx, $"{t.S("displayName")} › {c.S("displayName")}", () =>
                        SyncChannel(ctx, t.S("id"), c.S("id"), t.S("displayName"), c.S("displayName"), chain, ct));
                }
            }
        }

        // 4) Conversations
        if (rules.AnyIncludedUnder(Array.Empty<string>(), ChatsRoot))
        {
            var me = await Me(ct);
            var chats = new List<JsonObject>();
            await foreach (var c in _graph.GetPagedAsync("me/chats?$expand=members&$top=50", ct)) chats.Add(c);
            foreach (var c in chats)
            {
                var chatNode = "chat:" + c.S("id");
                var chain = new[] { ChatsRoot, chatNode };
                if (!rules.IsIncluded(chain)) continue;
                var name = ChatName(c, me.Id);
                store.SetTreeNode(ctx.SourceId, chatNode, ChatsRoot, name);
                await Guard(ctx, name, () => SyncChat(ctx, c.S("id"), name, c.S("webUrl"), chain, ct));
            }
        }
    }

    private static async Task Guard(SyncContext ctx, string what, Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { throw; }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            ctx.Warn($"{what} : accès refusé ou introuvable ({e.Code}).");
        }
        catch (Exception e) when (e is GraphException or HttpRequestException or IOException)
        {
            ctx.Warn($"{what} : {e.Message}");
        }
    }

    // ─── Lecteurs (OneDrive / bibliothèques) ───

    private string NodeFor(SyncContext ctx, string driveId, string driveNode, string? itemId)
    {
        if (itemId is null) return driveNode;
        var root = ctx.Store.GetState(ctx.SourceId, "root:" + driveId);
        return itemId == root ? driveNode : $"item:{driveId}:{itemId}";
    }

    private const string DeltaSelect = "id,name,file,folder,deleted,root,parentReference,size,lastModifiedDateTime,lastModifiedBy,createdBy,webUrl,webDavUrl,cTag,eTag,package";

    internal async Task SyncDrive(SyncContext ctx, string driveId, string driveNode, IReadOnlyList<string> driveChain, ItemKind kind, CancellationToken ct)
    {
        var store = ctx.Store;
        var deltaKey = "delta:" + driveId;
        if (ctx.ScopeChanged("drive:" + driveId, driveChain))
        {
            store.SetState(ctx.SourceId, deltaKey, null);
            ctx.Log?.Invoke($"Sélection modifiée : relecture complète du lecteur {driveNode}.");
        }
        if (store.GetState(ctx.SourceId, "root:" + driveId) is null)
        {
            var root = await _graph.GetAsync($"drives/{driveId}/root?$select=id", ct);
            store.SetState(ctx.SourceId, "root:" + driveId, root.S("id"));
        }

        var url = store.GetState(ctx.SourceId, deltaKey) ?? $"drives/{driveId}/root/delta?$select={DeltaSelect}";
        string? newDelta = null;
        int n = 0;
        ctx.Report($"{store.GetTreeNode(ctx.SourceId, driveNode)?.Name ?? "OneDrive"} : lecture des changements…");
        try
        {
            await foreach (var it in _graph.GetPagedAsync(url, ct, d => newDelta = d))
            {
                ct.ThrowIfCancellationRequested();
                await HandleDriveItem(ctx, it, driveId, driveNode, driveChain, kind, ct);
                if (++n % 100 == 0) ctx.Report($"{n} éléments examinés", n);
            }
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.Gone || e.Code is "resyncRequired" or "invalidToken")
        {
            store.SetState(ctx.SourceId, deltaKey, null);
            ctx.Log?.Invoke("Jeton delta expiré : relecture complète au prochain passage.");
            return;
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.Forbidden && url.Contains("/root/delta"))
        {
            // Pas d'accès à la racine : on parcourt seulement les dossiers cochés.
            ctx.Log?.Invoke($"{driveNode} : racine inaccessible, parcours des dossiers sélectionnés.");
            await WalkSelectedFolders(ctx, driveId, driveNode, driveChain, kind, ct);
            return;
        }
        if (newDelta is not null) store.SetState(ctx.SourceId, deltaKey, newDelta);
    }

    private async Task HandleDriveItem(SyncContext ctx, JsonObject it, string driveId, string driveNode, IReadOnlyList<string> driveChain, ItemKind kind, CancellationToken ct)
    {
        var store = ctx.Store;
        var id = it.S("id");
        var docId = $"sp:{driveId}:{id}";
        if (it.Has("deleted"))
        {
            ctx.Delete(docId);
            store.DeleteTreeNode(ctx.SourceId, $"item:{driveId}:{id}");
            return;
        }
        if (it.Has("root"))
        {
            store.SetState(ctx.SourceId, "root:" + driveId, id);
            return;
        }
        var parentId = it["parentReference"].Sn("id");
        var parentNode = NodeFor(ctx, driveId, driveNode, parentId);
        if (it.Has("folder") || it.Has("package") && !it.Has("file"))
        {
            store.SetTreeNode(ctx.SourceId, NodeFor(ctx, driveId, driveNode, id), parentNode, it.S("name"));
            return;
        }
        if (!it.Has("file")) return;

        var chain = ChainFor(ctx, parentNode, driveChain);
        if (!ctx.Rules.IsIncluded(chain)) { ctx.Delete(docId); return; }

        var version = it.Sn("cTag") ?? it.Sn("eTag") ?? it.S("lastModifiedDateTime");
        if (store.GetVersion(docId) == version) return;

        await UpsertFile(ctx, it, driveId, docId, chain, version, kind, ct);
    }

    private List<string> ChainFor(SyncContext ctx, string parentNode, IReadOnlyList<string> driveChain)
    {
        var (ids, _) = ctx.Store.GetTreePath(ctx.SourceId, parentNode);
        if (ids.Count == 0 || ids[0] != driveChain[0])
        {
            // Parent inconnu (ordre du delta) : on rattache au lecteur.
            var c = driveChain.ToList();
            if (parentNode != driveChain[^1]) c.Add(parentNode);
            return c;
        }
        return ids;
    }

    private string LocationFor(SyncContext ctx, IReadOnlyList<string> chain)
    {
        var names = new List<string>();
        foreach (var n in chain)
        {
            if (n is SitesRoot) continue;
            var t = ctx.Store.GetTreeNode(ctx.SourceId, n);
            if (t is not null && t.Value.Name.Length > 0) names.Add(t.Value.Name);
        }
        return string.Join(" › ", names);
    }

    private async Task UpsertFile(SyncContext ctx, JsonObject it, string driveId, string docId, IReadOnlyList<string> chain, string version, ItemKind kind, CancellationToken ct)
    {
        var name = it.S("name");
        var ext = Path.GetExtension(name).ToLowerInvariant();
        long size = it.L("size");
        string body = "";
        if (ctx.Extractors.CanExtract(ext) && size <= MaxDownloadBytes && size > 0)
        {
            try
            {
                var bytes = await _graph.GetBytesAsync($"drives/{driveId}/items/{it.S("id")}/content", ct, MaxDownloadBytes);
                using var ms = new MemoryStream(bytes);
                body = ctx.Extractors.Extract(ms, ext, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { ctx.Warn($"{name} : contenu illisible ({e.Message})"); }
        }
        var author = it["lastModifiedBy"]?["user"].S("displayName");
        if (string.IsNullOrEmpty(author)) author = it["createdBy"]?["user"].S("displayName") ?? "";
        var item = new IndexedItem
        {
            Id = docId, SourceId = ctx.SourceId, Kind = kind,
            Title = name, Author = author ?? "", Date = it.D("lastModifiedDateTime"),
            Location = LocationFor(ctx, chain), OpenUri = it.S("webUrl"), Extension = ext, Size = size,
            Ancestors = chain, Version = version, Body = body,
        };
        item.SetExtra("driveId", driveId).SetExtra("itemId", it.S("id"))
            .SetExtra("parentId", it["parentReference"].Sn("id")).SetExtra("webDavUrl", it.Sn("webDavUrl"));
        ctx.Upsert(item);
    }

    private async Task WalkSelectedFolders(SyncContext ctx, string driveId, string driveNode, IReadOnlyList<string> driveChain, ItemKind kind, CancellationToken ct)
    {
        var starts = new List<(string Node, List<string> Chain)>();
        if (ctx.Rules.IsIncluded(driveChain)) starts.Add((driveNode, driveChain.ToList()));
        else
            foreach (var (node, rule) in ctx.Rules.IncludedDescendants(driveNode))
                starts.Add((node, rule.Ancestors.Append(node).ToList()));

        var known = ctx.Store.GetVersions(ctx.SourceId, $"sp:{driveId}:");
        var seen = new HashSet<string>();
        var walkedRoots = new HashSet<string>();
        foreach (var (startNode, startChain) in starts)
        {
            walkedRoots.Add(startNode);
            var queue = new Queue<(string ItemUrl, List<string> Chain)>();
            queue.Enqueue((startNode == driveNode ? $"drives/{driveId}/root" : $"drives/{driveId}/items/{SplitItem(startNode).Item}", startChain));
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (itemUrl, chain) = queue.Dequeue();
                await foreach (var it in _graph.GetPagedAsync($"{itemUrl}/children?$select={DeltaSelect}&$top=999", ct))
                {
                    var id = it.S("id");
                    if (it.Has("folder"))
                    {
                        var node = $"item:{driveId}:{id}";
                        ctx.Store.SetTreeNode(ctx.SourceId, node, chain[^1], it.S("name"));
                        var childChain = new List<string>(chain) { node };
                        if (ctx.Rules.AnyIncludedUnder(chain, node)) queue.Enqueue(($"drives/{driveId}/items/{id}", childChain));
                        continue;
                    }
                    if (!it.Has("file") || !ctx.Rules.IsIncluded(chain)) continue;
                    var docId = $"sp:{driveId}:{id}";
                    seen.Add(docId);
                    var version = it.Sn("cTag") ?? it.Sn("eTag") ?? it.S("lastModifiedDateTime");
                    if (known.TryGetValue(docId, out var v) && v == version) continue;
                    await UpsertFile(ctx, it, driveId, docId, chain, version, kind, ct);
                }
            }
        }
        foreach (var (id, anc) in ctx.Store.GetAncestors(ctx.SourceId))
            if (id.StartsWith($"sp:{driveId}:") && !seen.Contains(id) && anc.Any(walkedRoots.Contains))
                ctx.Delete(id);
    }

    // ─── Canaux Teams : un document par fil ───

    internal async Task SyncChannel(SyncContext ctx, string teamId, string channelId, string teamName, string channelName, IReadOnlyList<string> chain, CancellationToken ct)
    {
        var store = ctx.Store;
        var key = $"chdelta:{teamId}:{channelId}";
        var url = store.GetState(ctx.SourceId, key);
        if (url is null)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, Config.ChannelHistoryDays)).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            url = $"teams/{teamId}/channels/{channelId}/messages/delta?$filter=lastModifiedDateTime gt {since}";
        }
        string? newDelta = null;
        ctx.Report($"{teamName} › {channelName} : lecture des messages…");
        int n = 0;
        try
        {
            await foreach (var root in _graph.GetPagedAsync(url, ct, d => newDelta = d))
            {
                ct.ThrowIfCancellationRequested();
                var rootId = root.S("id");
                var doc = $"thread:{teamId}:{channelId}:{rootId}";
                if (root.Has("@removed") || root.Has("deletedDateTime"))
                {
                    store.DeleteMessagesOfDocPrefix(ctx.SourceId, doc);
                    ctx.Delete(doc);
                    continue;
                }
                if (root.S("messageType") is not ("message" or "")) continue;
                StoreMessage(ctx, root, doc);
                await foreach (var rep in _graph.GetPagedAsync($"teams/{teamId}/channels/{channelId}/messages/{rootId}/replies?$top=50", ct))
                {
                    if (rep.Has("deletedDateTime")) { store.DeleteMessage(ctx.SourceId, rep.S("id")); continue; }
                    if (rep.S("messageType") is not ("message" or "")) continue;
                    StoreMessage(ctx, rep, doc);
                }
                RebuildDoc(ctx, doc, chain, title: Subject(root), location: $"{teamName} › {channelName}", rootWebUrl: root.S("webUrl"));
                if (++n % 20 == 0) ctx.Report($"{teamName} › {channelName} : {n} fils", n);
            }
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.Gone || e.Code is "resyncRequired")
        {
            store.SetState(ctx.SourceId, key, null);
            return;
        }
        if (newDelta is not null) store.SetState(ctx.SourceId, key, newDelta);
    }

    private static string Subject(JsonObject msg)
    {
        var s = msg.S("subject");
        if (!string.IsNullOrWhiteSpace(s)) return s;
        var t = MessageText(msg);
        t = t.Replace('\n', ' ');
        return t.Length > 90 ? t[..90].TrimEnd() + "…" : t.Length == 0 ? "(sans texte)" : t;
    }

    private static string MessageText(JsonObject msg)
    {
        var body = msg["body"];
        var content = body.S("content");
        var text = body.S("contentType") == "html" ? HtmlText.ToText(content) : content;
        if (msg["attachments"] is JsonArray atts)
            foreach (var a in atts)
            {
                var name = a.S("name");
                if (name.Length > 0) text += $"\n[Pièce jointe] {name}";
            }
        return text.Trim();
    }

    private static string AuthorOf(JsonObject msg) =>
        msg["from"]?["user"].S("displayName") is { Length: > 0 } u ? u :
        msg["from"]?["application"].S("displayName") is { Length: > 0 } a ? a : "";

    private static void StoreMessage(SyncContext ctx, JsonObject msg, string doc)
    {
        var ts = msg.D("createdDateTime");
        ctx.Store.UpsertMessage(ctx.SourceId, msg.S("id"), doc, ts, AuthorOf(msg), MessageText(msg), msg.S("webUrl"));
    }

    private static void RebuildDoc(SyncContext ctx, string doc, IReadOnlyList<string> chain, string title, string location, string? rootWebUrl)
    {
        var msgs = ctx.Store.GetMessages(ctx.SourceId, doc);
        if (msgs.Count == 0) { ctx.Delete(doc); return; }
        var sb = new StringBuilder();
        foreach (var m in msgs)
            sb.Append(m.Author).Append(" — ").Append(m.Ts.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR")))
              .Append('\n').Append(m.Text).Append("\n\n");
        var authors = msgs.Select(m => m.Author).Where(a => a.Length > 0).Distinct().ToList();
        var item = new IndexedItem
        {
            Id = doc, SourceId = ctx.SourceId, Kind = ItemKind.Teams,
            Title = title, Author = authors.FirstOrDefault() ?? "", Date = msgs[^1].Ts,
            Location = location, OpenUri = !string.IsNullOrEmpty(rootWebUrl) ? rootWebUrl : msgs[0].WebUrl,
            Extension = ".teams", Size = msgs.Count, Ancestors = chain,
            Version = $"{msgs.Count}-{msgs[^1].Ts.ToUnixTimeMilliseconds()}-{sb.Length}",
            Body = sb.ToString(),
        };
        item.SetExtra("participants", string.Join(", ", authors)).SetExtra("messages", msgs.Count.ToString(CultureInfo.InvariantCulture));
        ctx.Upsert(item);
    }

    // ─── Conversations : un document par conversation et par jour ───

    internal async Task SyncChat(SyncContext ctx, string chatId, string chatName, string chatWebUrl, IReadOnlyList<string> chain, CancellationToken ct)
    {
        var store = ctx.Store;
        var key = "chatwm:" + chatId;
        var wmStr = store.GetState(ctx.SourceId, key);
        var wm = wmStr is not null
            ? DateTimeOffset.Parse(wmStr, CultureInfo.InvariantCulture)
            : DateTimeOffset.UtcNow.AddDays(-Math.Max(1, Config.ChatHistoryDays));
        var newWm = wm;
        var touched = new HashSet<string>();
        var url = $"chats/{chatId}/messages?$top=50&$orderby=lastModifiedDateTime desc&$filter=lastModifiedDateTime gt {wm.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}";
        ctx.Report($"{chatName} : lecture des messages…");
        await foreach (var m in _graph.GetPagedAsync(url, ct))
        {
            var lm = m.D("lastModifiedDateTime");
            if (lm > newWm) newWm = lm;
            if (m.Has("deletedDateTime"))
            {
                var d = store.DeleteMessage(ctx.SourceId, m.S("id"));
                if (d is not null) touched.Add(d);
                continue;
            }
            if (m.S("messageType") is not ("message" or "")) continue;
            var day = m.D("createdDateTime").ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var doc = $"chatday:{chatId}:{day}";
            StoreMessage(ctx, m, doc);
            touched.Add(doc);
        }
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        foreach (var doc in touched)
        {
            var day = DateTime.ParseExact(doc[^10..], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            RebuildDoc(ctx, doc, chain, title: $"{chatName} — {day.ToString("dddd d MMMM yyyy", fr)}", location: $"Conversations › {chatName}", rootWebUrl: null);
        }
        store.SetState(ctx.SourceId, key, newWm.ToString("o", CultureInfo.InvariantCulture));
    }

    // ───────────────────────────── Ouverture, aperçu, miniatures ─────────────────────────────

    private static readonly Dictionary<string, string> OfficeSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "ms-word", [".docx"] = "ms-word", [".docm"] = "ms-word", [".dotx"] = "ms-word", [".rtf"] = "ms-word",
        [".xls"] = "ms-excel", [".xlsx"] = "ms-excel", [".xlsm"] = "ms-excel", [".xlsb"] = "ms-excel", [".csv"] = "ms-excel",
        [".ppt"] = "ms-powerpoint", [".pptx"] = "ms-powerpoint", [".pptm"] = "ms-powerpoint", [".ppsx"] = "ms-powerpoint",
        [".vsdx"] = "ms-visio", [".vsd"] = "ms-visio",
    };

    public override Task<OpenAction?> GetOpenActionAsync(IndexedItem item, CancellationToken ct)
    {
        if (item.Kind is ItemKind.SharePoint or ItemKind.OneDrive && _openOfficeInDesktop()
            && OfficeSchemes.TryGetValue(item.Extension, out var scheme) && item.GetExtra("webDavUrl") is { Length: > 0 } dav)
            return Task.FromResult<OpenAction?>(new OpenAction(OpenKind.Url, $"{scheme}:ofe|u|{dav}", item.OpenUri));
        return base.GetOpenActionAsync(item, ct);
    }

    public override async Task<OpenAction?> GetContainerActionAsync(IndexedItem item, CancellationToken ct)
    {
        if (item.Kind is ItemKind.SharePoint or ItemKind.OneDrive && item.GetExtra("driveId") is { } d && item.GetExtra("parentId") is { } p)
        {
            var parent = await _graph.GetAsync($"drives/{d}/items/{p}?$select=webUrl", ct);
            var u = parent.S("webUrl");
            return u.Length > 0 ? new OpenAction(OpenKind.Url, u) : null;
        }
        if (item.Kind == ItemKind.Teams && item.Id.StartsWith("thread:"))
        {
            var parts = item.Id.Split(':');
            if (parts.Length >= 4)
                return new OpenAction(OpenKind.Url, $"https://teams.microsoft.com/l/channel/{Uri.EscapeDataString(parts[2])}/x?groupId={parts[1]}");
        }
        return null;
    }

    public override async Task<string?> GetPreviewFileAsync(IndexedItem item, CancellationToken ct)
    {
        if (item.Kind is not (ItemKind.SharePoint or ItemKind.OneDrive)) return null;
        if (item.GetExtra("driveId") is not { } d || item.GetExtra("itemId") is not { } i) return null;
        if (item.Size > MaxDownloadBytes * 4) return null;
        var dir = Path.Combine(_cacheDir ?? Path.GetTempPath(), "apercus");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, CacheKey(item.Id + item.Version) + "-" + SafeFileName(item.Title));
        if (!File.Exists(path)) await _graph.DownloadToFileAsync($"drives/{d}/items/{i}/content", path, ct);
        return path;
    }

    private string? _cacheDir;
    public void SetCacheDir(string dir) => _cacheDir = dir;

    public override async Task<byte[]?> GetThumbnailAsync(IndexedItem item, CancellationToken ct)
    {
        if (item.GetExtra("driveId") is not { } d || item.GetExtra("itemId") is not { } i) return null;
        try { return await _graph.GetBytesAsync($"drives/{d}/items/{i}/thumbnails/0/medium/content", ct, 4 * 1024 * 1024); }
        catch (GraphException) { return null; }
    }

    public void Dispose() => _graph.Dispose();
}
