using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;
using Fouineur.Core.Extraction;
using Fouineur.Core.Model;

namespace Fouineur.Core.Connectors.Glpi;

/// <summary>
/// GLPI : tickets, problèmes et changements (avec suivis, tâches, solutions), base de connaissances, inventaire.
///
/// Nœuds du sélecteur : glpi:Ticket, glpi:Problem, glpi:Change, glpi:KnowbaseItem, glpi:assets › glpi:asset:{Type}.
/// Identifiants d'éléments : glpi:{Type}:{id}. Synchro incrémentale par date_mod (tri décroissant, arrêt au dernier passage).
/// </summary>
public sealed class GlpiConnector : ConnectorBase, IDisposable
{
    private readonly GlpiClient _client;
    public GlpiClient Client => _client;

    public GlpiConnector(SourceConfig config, GlpiClient client) : base(config) { _client = client; }

    internal sealed record TypeInfo(string Type, string Label, string Node, string[] Chain, bool Itil, string? TaskType);

    public const string AssetsRoot = "glpi:assets";

    internal static readonly TypeInfo[] Types =
    {
        new("Ticket", "Tickets", "glpi:Ticket", new[] { "glpi:Ticket" }, true, "TicketTask"),
        new("Problem", "Problèmes", "glpi:Problem", new[] { "glpi:Problem" }, true, "ProblemTask"),
        new("Change", "Changements", "glpi:Change", new[] { "glpi:Change" }, true, "ChangeTask"),
        new("KnowbaseItem", "Base de connaissances", "glpi:KnowbaseItem", new[] { "glpi:KnowbaseItem" }, false, null),
        new("Computer", "Ordinateurs", "glpi:asset:Computer", new[] { AssetsRoot, "glpi:asset:Computer" }, false, null),
        new("NetworkEquipment", "Matériel réseau", "glpi:asset:NetworkEquipment", new[] { AssetsRoot, "glpi:asset:NetworkEquipment" }, false, null),
        new("Printer", "Imprimantes", "glpi:asset:Printer", new[] { AssetsRoot, "glpi:asset:Printer" }, false, null),
        new("Phone", "Téléphones", "glpi:asset:Phone", new[] { AssetsRoot, "glpi:asset:Phone" }, false, null),
        new("Monitor", "Écrans", "glpi:asset:Monitor", new[] { AssetsRoot, "glpi:asset:Monitor" }, false, null),
        new("Peripheral", "Périphériques", "glpi:asset:Peripheral", new[] { AssetsRoot, "glpi:asset:Peripheral" }, false, null),
    };

    /// <summary>Sélection par défaut : tickets et base de connaissances.</summary>
    public void ApplyDefaultSelection()
    {
        Config.Rules.Set(Array.Empty<string>(), "glpi:Ticket", true, "Tickets");
        Config.Rules.Set(Array.Empty<string>(), "glpi:KnowbaseItem", true, "Base de connaissances");
    }

    // ───────────────────────────── Sélecteur ─────────────────────────────

    public override Task<IReadOnlyList<SourceNode>> GetChildrenAsync(SourceNode? parent, CancellationToken ct)
    {
        IReadOnlyList<SourceNode> list = parent is null
            ? Types.Where(t => t.Chain.Length == 1).Select(t => new SourceNode
            {
                Id = t.Node, Name = t.Label, HasChildren = false,
                Icon = t.Itil ? NodeIcon.Ticket : NodeIcon.Knowledge,
            }).Append(new SourceNode { Id = AssetsRoot, Name = "Inventaire", Icon = NodeIcon.Asset }).ToList()
            : parent.Id == AssetsRoot
                ? Types.Where(t => t.Chain.Length == 2).Select(t => new SourceNode
                {
                    Id = t.Node, Name = t.Label, HasChildren = false, Icon = NodeIcon.Asset, Ancestors = new[] { AssetsRoot },
                }).ToList()
                : Array.Empty<SourceNode>();
        return Task.FromResult(list);
    }

    // ───────────────────────────── Synchronisation ─────────────────────────────

