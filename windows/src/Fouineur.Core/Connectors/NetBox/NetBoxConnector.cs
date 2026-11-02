using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;
using Fouineur.Core.Model;

namespace Fouineur.Core.Connectors.NetBox;

/// <summary>
/// NetBox en lecture seule : équipements, baies, sites, machines virtuelles, clusters, adresses IP, préfixes,
/// VLAN, VRF, circuits, fournisseurs, locataires.
///
/// Nœuds du sélecteur : netbox:{groupe} › netbox:{module}/{objet} (ex. netbox:dcim › netbox:dcim/devices).
/// Identifiants : netbox:{module}/{objet}:{id}. Synchro incrémentale par last_updated (filtre last_updated__gte).
/// Seules des requêtes GET sont émises.
/// </summary>
public sealed class NetBoxConnector : ConnectorBase, IDisposable
{
    private readonly NetBoxClient _client;
    public NetBoxClient Client => _client;

    public NetBoxConnector(SourceConfig config, NetBoxClient client) : base(config) { _client = client; }

    /// <summary>Champ affiché : clé JSON → libellé.</summary>
    internal sealed record Field(string Key, string Label);

    internal sealed record TypeInfo(string Endpoint, string Label, string Singular, string Group, Field[] Fields)
    {
        public string Node => "netbox:" + Endpoint;
        public string GroupNode => "netbox:" + Group;
        public string[] Chain => new[] { GroupNode, Node };
    }

    internal static readonly (string Id, string Label)[] Groups =
    {
        ("dcim", "Équipements et sites"), ("virtualization", "Virtualisation"), ("ipam", "Adressage IP"),
        ("circuits", "Circuits"), ("tenancy", "Locataires"),
    };

    private static Field F(string key, string label) => new(key, label);
    private static readonly Field Status = F("status", "Statut"), Tenant = F("tenant", "Locataire"), Site = F("site", "Site"),
        Role = F("role", "Rôle"), Scope = F("scope", "Périmètre");

    internal static readonly TypeInfo[] Types =
    {
        new("dcim/devices", "Équipements", "Équipement", "dcim", new[]
        {
            Status, Role, F("device_role", "Rôle"), F("device_type", "Modèle"), F("platform", "Plateforme"), Site, F("location", "Emplacement"),
            F("rack", "Baie"), F("position", "Position (U)"), F("face", "Face"), F("serial", "N° de série"), F("asset_tag", "N° d'inventaire"),
            F("primary_ip4", "IP principale"), F("primary_ip6", "IPv6 principale"), F("oob_ip", "IP hors bande"), F("cluster", "Cluster"),
            F("virtual_chassis", "Châssis virtuel"), Tenant,
        }),
        new("dcim/racks", "Baies", "Baie", "dcim", new[]
        {
            Status, Site, F("location", "Emplacement"), Role, F("u_height", "Hauteur (U)"), F("facility_id", "Réf. salle"),
            F("serial", "N° de série"), F("asset_tag", "N° d'inventaire"), Tenant,
        }),
        new("dcim/sites", "Sites", "Site", "dcim", new[]
        {
            Status, F("region", "Région"), F("group", "Groupe"), F("facility", "Salle / bâtiment"), F("time_zone", "Fuseau"),
            F("physical_address", "Adresse"), F("shipping_address", "Adresse de livraison"), Tenant,
        }),
        new("virtualization/virtual-machines", "Machines virtuelles", "Machine virtuelle", "virtualization", new[]
        {
            Status, Role, F("platform", "Plateforme"), F("cluster", "Cluster"), Site, F("device", "Hôte"), F("vcpus", "vCPU"),
            F("memory", "Mémoire (Mo)"), F("disk", "Disque (Go)"), F("primary_ip4", "IP principale"), F("primary_ip6", "IPv6 principale"), Tenant,
        }),
        new("virtualization/clusters", "Clusters", "Cluster", "virtualization", new[]
        {
            F("type", "Type"), F("group", "Groupe"), Status, Site, Scope, Tenant,
        }),
        new("ipam/ip-addresses", "Adresses IP", "Adresse IP", "ipam", new[]
        {
            Status, Role, F("assigned_object", "Affectée à"), F("dns_name", "Nom DNS"), F("vrf", "VRF"), F("nat_inside", "NAT (interne)"), Tenant,
        }),
        new("ipam/prefixes", "Préfixes", "Préfixe", "ipam", new[]
        {
            Status, Role, F("vlan", "VLAN"), F("vrf", "VRF"), Site, Scope, F("is_pool", "Pool"), Tenant,
        }),
        new("ipam/vlans", "VLAN", "VLAN", "ipam", new[]
        {
            F("vid", "VID"), F("name", "Nom"), Status, Role, F("group", "Groupe"), Site, Tenant,
        }),
        new("ipam/vrfs", "VRF", "VRF", "ipam", new[] { F("rd", "RD"), Tenant }),
        new("circuits/circuits", "Circuits", "Circuit", "circuits", new[]
        {
            F("cid", "Référence"), F("provider", "Opérateur"), F("provider_account", "Compte"), F("type", "Type"), Status,
            F("commit_rate", "Débit garanti (kb/s)"), F("install_date", "Mise en service"), F("termination_date", "Fin de contrat"),
            F("termination_a", "Extrémité A"), F("termination_z", "Extrémité Z"), Tenant,
        }),
        new("circuits/providers", "Opérateurs", "Opérateur", "circuits", Array.Empty<Field>()),
        new("tenancy/tenants", "Locataires", "Locataire", "tenancy", new[] { F("group", "Groupe") }),
    };

