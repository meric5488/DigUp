using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;
using Fouineur.Core.Extraction;
using Fouineur.Core.Model;

namespace Fouineur.Core.Connectors.Zimbra;

/// <summary>
/// Zimbra : boîte personnelle, comptes délégués (« boîtes supplémentaires ») et dossiers partagés montés.
///
/// Identifiants de nœuds :
///   acct:{compte}               racine d'une boîte
///   zf:{compte}:{idDossier}     dossier (y compris dossier partagé monté)
/// Identifiants d'éléments : zm:{compte}:{idMessage} (idMessage = « zid:id » pour un dossier monté).
/// </summary>
public sealed class ZimbraConnector : ConnectorBase, IDisposable
{
    public const int TrashId = 3, JunkId = 4;
    private readonly ZimbraClient _client;
    public int FetchBatchSize { get; set; } = 40;
    private string? _cacheDir;

    public ZimbraConnector(SourceConfig config, ZimbraClient client) : base(config) { _client = client; }

    public ZimbraClient Client => _client;
    public void SetCacheDir(string dir) => _cacheDir = dir;

    public IReadOnlyList<string> Accounts =>
        new[] { Config.UserName }.Concat(Config.ExtraAccounts.Where(a => !string.IsNullOrWhiteSpace(a)))
            .Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string AccountNode(string account) => "acct:" + account.ToLowerInvariant();
    public static string FolderNode(string account, string folderId) => $"zf:{account.ToLowerInvariant()}:{folderId}";

    /// <summary>Sélection par défaut pour une boîte : tout sauf la corbeille et le courrier indésirable.</summary>
    public void ApplyDefaultSelection(string account)
    {
        var a = AccountNode(account);
        Config.Rules.Set(Array.Empty<string>(), a, true, account);
        Config.Rules.Set(new[] { a }, FolderNode(account, TrashId.ToString()), false, "Corbeille");
        Config.Rules.Set(new[] { a }, FolderNode(account, JunkId.ToString()), false, "Courrier indésirable");
    }

    // ───────────────────────────── Dossiers ─────────────────────────────

    public sealed record ZFolder(string Id, string Name, string Path, string? ParentId, bool IsMount, string? OwnerZid, string? RemoteId, string? Owner, long Count, List<string> Chain);

    private static readonly HashSet<string> MailViews = new(StringComparer.OrdinalIgnoreCase) { "", "message", "conversation" };

    /// <summary>Arborescence des dossiers de messagerie d'un compte (chaînes de nœuds calculées).</summary>
    public async Task<Dictionary<string, ZFolder>> GetFoldersAsync(string account, CancellationToken ct)
    {
        var resp = await _client.CallAsync("GetFolderRequest", new JsonObject { ["_jsns"] = "urn:zimbraMail" }, account, ct);
        var map = new Dictionary<string, ZFolder>();
        var acct = AccountNode(account);
        void Walk(JsonObject f, string? parentId, List<string> parentChain, bool isLink)
        {
            var id = f.S("id");
            var view = f.S("view");
            bool isRoot = id == "1";
            if (!isRoot && !MailViews.Contains(view)) return;
            var chain = isRoot ? new List<string> { acct } : new List<string>(parentChain) { FolderNode(account, id) };
            if (!isRoot)
            {
                var name = DisplayName(f);
                map[id] = new ZFolder(id, name, f.S("absFolderPath"), parentId == "1" ? null : parentId, isLink,
                    isLink ? f.S("zid") : null, isLink ? f.S("rid") : null, isLink ? f.S("owner") : null, f.L("n"), chain);
            }
            foreach (var key in new[] { "folder", "link" })
                if (f[key] is JsonArray arr)
                    foreach (var c in arr.OfType<JsonObject>())
                        Walk(c, id, chain, key == "link");
        }
        if (resp["folder"] is JsonArray roots)
            foreach (var r in roots.OfType<JsonObject>()) Walk(r, null, new List<string>(), false);
        return map;
    }

    private static string DisplayName(JsonObject f) => f.S("id") switch
    {
        "2" => "Boîte de réception", "3" => "Corbeille", "4" => "Courrier indésirable", "5" => "Envoyés", "6" => "Brouillons",
        _ => f.S("name"),
    };