    public override async Task SyncAsync(SyncContext ctx, CancellationToken ct)
    {
        ctx.Prune();
        ctx.Log?.Invoke($"[{ctx.Source.Name}] Connexion : {_client.Describe()}");
        _subFailures.Clear();
        _listQuery.Clear();
        try
        {
            foreach (var t in Types)
            {
                ct.ThrowIfCancellationRequested();
                if (!ctx.Rules.IsIncluded(t.Chain)) continue;
                try { await SyncType(ctx, t, ct); }
                catch (GlpiException e) when (e.Status is 401 or 403 && e.Code is "ERROR_RIGHT_MISSING" or "ERROR_ITEM_NOT_FOUND")
                {
                    ctx.Warn($"{t.Label} : droits insuffisants pour ce compte GLPI ({e.Code}).");
                }
                catch (GlpiException e) when (e.Code is "ERROR_RESOURCE_NOT_FOUND_NOR_COMMONDBTM")
                {
                    ctx.Warn($"{t.Label} : type inconnu de cette version de GLPI.");
                }
                catch (GlpiException e) when (e.Status >= 500)
                {
                    // Une erreur du serveur sur un type n'empêche pas de lire les autres.
                    ctx.Warn($"{t.Label} : {e.Message}");
                }
            }
        }
        finally { await _client.KillSessionAsync(); }
    }

    private readonly HashSet<string> _subFailures = new();
    private readonly Dictionary<string, string> _listQuery = new();

    internal static readonly string[] ListQueries =
    {
        "expand_dropdowns=true&sort=date_mod&order=DESC",
        "sort=date_mod&order=DESC",
        "expand_dropdowns=true",
        "",
    };

    private async Task<string> PickListQueryAsync(TypeInfo t, CancellationToken ct)
    {
        if (_listQuery.TryGetValue(t.Type, out var q)) return q;
        GlpiException? last = null;
        foreach (var candidate in ListQueries)
        {
            try
            {
                var sep = candidate.Length > 0 ? "&" : "";
                await _client.GetAsync($"{t.Type}?{candidate}{sep}range=0-0", ct);
                return _listQuery[t.Type] = candidate;
            }
            catch (GlpiException e) when (e.Status >= 500) { last = e; }
        }
        throw last!;
    }

