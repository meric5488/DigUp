using System.Net;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;
using Fouineur.Core.Connectors.NetBox;
using Fouineur.Core.Model;

namespace Fouineur.Tests;

public static class NetBoxTests
{
    private sealed class NServer
    {
        public string Token = "nbt_abc.def";
        public HashSet<int> Devices = new() { 1, 2 };
        public string Dev1Updated = "2026-10-30T10:00:00.000000Z";
        public int FullReads;
        public List<string> Methods = new();

        private JsonObject Device(int id) => id == 1
            ? new JsonObject
            {
                ["id"] = 1, ["display"] = "SW-CORE-01", ["name"] = "SW-CORE-01",
                ["url"] = "https://netbox.ex.fr/api/dcim/devices/1/", ["display_url"] = "https://netbox.ex.fr/dcim/devices/1/",
                ["status"] = new JsonObject { ["value"] = "active", ["label"] = "Active" },
                ["role"] = new JsonObject { ["id"] = 3, ["display"] = "Cœur de réseau", ["name"] = "Cœur de réseau" },
                ["device_type"] = new JsonObject { ["id"] = 9, ["display"] = "C9300-48P", ["model"] = "C9300-48P", ["manufacturer"] = new JsonObject { ["display"] = "Cisco" } },
                ["site"] = new JsonObject { ["display"] = "Metz DC1" }, ["rack"] = new JsonObject { ["display"] = "R12" }, ["position"] = 42.0,
                ["serial"] = "FOC2233X0AB", ["primary_ip4"] = new JsonObject { ["display"] = "10.255.0.1/24", ["address"] = "10.255.0.1/24" },
                ["tenant"] = null, ["platform"] = null,
                ["tags"] = new JsonArray(new JsonObject { ["display"] = "prod" }),
                ["custom_fields"] = new JsonObject { ["contrat_maintenance"] = "SmartNet 2027", ["vide"] = null },
                ["description"] = "Commutateur cœur salle 1", ["comments"] = "",
                ["last_updated"] = Dev1Updated,
            }
            : new JsonObject
            {
                ["id"] = 2, ["display"] = "FW-01", ["url"] = "https://netbox.ex.fr/api/dcim/devices/2/",
                ["status"] = new JsonObject { ["value"] = "active", ["label"] = "Active" }, ["last_updated"] = "2026-10-01T08:00:00Z",
            };

        public (HttpStatusCode, string, string?) Handle(string method, string url, string body)
        {
            Methods.Add(method);
            var path = url[(url.IndexOf("/api/", StringComparison.Ordinal) + 5)..];
            if (path.StartsWith("status/")) return FakeHttp.Json("{\"netbox-version\":\"4.5.2\"}");
            if (path.StartsWith("dcim/devices/") && path.Contains("brief=1"))
            {
                if (path.Contains("limit=1&")) return FakeHttp.Json($"{{\"count\":{Devices.Count},\"next\":null,\"results\":[]}}");
                return FakeHttp.Json(new JsonObject { ["count"] = Devices.Count, ["next"] = null, ["results"] = new JsonArray(Devices.Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToArray()) });
            }
            if (path.StartsWith("dcim/devices/"))
            {
                // Pagination : 1 élément par page pour vérifier « next ».
                var all = Devices.OrderBy(i => i).Select(Device)
                    .Where(d => !path.Contains("last_updated__gte=") || string.CompareOrdinal(d["last_updated"]!.ToString(), Uri.UnescapeDataString(path.Split("last_updated__gte=")[1].Split('&')[0])) >= 0)
                    .ToList();
                if (!path.Contains("last_updated__gte")) FullReads++;
                int offset = path.Contains("offset=") ? int.Parse(path.Split("offset=")[1].Split('&')[0]) : 0;
                var page = all.Skip(offset).Take(1).ToList();
                string? next = offset + 1 < all.Count ? $"http://interne:8000/api/{path.Split('?')[0]}?{path.Split('?')[1].Split("&offset=")[0]}&offset={offset + 1}" : null;
                return FakeHttp.Json(new JsonObject { ["count"] = all.Count, ["next"] = next, ["results"] = new JsonArray(page.Select(p => (JsonNode)p).ToArray()) });
            }
            if (path.StartsWith("ipam/ip-addresses/") && path.Contains("brief=1"))
                return FakeHttp.Json("{\"count\":1,\"next\":null,\"results\":[{\"id\":7}]}");
            if (path.StartsWith("ipam/ip-addresses/"))
                return FakeHttp.Json("""
                    {"count":1,"next":null,"results":[{"id":7,"display":"10.255.0.1/24","address":"10.255.0.1/24","dns_name":"sw-core-01.ingecap.local",
                     "status":{"value":"active","label":"Active"},"assigned_object":{"name":"Vlan10","device":{"display":"SW-CORE-01"}},
                     "vrf":null,"last_updated":"2026-10-30T10:00:00Z"}]}
                    """);
            if (path.StartsWith("ipam/prefixes/")) return FakeHttp.Status(HttpStatusCode.Forbidden, "{\"detail\":\"You do not have permission to perform this action.\"}");
            return FakeHttp.Json("{\"count\":0,\"next\":null,\"results\":[]}");
        }
    }