    public override async Task<IReadOnlyList<SourceNode>> GetChildrenAsync(SourceNode? parent, CancellationToken ct)
    {
        if (parent is null)
            return Accounts.Select((a, i) => new SourceNode
            {
                Id = AccountNode(a), Name = a, Icon = NodeIcon.Mailbox, Tag = a,
                Detail = i == 0 ? "boîte personnelle" : "boîte supplémentaire",
            }).ToList();

        var account = parent.Tag as string ?? Accounts.FirstOrDefault(a => parent.Chain[0] == AccountNode(a)) ?? Config.UserName;
        var folders = await GetFoldersAsync(account, ct);
        string? parentFolderId = parent.Id.StartsWith("acct:") ? null : parent.Id[(parent.Id.LastIndexOf(':') + 1)..];
        // pour un identifiant de dossier monté du type « zf:compte:257 », l'identifiant local est après le dernier « : »
        return folders.Values
            .Where(f => f.ParentId == parentFolderId)
            .OrderBy(f => SortKey(f.Id)).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new SourceNode
            {
                Id = FolderNode(account, f.Id), Name = f.Name, Ancestors = parent.Chain, Tag = account,
                Icon = f.IsMount ? NodeIcon.SharedFolder : NodeIcon.MailFolder,
                HasChildren = folders.Values.Any(c => c.ParentId == f.Id),
                Detail = f.IsMount ? $"partagé par {f.Owner}" : f.Count > 0 ? $"{f.Count} messages" : null,
            }).ToList();
    }

    private static int SortKey(string id) => id switch { "2" => 0, "5" => 1, "6" => 2, "4" => 8, "3" => 9, _ => 5 };

    // ───────────────────────────── Synchronisation ─────────────────────────────

    public override async Task SyncAsync(SyncContext ctx, CancellationToken ct)
    {
        ctx.Prune();
        foreach (var account in Accounts)
        {
            ct.ThrowIfCancellationRequested();
            var acctNode = AccountNode(account);
            if (!ctx.Rules.AnyIncludedUnder(Array.Empty<string>(), acctNode)) continue;
            try { await SyncAccount(ctx, account, ct); }
            catch (OperationCanceledException) { throw; }
            catch (ZimbraException e) when (e.Code.StartsWith("account.") || e.Code is "service.PERM_DENIED")
            {
                ctx.Warn($"{account} : accès refusé ({e.Code}). La délégation est-elle accordée sur toute la boîte ?");
            }
            catch (Exception e) when (e is ZimbraException or HttpRequestException)
            {
                ctx.Warn($"{account} : {e.Message}");
            }
        }
    }

    private async Task SyncAccount(SyncContext ctx, string account, CancellationToken ct)
    {
        var store = ctx.Store;
        var acctNode = AccountNode(account);
        ctx.Report($"{account} : lecture des dossiers…");
        var folders = await GetFoldersAsync(account, ct);
        store.SetTreeNode(ctx.SourceId, acctNode, null, account);
        foreach (var f in folders.Values)
            store.SetTreeNode(ctx.SourceId, FolderNode(account, f.Id), f.ParentId is null ? acctNode : FolderNode(account, f.ParentId), f.Name);

        var tokenKey = "token:" + account.ToLowerInvariant();
        if (ctx.ScopeChanged(acctNode, new[] { acctNode })) store.SetState(ctx.SourceId, tokenKey, null);

        var token = store.GetState(ctx.SourceId, tokenKey);
        if (token is null) await FullSync(ctx, account, folders, tokenKey, ct);
        else await DeltaSync(ctx, account, folders, tokenKey, token, ct);

        // Dossiers partagés montés : pas couverts par SyncRequest, on les relit par recherche.
        foreach (var mount in folders.Values.Where(f => f.IsMount && ctx.Rules.IsIncluded(f.Chain)))
            await SyncMount(ctx, account, mount, ct);
    }

    private async Task FullSync(SyncContext ctx, string account, Dictionary<string, ZFolder> folders, string tokenKey, CancellationToken ct)
    {
        ctx.Report($"{account} : inventaire de la boîte…");
        var resp = await _client.CallAsync("SyncRequest", new JsonObject { ["_jsns"] = "urn:zimbraMail", ["typed"] = 1 }, account, ct);
        var token = resp["token"]?.ToString();
        var wanted = new Dictionary<string, string>(); // msgId → folderId
        void Walk(JsonObject f)
        {
            var fid = f.S("id");
            if (f["m"] is JsonArray ms && folders.TryGetValue(fid, out var zf) && ctx.Rules.IsIncluded(zf.Chain))
                foreach (var m in ms.OfType<JsonObject>())
                    foreach (var id in m.S("ids").Split(',', StringSplitOptions.RemoveEmptyEntries))
                        wanted[id] = fid;
            if (f["folder"] is JsonArray subs) foreach (var s in subs.OfType<JsonObject>()) Walk(s);
        }
        if (resp["folder"] is JsonArray roots) foreach (var r in roots.OfType<JsonObject>()) Walk(r);

        var prefix = $"zm:{account.ToLowerInvariant()}:";
        var known = ctx.Store.GetVersions(ctx.SourceId, prefix);
        // éléments locaux disparus (on laisse les messages de dossiers montés, identifiants « zid:id »)
        using (ctx.Store.Batch())
            foreach (var id in known.Keys)
            {
                var local = id[prefix.Length..];
                if (!local.Contains(':') && !wanted.ContainsKey(local)) ctx.Delete(id);
            }
        var toFetch = wanted.Keys.Where(id => !known.ContainsKey(prefix + id)).ToList();
        await FetchAndIndex(ctx, account, folders, toFetch, null, ct);
        if (token is not null) ctx.Store.SetState(ctx.SourceId, tokenKey, token);
    }

    private async Task DeltaSync(SyncContext ctx, string account, Dictionary<string, ZFolder> folders, string tokenKey, string token, CancellationToken ct)
    {
        var prefix = $"zm:{account.ToLowerInvariant()}:";
        for (int round = 0; round < 50; round++)
        {
            JsonObject resp;
            try
            {
                resp = await _client.CallAsync("SyncRequest", new JsonObject { ["_jsns"] = "urn:zimbraMail", ["token"] = token, ["typed"] = 1 }, account, ct);
            }
            catch (ZimbraException e) when (e.Code == "mail.MUST_RESYNC")
            {
                ctx.Log?.Invoke($"{account} : resynchronisation complète demandée par le serveur.");
                await FullSync(ctx, account, folders, tokenKey, ct);
                return;
            }
            var toFetch = new List<string>();
            if (resp["m"] is JsonArray ms)
                foreach (var m in ms.OfType<JsonObject>())
                {
                    var id = m.S("id");
                    var fid = m.S("l");
                    if (folders.TryGetValue(fid, out var f) && ctx.Rules.IsIncluded(f.Chain))
                    {
                        var version = $"{fid}-{m.S("ms")}-{m.S("md")}";
                        if (ctx.Store.GetVersion(prefix + id) != version) toFetch.Add(id);
                    }
                    else ctx.Delete(prefix + id);
                }
            if (resp["deleted"] is JsonArray dels)
                foreach (var d in dels.OfType<JsonObject>())
                {
                    var ids = d.S("ids");
                    if (d["m"] is JsonArray dm) ids = string.Join(',', dm.OfType<JsonObject>().Select(x => x.S("ids")));
                    foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries)) ctx.Delete(prefix + id);
                }
            await FetchAndIndex(ctx, account, folders, toFetch, null, ct);
            token = resp["token"]?.ToString() ?? token;
            ctx.Store.SetState(ctx.SourceId, tokenKey, token);
            if (!(resp["more"] is JsonNode more && (more.ToString() is "true" or "1"))) break;
        }
    }

    private async Task SyncMount(SyncContext ctx, string account, ZFolder mount, CancellationToken ct)
    {
        ctx.Report($"{account} › {mount.Name} (partagé) : lecture…");
        var prefix = $"zm:{account.ToLowerInvariant()}:";
        var ids = new List<string>();
        int offset = 0;
        while (true)
        {
            var resp = await _client.CallAsync("SearchRequest", new JsonObject
            {
                ["_jsns"] = "urn:zimbraMail",
                ["query"] = $"inid:\"{mount.Id}\"",
                ["types"] = "message",
                ["limit"] = 500,
                ["offset"] = offset,
                ["sortBy"] = "dateDesc",
            }, account, ct);
            if (resp["m"] is JsonArray ms)
                foreach (var m in ms.OfType<JsonObject>()) ids.Add(m.S("id"));
            if (!(resp["more"] is JsonNode more && more.ToString() is "true" or "1")) break;
            offset += 500;
            if (offset > 200_000) break;
        }
        var set = ids.ToHashSet();
        var mountChain = mount.Chain;
        var known = ctx.Store.GetAncestors(ctx.SourceId).Where(x => x.Id.StartsWith(prefix) && x.Ancestors.Contains(FolderNode(account, mount.Id))).Select(x => x.Id).ToList();
        foreach (var k in known) if (!set.Contains(k[prefix.Length..])) ctx.Delete(k);
        var toFetch = ids.Where(id => ctx.Store.GetVersion(prefix + id) is null).ToList();
        await FetchAndIndex(ctx, account, new Dictionary<string, ZFolder>(), toFetch, mount, ct);
    }

    private async Task FetchAndIndex(SyncContext ctx, string account, Dictionary<string, ZFolder> folders, List<string> ids, ZFolder? mount, CancellationToken ct)
    {
        int done = 0;
        for (int i = 0; i < ids.Count; i += FetchBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(i).Take(FetchBatchSize).ToList();
            var reqs = new JsonArray();
            for (int k = 0; k < batch.Count; k++)
                reqs.Add(new JsonObject
                {
                    ["_jsns"] = "urn:zimbraMail", ["requestId"] = k.ToString(CultureInfo.InvariantCulture),
                    ["m"] = new JsonObject { ["id"] = batch[k], ["html"] = 0, ["max"] = 250_000, ["needExp"] = 1 },
                });
            var resp = await _client.CallAsync("BatchRequest", new JsonObject
            {
                ["_jsns"] = "urn:zimbra", ["onerror"] = "continue", ["GetMsgRequest"] = reqs,
            }, account, ct);
            if (resp["GetMsgResponse"] is JsonArray results)
                foreach (var r in results.OfType<JsonObject>())
                    if (r["m"] is JsonArray ma && ma.FirstOrDefault() is JsonObject m)
                    {
                        var item = BuildItem(ctx, account, folders, m, mount);
                        if (item is not null) ctx.Upsert(item);
                    }
            if (resp["Fault"] is JsonArray faults && faults.Count > 0)
                ctx.Warn($"{account} : {faults.Count} message(s) illisible(s) dans un lot.");
            done += batch.Count;
            ctx.Report($"{account} : {done}/{ids.Count} messages", done, ids.Count);
        }
    }

    internal IndexedItem? BuildItem(SyncContext ctx, string account, Dictionary<string, ZFolder> folders, JsonObject m, ZFolder? mount)
    {
        var id = m.S("id");
        var fid = m.S("l");
        List<string> chain;
        string folderPath;
        if (mount is not null) { chain = mount.Chain; folderPath = mount.Name; }
        else if (folders.TryGetValue(fid, out var f)) { chain = f.Chain; folderPath = string.Join(" › ", f.Chain.Skip(1).Select(n => folders.Values.FirstOrDefault(x => FolderNode(account, x.Id) == n)?.Name ?? "")); }
        else return null;
        if (!ctx.Rules.IsIncluded(chain)) return null;

        var from = Addresses(m, "f").FirstOrDefault() ?? "";
        var to = string.Join(", ", Addresses(m, "t"));
        var cc = string.Join(", ", Addresses(m, "c"));
        var sb = new StringBuilder();
        var attachments = new List<string>();
        CollectParts(m["mp"] as JsonArray, sb, attachments);
        var body = sb.ToString();
        if (body.Trim().Length == 0) body = m.S("fr");
        if (attachments.Count > 0) body += "\n\n" + string.Join("\n", attachments.Select(a => "[Pièce jointe] " + a));
        var header = $"De : {from}\nÀ : {to}" + (cc.Length > 0 ? $"\nCc : {cc}" : "") + "\n\n";

        var item = new IndexedItem
        {
            Id = $"zm:{account.ToLowerInvariant()}:{id}",
            SourceId = ctx.SourceId,
            Kind = ItemKind.Mail,
            Title = string.IsNullOrWhiteSpace(m.S("su")) ? "(sans objet)" : m.S("su"),
            Author = from,
            Date = DateTimeOffset.FromUnixTimeMilliseconds(m.L("d")),
            Location = $"{account} › {folderPath}",
            OpenUri = "",
            Extension = ".eml",
            Size = m.L("s"),
            Ancestors = chain,
            Version = $"{fid}-{m.S("ms")}-{m.S("md")}",
            Body = TextExtractors.Normalize(header + body),
        };
        item.SetExtra("account", account).SetExtra("msgId", id).SetExtra("folderId", fid)
            .SetExtra("to", to).SetExtra("cc", cc.Length > 0 ? cc : null)
            .SetExtra("attachments", attachments.Count > 0 ? string.Join("|", attachments) : null);
        return item;
    }

    private static IEnumerable<string> Addresses(JsonObject m, string type) =>
        (m["e"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(e => e.S("t") == type)
            .Select(e => e.S("p").Length > 0 ? $"{e.S("p")} <{e.S("a")}>" : e.S("a"));

    private static void CollectParts(JsonArray? parts, StringBuilder sb, List<string> attachments)
    {
        if (parts is null) return;
        foreach (var p in parts.OfType<JsonObject>())
        {
            var cd = p.S("cd");
            var fn = p.S("filename");
            if (cd == "attachment" || (fn.Length > 0 && !p.Has("body")))
            {
                if (fn.Length > 0) attachments.Add(fn);
                continue;
            }
            if (p.Has("body") && p.Has("content"))
            {
                var c = p.S("content");
                sb.Append(p.S("ct").StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ? HtmlText.ToText(c) : c).Append('\n');
            }
            CollectParts(p["mp"] as JsonArray, sb, attachments);
        }
    }

    // ───────────────────────────── Ouverture ─────────────────────────────

    public string WebmailUrl(IndexedItem item) => Config.WebmailTemplate
        .Replace("{base}", _client.BaseUrl)
        .Replace("{id}", Uri.EscapeDataString(item.GetExtra("msgId") ?? ""))
        .Replace("{account}", Uri.EscapeDataString(item.GetExtra("account") ?? ""))
        .Replace("{folder}", Uri.EscapeDataString(item.GetExtra("folderId") ?? ""));

    /// <summary>Ouvre le message dans l'application de messagerie par défaut (fichier .eml téléchargé), sinon le webmail.</summary>
    public override async Task<OpenAction?> GetOpenActionAsync(IndexedItem item, CancellationToken ct)
    {
        var path = await DownloadEmlAsync(item, ct);
        return path is not null ? new OpenAction(OpenKind.File, path, WebmailUrl(item)) : new OpenAction(OpenKind.Url, WebmailUrl(item));
    }

    public override Task<OpenAction?> GetContainerActionAsync(IndexedItem item, CancellationToken ct) =>
        Task.FromResult<OpenAction?>(new OpenAction(OpenKind.Url, WebmailUrl(item)));

    public async Task<string?> DownloadEmlAsync(IndexedItem item, CancellationToken ct)
    {
        var account = item.GetExtra("account");
        var msgId = item.GetExtra("msgId");
        if (msgId is null) return null;
        var dir = Path.Combine(_cacheDir ?? Path.GetTempPath(), "mails");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, CacheKey(item.Id + item.Version) + "-" + SafeFileName(item.Title) + ".eml");
        if (!File.Exists(path))
        {
            var bytes = await _client.DownloadRawAsync(account, msgId, ct);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
        return path;
    }

    public void Dispose() => _client.Dispose();
}

internal static class ZJ
{
    public static string S(this JsonNode? n, string key) => n?[key] switch
    {
        null => "",
        JsonValue v => v.ToString(),
        JsonObject o => o["_content"]?.ToString() ?? "",
        _ => "",
    };
    public static long L(this JsonNode? n, string key) => long.TryParse(n.S(key), out var l) ? l : 0;
    public static bool Has(this JsonNode? n, string key) => n is JsonObject o && o.ContainsKey(key) && o[key] is not null;
}