    /// <summary>Sélection par défaut : équipements, baies, sites, VM, adresses IP, préfixes, VLAN.</summary>
    public void ApplyDefaultSelection()
    {
        foreach (var e in new[] { "dcim/devices", "dcim/racks", "dcim/sites", "virtualization/virtual-machines", "ipam/ip-addresses", "ipam/prefixes", "ipam/vlans" })
        {
            var t = Types.First(x => x.Endpoint == e);
            Config.Rules.Set(new[] { t.GroupNode }, t.Node, true, t.Label);
        }
    }

    // ───────────────────────────── Sélecteur ─────────────────────────────

    public override Task<IReadOnlyList<SourceNode>> GetChildrenAsync(SourceNode? parent, CancellationToken ct)
    {
        IReadOnlyList<SourceNode> list = parent is null
            ? Groups.Select(g => new SourceNode { Id = "netbox:" + g.Id, Name = g.Label, HasChildren = true, Icon = NodeIcon.Network }).ToList()
            : Types.Where(t => t.GroupNode == parent.Id).Select(t => new SourceNode
            {
                Id = t.Node, Name = t.Label, HasChildren = false, Icon = NodeIcon.Network, Ancestors = new[] { t.GroupNode },
            }).ToList();
        return Task.FromResult(list);
    }

    // ───────────────────────────── Synchronisation ─────────────────────────────