    private static string Fmt(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private async Task SyncType(SyncContext ctx, TypeInfo t, CancellationToken ct)
    {
        var store = ctx.Store;
        // Inventaire : le format a changé (logiciels installés) → nouveau repère, donc une relecture complète, une fois.
        bool isAsset = !t.Itil && t.Type != "KnowbaseItem";
        var wmKey = (isAsset ? "wm:" + AssetFormat + ":" : "wm:") + t.Type;
        if (ctx.ScopeChanged("type:" + t.Type, t.Chain)) store.SetState(ctx.SourceId, wmKey, null);
        var watermark = store.GetState(ctx.SourceId, wmKey);
        string? cutoff = t.Itil && Config.GlpiHistoryDays > 0 ? Fmt(DateTime.Now.AddDays(-Config.GlpiHistoryDays)) : null;
        string? newest = null;
        int n = 0;
        ctx.Report($"{t.Label} : lecture des modifications…");

        // Certaines installations plantent (500) sur expand_dropdowns ou sur le tri : on retombe sur des requêtes plus simples.
        var query = await PickListQueryAsync(t, ct);
        bool sorted = query.Contains("sort=date_mod");
        await foreach (var o in _client.ListAsync(t.Type, query, ct))
        {
            var mod = S(o, "date_mod");
            if (newest is null || string.CompareOrdinal(mod, newest) > 0) newest = mod;
            // Sans tri par date (repli), on parcourt tout : la version (date_mod) évite de relire l'inchangé.
            bool older = watermark is not null && mod.Length > 0 && string.CompareOrdinal(mod, watermark) < 0
                         || cutoff is not null && mod.Length > 0 && string.CompareOrdinal(mod, cutoff) < 0;
            if (older && sorted) break;
            if (older) continue;
            if (o["is_template"] is JsonNode tpl && tpl.ToString() == "1") continue;

            var id = S(o, "id");
            var docId = $"glpi:{t.Type}:{id}";
            if (store.GetVersion(docId) == (isAsset ? mod + "|" + AssetFormat : mod)) continue;
            var item = t.Itil ? await BuildItil(ctx, t, o, ct) : t.Type == "KnowbaseItem" ? BuildKb(ctx, t, o) : await BuildAsset(ctx, t, o, ct);
            ctx.Upsert(item);
            if (++n % 25 == 0) ctx.Report($"{t.Label} : {n} élément(s) mis à jour", n);
        }
        if (newest is not null) store.SetState(ctx.SourceId, wmKey, newest);

        // Suppressions (corbeille ou suppression définitive) : comparaison avec la liste des identifiants.
        var ids = new HashSet<string>();
        var old = _client.PageSize;
        _client.PageSize = 1000;
        try
        {
            await foreach (var o in _client.ListAsync(t.Type, "only_id=true", ct)) ids.Add(S(o, "id"));
        }
        finally { _client.PageSize = old; }
        var prefix = $"glpi:{t.Type}:";
        foreach (var known in store.GetVersions(ctx.SourceId, prefix).Keys)
            if (!ids.Contains(known[prefix.Length..])) ctx.Delete(known);
    }

    // ─── Tickets, problèmes, changements ───

    private static readonly Dictionary<string, string> TicketStatus = new()
    {
        ["1"] = "Nouveau", ["2"] = "En cours (attribué)", ["3"] = "En cours (planifié)", ["4"] = "En attente", ["5"] = "Résolu", ["6"] = "Clos",
    };
    private static readonly Dictionary<string, string> Priority = new()
    {
        ["1"] = "Très basse", ["2"] = "Basse", ["3"] = "Moyenne", ["4"] = "Haute", ["5"] = "Très haute", ["6"] = "Majeure",
    };

    private async Task<IndexedItem> BuildItil(SyncContext ctx, TypeInfo t, JsonObject o, CancellationToken ct)
    {
        var id = S(o, "id");
        var sb = new StringBuilder();
        string status = TicketStatus.TryGetValue(S(o, "status"), out var st) ? st : $"statut {S(o, "status")}";
        var facts = new List<string> { "Statut : " + status };
        if (Priority.TryGetValue(S(o, "priority"), out var pr)) facts.Add("Priorité : " + pr);
        AddFact(facts, "Catégorie", S(o, "itilcategories_id"));
        AddFact(facts, "Entité", S(o, "entities_id"));
        AddFact(facts, "Lieu", S(o, "locations_id"));
        AddFact(facts, "Ouvert le", S(o, "date"));
        AddFact(facts, "Résolu le", S(o, "solvedate"));
        sb.Append(string.Join(" · ", facts)).Append("\n\n");
        sb.Append(Html(S(o, "content"))).Append("\n\n");

        // Suivis, tâches, solutions (sous-éléments du ticket)
        var entries = new List<(string Date, string Label, string Author, string Text)>();
        foreach (var (sub, label) in new[] { ("ITILFollowup", "Suivi"), (t.TaskType!, "Tâche"), ("ITILSolution", "Solution") })
        {
            if (_subFailures.Contains(t.Type + "/" + sub)) continue; // déjà en erreur 500 : inutile d'insister à chaque ticket
            try
            {
                var (json, _) = await _client.GetAsync($"{t.Type}/{id}/{sub}?expand_dropdowns=true&range=0-999", ct);
                foreach (var e in (json as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var date = S(e, "date").Length > 0 ? S(e, "date") : S(e, "date_creation");
                    var priv = S(e, "is_private") == "1" ? " (privé)" : "";
                    entries.Add((date, label + priv, S(e, "users_id"), Html(S(e, "content"))));
                }
            }
            catch (GlpiException ex) when (ex.Status is 400 or 401 or 403 or 404) { /* pas de droit sur ce sous-élément */ }
            catch (GlpiException ex) when (ex.Status >= 500)
            {
                if (_subFailures.Add(t.Type + "/" + sub)) ctx.Warn($"{t.Label} : {label.ToLowerInvariant()}s illisibles ({ex.Message}).");
            }
        }
        foreach (var e in entries.OrderBy(e => e.Date, StringComparer.Ordinal))
            sb.Append(e.Label).Append(" — ").Append(e.Author).Append(" — ").Append(e.Date).Append('\n').Append(e.Text).Append("\n\n");

        var typeLabel = t.Type switch { "Ticket" => "Ticket", "Problem" => "Problème", _ => "Changement" };
        var item = new IndexedItem
        {
            Id = $"glpi:{t.Type}:{id}", SourceId = ctx.SourceId, Kind = ItemKind.Glpi,
            Title = $"#{id} — {S(o, "name")}",
            Author = S(o, "users_id_recipient"),
            Date = ParseDate(S(o, "date_mod")),
            Location = $"GLPI › {t.Label}" + (S(o, "itilcategories_id") is { Length: > 0 } cat ? " › " + cat : "") + " · " + status,
            OpenUri = $"{_client.BaseUrl}/front/{t.Type.ToLowerInvariant()}.form.php?id={id}",
            Extension = ".glpi", Size = entries.Count, Ancestors = t.Chain, Version = S(o, "date_mod"),
            Body = TextExtractors.Normalize(sb.ToString()),
        };
        item.SetExtra("glpiType", typeLabel).SetExtra("status", status);
        return item;
    }

    // ─── Base de connaissances ───

    private IndexedItem BuildKb(SyncContext ctx, TypeInfo t, JsonObject o)
    {
        var id = S(o, "id");
        var item = new IndexedItem
        {
            Id = $"glpi:{t.Type}:{id}", SourceId = ctx.SourceId, Kind = ItemKind.Glpi,
            Title = S(o, "name"), Author = S(o, "users_id"), Date = ParseDate(S(o, "date_mod")),
            Location = "GLPI › Base de connaissances",
            OpenUri = $"{_client.BaseUrl}/front/knowbaseitem.form.php?id={id}",
            Extension = ".glpi", Ancestors = t.Chain, Version = S(o, "date_mod"),
            Body = TextExtractors.Normalize(Html(S(o, "answer"))),
        };
        item.SetExtra("glpiType", "Article de la base de connaissances");
        return item;
    }

    // ─── Inventaire ───

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "links", "is_deleted", "is_template", "template_name", "is_dynamic", "is_recursive", "date_mod", "date_creation",
        "ticket_tco", "is_global", "autoupdatesystems_id", "last_inventory_update", "last_boot",
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "Nom", ["serial"] = "N° de série", ["otherserial"] = "N° d'inventaire", ["contact"] = "Contact", ["contact_num"] = "N° contact",
        ["users_id"] = "Utilisateur", ["groups_id"] = "Groupe", ["users_id_tech"] = "Technicien", ["groups_id_tech"] = "Groupe technique",
        ["locations_id"] = "Lieu", ["states_id"] = "Statut", ["entities_id"] = "Entité", ["manufacturers_id"] = "Fabricant",
        ["comment"] = "Commentaire", ["uuid"] = "UUID", ["networks_id"] = "Réseau",
    };

    /// <summary>Version du contenu des fiches d'inventaire (à changer pour forcer une relecture).</summary>
    internal const string AssetFormat = "sw3";

    internal sealed record AssetDetails(List<string> Software, List<(string Name, string Version)> SoftwareRows, List<string> Ips, List<string> Macs, string? Os);

    /// <summary>
    /// Détails d'un matériel en un appel : logiciels installés (nom + version) et ports réseau (IP, MAC),
    /// puis le système d'exploitation (Item_OperatingSystem).
    /// </summary>
    private async Task<AssetDetails> DetailsAsync(SyncContext ctx, TypeInfo t, string id, CancellationToken ct)
    {
        var software = new List<string>();
        var rows = new List<(string Name, string Version)>();
        var ips = new List<string>();
        var macs = new List<string>();
        string? os = null;
        if (!_subFailures.Contains(t.Type + "/details"))
        {
            try
            {
                var (json, _) = await _client.GetAsync($"{t.Type}/{id}?with_softwares=true&with_networkports=true&expand_dropdowns=true", ct);
                var o = json as JsonObject;
                foreach (var e in (o?["_softwares"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var name = Clean(S(e, "softwares_id"));
                    if (name.Length == 0 || name.All(char.IsDigit)) name = Clean(S(e, "name"));
                    if (name.Length == 0) continue;
                    var ver = Clean(S(e, "softwareversions_id"));
                    if (ver.All(char.IsDigit) || ver == name) ver = "";
                    software.Add(ver.Length > 0 ? $"{name} {ver}" : name);
                    rows.Add((name, ver));
                }
                CollectNetwork(o?["_networkports"], ips, macs);
            }
            catch (GlpiException ex) when (ex.Status is 400 or 401 or 403 or 404) { _subFailures.Add(t.Type + "/details"); }
            catch (GlpiException ex) when (ex.Status >= 500)
            {
                if (_subFailures.Add(t.Type + "/details")) ctx.Warn($"{t.Label} : logiciels et réseau illisibles ({ex.Message}).");
            }
        }
        if (!_subFailures.Contains(t.Type + "/os"))
        {
            try
            {
                var (json, _) = await _client.GetAsync($"{t.Type}/{id}/Item_OperatingSystem?expand_dropdowns=true", ct);
                var parts = new List<string>();
                foreach (var e in (json as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var p = new[] { "operatingsystems_id", "operatingsystemeditions_id", "operatingsystemversions_id", "operatingsystemservicepacks_id" }
                        .Select(k => Clean(S(e, k))).Where(v => v.Length > 0 && !v.All(char.IsDigit)).Distinct().ToList();
                    var arch = Clean(S(e, "operatingsystemarchitectures_id"));
                    var kernel = Clean(S(e, "operatingsystemkernelversions_id"));
                    var text = string.Join(" ", p);
                    if (arch.Length > 0 && !arch.All(char.IsDigit)) text += $" ({arch})";
                    if (kernel.Length > 0 && !kernel.All(char.IsDigit)) text += $" — noyau {kernel}";
                    if (text.Trim().Length > 0) parts.Add(text.Trim());
                }
                if (parts.Count > 0) os = string.Join(" ; ", parts.Distinct());
            }
            catch (GlpiException ex) when (ex.Status is 400 or 401 or 403 or 404) { _subFailures.Add(t.Type + "/os"); }
            catch (GlpiException ex) when (ex.Status >= 500)
            {
                if (_subFailures.Add(t.Type + "/os")) ctx.Warn($"{t.Label} : système d'exploitation illisible ({ex.Message}).");
            }
        }
        static List<string> Tidy(List<string> l) => l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new AssetDetails(
            Tidy(software).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList(),
            rows.Distinct().OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Tidy(ips), Tidy(macs), os);
    }

    /// <summary>
    /// _networkports : { "NetworkPortEthernet": [ { "name", "mac", "NetworkName": { "IPAddress": [ { "name": "10.0.0.5" } ] } } ] }.
    /// On ignore boucle locale et adresses de lien local.
    /// </summary>
    internal static void CollectNetwork(JsonNode? node, List<string> ips, List<string> macs)
    {
        if (node is not JsonObject byType) return;
        foreach (var (_, ports) in byType)
        {
            foreach (var port in (ports as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var mac = Clean(S(port, "mac"));
                if (mac.Length >= 12 && mac != "00:00:00:00:00:00") macs.Add(mac.ToLowerInvariant());
                var names = port["NetworkName"] switch { JsonArray a => a.OfType<JsonObject>(), JsonObject o => new[] { o }, _ => Enumerable.Empty<JsonObject>() };
                foreach (var nn in names)
                    foreach (var ip in (nn["IPAddress"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        var v = S(ip, "name").Trim();
                        if (v.Length == 0 || v.StartsWith("127.") || v == "::1" || v.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) || v.StartsWith("169.254.")) continue;
                        ips.Add(v);
                    }
            }
        }
    }

    private async Task<IndexedItem> BuildAsset(SyncContext ctx, TypeInfo t, JsonObject o, CancellationToken ct)
    {
        var id = S(o, "id");
        var sb = new StringBuilder();
        foreach (var (k, v) in o)
        {
            if (Skip.Contains(k) || v is not JsonValue) continue;
            var val = Clean(v.ToString());
            if (val.Length == 0 || val == "0" && k.EndsWith("_id", StringComparison.Ordinal)) continue;
            var label = Labels.TryGetValue(k, out var l) ? l
                : k.EndsWith("models_id") ? "Modèle" : k.EndsWith("types_id") ? "Type" : k;
            sb.Append(label).Append(" : ").Append(val).Append('\n');
        }
        var d = await DetailsAsync(ctx, t, id, ct);
        var software = d.Software;
        if (d.Os is not null) sb.Append("Système d'exploitation : ").Append(d.Os).Append('\n');
        if (d.Ips.Count > 0) sb.Append("Adresses IP : ").Append(string.Join(", ", d.Ips)).Append('\n');
        if (d.Macs.Count > 0) sb.Append("Adresses MAC : ").Append(string.Join(", ", d.Macs)).Append('\n');
        if (software.Count > 0)
        {
            sb.Append("\nLogiciels installés (").Append(software.Count).Append(") :\n");
            foreach (var sw in software) sb.Append("• ").Append(sw).Append('\n');
        }
        var who = S(o, "users_id").Length > 0 ? S(o, "users_id") : S(o, "contact");
        var item = new IndexedItem
        {
            Id = $"glpi:{t.Type}:{id}", SourceId = ctx.SourceId, Kind = ItemKind.Glpi,
            Title = S(o, "name").Length > 0 ? S(o, "name") : $"{t.Label} #{id}",
            Author = who, Date = ParseDate(S(o, "date_mod")),
            Location = $"GLPI › Inventaire › {t.Label}" + (S(o, "locations_id") is { Length: > 0 } loc ? " · " + loc : ""),
            OpenUri = $"{_client.BaseUrl}/front/{t.Type.ToLowerInvariant()}.form.php?id={id}",
            Extension = ".glpi", Ancestors = t.Chain, Version = S(o, "date_mod") + "|" + AssetFormat,
            Body = sb.ToString(),
        };
        item.SetExtra("glpiType", t.Label.TrimEnd('s'));
        if (software.Count > 0) item.SetExtra("software", software.Count.ToString(CultureInfo.InvariantCulture));
        if (d.Os is not null) item.SetExtra("os", d.Os);
        if (d.Ips.Count > 0) item.SetExtra("ip", string.Join(", ", d.Ips));
        // Données structurées pour le tableau d'inventaire (Fouineur.Core.Inventory).
        item.SetExtra("inv", "1");
        item.SetExtra("lieu", Clean(S(o, "locations_id")) is { Length: > 0 } lieu && !lieu.All(char.IsDigit) ? lieu : null);
        item.SetExtra("serial", Clean(S(o, "serial")) is { Length: > 0 } sn ? sn : null);
        if (d.Macs.Count > 0) item.SetExtra("mac", string.Join(", ", d.Macs));
        if (d.SoftwareRows.Count > 0)
            item.Extra["sw"] = new JsonArray(d.SoftwareRows.Select(r => (JsonNode)new JsonArray(r.Name, r.Version)).ToArray());
        return item;
    }

    // ─── Utilitaires ───

    private static void AddFact(List<string> facts, string label, string value)
    {
        if (value.Length > 0) facts.Add($"{label} : {value}");
    }

    /// <summary>Valeur texte d'un champ ; « &amp;nbsp; » (liste déroulante vide) devient vide.</summary>
    internal static string S(JsonObject o, string key) => o[key] switch
    {
        null => "",
        JsonValue v => Clean(v.ToString()),
        _ => "",
    };

    private static string Clean(string s)
    {
        s = s.Trim();
        return s is "&nbsp;" or "null" ? "" : WebUtility.HtmlDecode(s).Trim();
    }

    /// <summary>Le contenu GLPI est du HTML parfois encodé deux fois (&amp;lt;p&amp;gt;).</summary>
    internal static string Html(string s)
    {
        if (s.Length == 0) return "";
        var decoded = WebUtility.HtmlDecode(s);
        if (decoded.Contains("&lt;", StringComparison.Ordinal)) decoded = WebUtility.HtmlDecode(decoded);
        return HtmlText.ToText(decoded);
    }

    private static DateTimeOffset ParseDate(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? new DateTimeOffset(d) : DateTimeOffset.MinValue;

    // ─── Ouverture ───

    public override Task<OpenAction?> GetContainerActionAsync(IndexedItem item, CancellationToken ct)
    {
        var type = item.Id.Split(':') is { Length: >= 2 } p ? p[1] : "ticket";
        return Task.FromResult<OpenAction?>(new OpenAction(OpenKind.Url, $"{_client.BaseUrl}/front/{type.ToLowerInvariant()}.php"));
    }

    /// <summary>Vérifie la connexion ; renvoie le nombre de tickets visibles.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        try { await _client.EnsureSessionAsync(ct, force: true); }
        catch (GlpiException e) { throw new GlpiException(e.Status, e.Code, e.Message.Split(" : ", 2).Last() + " — " + _client.Describe(), e.Path); }
        var (_, total) = await _client.GetAsync("Ticket?only_id=true&range=0-0", ct);
        await _client.KillSessionAsync();
        return total is { } n ? $"{n:N0} ticket(s) visibles" : "connexion réussie";
    }

    public void Dispose() => _client.Dispose();
}