    private static (NetBoxConnector Con, NServer Srv, FakeHttp Http, SourceConfig Cfg) Make(string token = "nbt_abc.def")
    {
        var srv = new NServer();
        FakeHttp? http = null;
        http = new FakeHttp((m, url, body) =>
        {
            var auth = http!.LastAuthorization;
            var expected = srv.Token.StartsWith("nbt_") ? "Bearer " + srv.Token : "Token " + srv.Token;
            return auth == expected ? srv.Handle(m, url, body) : FakeHttp.Status(HttpStatusCode.Forbidden, "{\"detail\":\"Invalid v2 token\"}");
        });
        var cfg = new SourceConfig { Type = SourceType.NetBox, Name = "NetBox", ServerUrl = "https://netbox.ex.fr/api/" };
        var secrets = new MemorySecretStore();
        var client = new NetBoxClient(cfg.ServerUrl, secrets, "netbox:" + cfg.Id, http) { PageSize = 1 };
        secrets.Set(client.TokenKey, token);
        return (new NetBoxConnector(cfg, client), srv, http, cfg);
    }

    [Test]
    public static async Task Equipements_IP_pagination_incremental_et_suppression()
    {
        using var d = new TempDir();
        using var s = IndexTests.NewStore(d);
        var (con, srv, http, cfg) = Make();
        con.ApplyDefaultSelection();
        Assert.Equal("https://netbox.ex.fr", con.Client.BaseUrl);
        Assert.Contains("NetBox 4.5.2, 2 équipement(s)", await con.TestAsync(default));

        var ctx = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx, default);
        Assert.Equal(1, ctx.Errors, "préfixes refusés : un avertissement, le reste continue");

        var sw = s.Get("netbox:dcim/devices:1")!;
        Assert.Equal(ItemKind.NetBox, sw.Kind);
        Assert.Equal("SW-CORE-01", sw.Title);
        Assert.Contains("Statut : Active", sw.Body);
        Assert.Contains("Modèle : Cisco C9300-48P", sw.Body);
        Assert.Contains("IP principale : 10.255.0.1/24", sw.Body);
        Assert.Contains("contrat_maintenance : SmartNet 2027", sw.Body);
        Assert.Contains("Étiquettes : prod", sw.Body);
        Assert.NotContains("vide", sw.Body);
        Assert.NotContains("Locataire", sw.Body);
        Assert.Equal("https://netbox.ex.fr/dcim/devices/1/", sw.OpenUri);
        Assert.Contains("NetBox › Équipements · Metz DC1", sw.Location);
        Assert.Equal("https://netbox.ex.fr/dcim/devices/2/", s.Get("netbox:dcim/devices:2")!.OpenUri, "lien déduit de l'URL d'API");
        Assert.True(http.Calls.Where(c => c.Url.Contains("offset=1")).All(c => c.Url.StartsWith("https://netbox.ex.fr/api/")), "« next » interne réécrit sur notre adresse");

        var ip = s.Get("netbox:ipam/ip-addresses:7")!;
        Assert.Contains("Affectée à : SW-CORE-01 › Vlan10", ip.Body);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "sw-core-01.ingecap.local" }, null).Hits.Count(h => h.Item.Id == ip.Id));
        Assert.True(srv.Methods.All(m => m == "GET"), "lecture seule : uniquement des GET");

        // 2e passage : seuls les objets modifiés depuis le repère sont relus ; un équipement supprimé disparaît.
        int full = srv.FullReads;
        srv.Devices.Remove(2);
        var ctx2 = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx2, default);
        Assert.Equal(full, srv.FullReads, "lecture incrémentale (last_updated__gte)");
        Assert.Null(s.Get("netbox:dcim/devices:2"));
        Assert.Equal(1, ctx2.Deleted);
    }

    [Test]
    public static async Task Jeton_v1_et_jeton_refuse()
    {
        var (con, srv, _, _) = Make("0123456789abcdef0123456789abcdef01234567");
        srv.Token = "0123456789abcdef0123456789abcdef01234567";
        Assert.Contains("NetBox 4.5.2", await con.TestAsync(default));

        var (bad, _, _, _) = Make("nbt_mauvais.jeton");
        try { await bad.TestAsync(default); throw new Exception("exception attendue"); }
        catch (NetBoxException e) { Assert.Contains("Invalid v2 token", e.Message); }

        Assert.Equal("Bearer nbt_x.y", NetBoxClient.AuthHeader("nbt_x.y"));
        Assert.Equal("Token abc", NetBoxClient.AuthHeader(" abc "));
        Assert.Equal("https://nb.ex.fr/netbox", NetBoxClient.NormalizeBase("https://nb.ex.fr/netbox/api/dcim/"));

        // Certificat : Windows d'abord, sinon seule l'empreinte épinglée exacte passe.
        var chain = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
        Assert.True(NetBoxClient.Accept(System.Net.Security.SslPolicyErrors.None, "AA", null));
        Assert.False(NetBoxClient.Accept(chain, "AABB", null));
        Assert.True(NetBoxClient.Accept(chain, "AABB", "aa:bb"));
        Assert.False(NetBoxClient.Accept(chain, "AABC", "aa:bb"), "autre certificat refusé");
        Assert.Contains("autorité non reconnue", NetBoxClient.Explain(chain, "UntrustedRoot"));
        var info = new NetBoxClient.CertificateInfo("FEEF", "CN=x", "CN=x", new DateTime(2024, 5, 18), chain, "", "x.ex.fr");
        Assert.Equal("FEEF", new NetBoxException(495, "refus", "/api/", info).Certificate!.Sha256, "le certificat voyage avec l'erreur");
    }
}