    public override async Task SyncAsync(SyncContext ctx, CancellationToken ct)
    {
        ctx.Prune();
        foreach (var t in Types)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Rules.IsIncluded(t.Chain)) continue;
            try { await SyncType(ctx, t, ct); }
            catch (NetBoxException e) when (e.Status is 403 or 404)
            {
                ctx.Warn($"{t.Label} : {e.Message}");
            }
            catch (NetBoxException e) when (e.Status >= 500)
            {
                ctx.Warn($"{t.Label} : {e.Message}");
            }
        }
    }

    private async Task SyncType(SyncContext ctx, TypeInfo t, CancellationToken ct)
    {
        var store = ctx.Store;
        var wmKey = "wm:" + t.Endpoint;
        if (ctx.ScopeChanged("type:" + t.Endpoint, t.Chain)) store.SetState(ctx.SourceId, wmKey, null);
        var watermark = store.GetState(ctx.SourceId, wmKey);
        string? newest = null;
        int n = 0;
        ctx.Report($"NetBox › {t.Label} : lecture des modifications…");

        var query = watermark is null ? "ordering=id" : "ordering=id&last_updated__gte=" + Uri.EscapeDataString(watermark);
        await foreach (var o in _client.ListAsync(t.Endpoint, query, ct))
        {
            var mod = S(o, "last_updated");
            if (mod.Length > 0 && (newest is null || string.CompareOrdinal(mod, newest) > 0)) newest = mod;
            var docId = $"netbox:{t.Endpoint}:{S(o, "id")}";
            if (mod.Length > 0 && store.GetVersion(docId) == mod) continue;
            ctx.Upsert(Build(ctx, t, o));
            if (++n % 100 == 0) ctx.Report($"NetBox › {t.Label} : {n} élément(s) mis à jour", n);
        }
        if (newest is not null) store.SetState(ctx.SourceId, wmKey, newest);

        // Suppressions : liste brève des identifiants.
        var ids = new HashSet<string>();
        var old = _client.PageSize;
        _client.PageSize = 1000;
        try { await foreach (var o in _client.ListAsync(t.Endpoint, "brief=1&ordering=id", ct)) ids.Add(S(o, "id")); }
        finally { _client.PageSize = old; }
        var prefix = $"netbox:{t.Endpoint}:";
        foreach (var known in store.GetVersions(ctx.SourceId, prefix).Keys)
            if (!ids.Contains(known[prefix.Length..])) ctx.Delete(known);
    }

    internal IndexedItem Build(SyncContext ctx, TypeInfo t, JsonObject o)
    {
        var id = S(o, "id");
        var sb = new StringBuilder();
        foreach (var f in t.Fields)
        {
            var v = Render(o[f.Key], f.Key);
            if (v.Length > 0) sb.Append(f.Label).Append(" : ").Append(v).Append('\n');
        }
        if (o["tags"] is JsonArray tags && tags.Count > 0) sb.Append("Étiquettes : ").Append(Render(tags, "tags")).Append('\n');
        if (o["custom_fields"] is JsonObject cf)
            foreach (var (k, v) in cf)
                if (Render(v, k) is { Length: > 0 } val) sb.Append(k).Append(" : ").Append(val).Append('\n');
        if (S(o, "description") is { Length: > 0 } desc) sb.Append('\n').Append(desc).Append('\n');
        if (S(o, "comments") is { Length: > 0 } comments) sb.Append('\n').Append(comments).Append('\n');

        var title = S(o, "display") is { Length: > 0 } d ? d : S(o, "name") is { Length: > 0 } nm ? nm : $"{t.Singular} #{id}";
        var where = Render(o["site"], "site") is { Length: > 0 } site ? site : Render(o["scope"], "scope");
        var item = new IndexedItem
        {
            Id = $"netbox:{t.Endpoint}:{id}", SourceId = ctx.SourceId, Kind = ItemKind.NetBox,
            Title = title, Author = Render(o["tenant"], "tenant"),
            Date = DateTimeOffset.TryParse(S(o, "last_updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTimeOffset.MinValue,
            Location = $"NetBox › {t.Label}" + (where.Length > 0 ? " · " + where : ""),
            OpenUri = WebUrl(o), Extension = ".netbox", Ancestors = t.Chain, Version = S(o, "last_updated"),
            Body = sb.ToString(),
        };
        item.SetExtra("netboxType", t.Singular);
        if (Render(o["primary_ip4"], "primary_ip4") is { Length: > 0 } ip) item.SetExtra("ip", ip);
        return item;
    }

    /// <summary>Lien vers la fiche web : display_url (NetBox 4+) ou l'URL d'API sans « /api ».</summary>
    private string WebUrl(JsonObject o)
    {
        var display = S(o, "display_url");
        if (display.Length > 0) return display;
        var api = S(o, "url");
        if (api.Length > 0)
        {
            int i = api.IndexOf("/api/", StringComparison.Ordinal);
            if (i >= 0) return api[..i] + api[(i + 4)..];
        }
        return _client.BaseUrl;
    }

    /// <summary>
    /// Valeur lisible d'un champ NetBox : choix ({value,label}) → label ; objet imbriqué → display/name ;
    /// modèle d'équipement → « Fabricant Modèle » ; interface affectée → « équipement › interface » ; liste → jointure.
    /// </summary>
    internal static string Render(JsonNode? node, string key = "")
    {
        switch (node)
        {
            case null: return "";
            case JsonValue v:
                var s = v.ToString().Trim();
                return s is "null" ? "" : v.TryGetValue<bool>(out var b) ? (b ? "oui" : "non") : s;
            case JsonArray a:
                return string.Join(", ", a.Select(x => Render(x)).Where(x => x.Length > 0));
            case JsonObject o:
                if (o["label"] is JsonValue && o.ContainsKey("value")) return Render(o["label"]);
                if (key == "device_type" && o["manufacturer"] is JsonObject m)
                    return (Render(m) + " " + (S(o, "model") is { Length: > 0 } model ? model : S(o, "display"))).Trim();
                if (key == "assigned_object")
                {
                    var parent = Render(o["device"]) is { Length: > 0 } dev ? dev : Render(o["virtual_machine"]);
                    var name = S(o, "name") is { Length: > 0 } nm ? nm : S(o, "display");
                    return parent.Length > 0 ? $"{parent} › {name}" : name;
                }
                if (key is "termination_a" or "termination_z")
                {
                    var site = Render(o["site"]) is { Length: > 0 } st ? st : Render(o["termination"]);
                    return site.Length > 0 ? site : S(o, "display");
                }
                foreach (var k in new[] { "display", "name", "address", "prefix", "label", "slug" })
                    if (S(o, k) is { Length: > 0 } x) return x;
                return "";
            default: return "";
        }
    }

    internal static string S(JsonObject o, string key) => o[key] is JsonValue v && v.ToString() is var s && s != "null" ? s.Trim() : "";

    // ─── Ouverture ───

    public override Task<OpenAction?> GetContainerActionAsync(IndexedItem item, CancellationToken ct)
    {
        var endpoint = item.Id.Split(':') is { Length: >= 3 } p ? p[1] : "";
        return Task.FromResult<OpenAction?>(new OpenAction(OpenKind.Url, $"{_client.BaseUrl}/{endpoint}/"));
    }

    /// <summary>Vérifie la connexion ; renvoie la version de NetBox et le nombre d'équipements visibles.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        var status = await _client.GetAsync("status/", ct) as JsonObject;
        var version = status?["netbox-version"]?.ToString();
        var devices = await _client.GetAsync("dcim/devices/?limit=1&brief=1", ct) as JsonObject;
        var count = devices?["count"]?.ToString();
        return $"NetBox {version ?? "?"}" + (count is null ? "" : $", {int.Parse(count, CultureInfo.InvariantCulture):N0} équipement(s) visibles");
    }

    public void Dispose() => _client.Dispose();
}
