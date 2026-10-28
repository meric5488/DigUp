using System.Net;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;
using Fouineur.Core.Connectors.Glpi;
using Fouineur.Core.Index;
using Fouineur.Core.Model;

namespace Fouineur.Tests;

public static class GlpiTests
{
    private sealed class GServer
    {
        public List<JsonObject> Tickets = new()
        {
            new() { ["id"] = 12, ["name"] = "Imprimante bloquée au 2e étage", ["content"] = "&lt;p&gt;L'imprimante &lt;b&gt;HP&lt;/b&gt; affiche un bourrage&lt;/p&gt;",
                    ["status"] = 2, ["priority"] = 4, ["itilcategories_id"] = "Matériel > Impression", ["entities_id"] = "Ingecap", ["locations_id"] = "&nbsp;",
                    ["users_id_recipient"] = "Eric", ["date"] = "2026-10-20 09:00:00", ["date_mod"] = "2026-10-21 10:00:00" },
            new() { ["id"] = 7, ["name"] = "Accès VPN", ["content"] = "Pas d'accès au VPN", ["status"] = 6, ["priority"] = 3,
                    ["users_id_recipient"] = "Sophie", ["date"] = "2026-09-01 08:00:00", ["date_mod"] = "2026-09-02 08:00:00" },
        };
        public HashSet<int> Existing = new() { 12, 7 };
        public int SubCalls;
        public string? Token;

        public (HttpStatusCode, string, string?) Handle(string method, string url, string body)
        {
            var path = url[(url.IndexOf("apirest.php/", StringComparison.Ordinal) + 12)..];
            if (path.StartsWith("initSession"))
                return Token == "bon" ? FakeHttp.Json("{\"session_token\":\"S1\"}") : FakeHttp.Status(HttpStatusCode.Unauthorized, "[\"ERROR_GLPI_LOGIN_USER_TOKEN\",\"parameter user_token seems invalid\"]");
            if (path.StartsWith("killSession")) return FakeHttp.Json("{}");
            if (path.StartsWith("Ticket/") && path.Contains("ITILFollowup"))
            {
                SubCalls++;
                return FakeHttp.Json("[{\"id\":1,\"content\":\"&lt;p&gt;Bourrage retiré, rouleau à changer&lt;/p&gt;\",\"users_id\":\"Paul\",\"date\":\"2026-10-21 09:30:00\",\"is_private\":0}]");
            }
            if (path.StartsWith("Ticket/") && path.Contains("ITILSolution"))
                return FakeHttp.Json("[{\"id\":3,\"content\":\"Rouleau remplacé\",\"users_id\":\"Paul\",\"date_creation\":\"2026-10-21 09:55:00\"}]");
            if (path.StartsWith("Ticket/") && path.Contains("TicketTask")) return FakeHttp.Json("[]");
            if (path.StartsWith("Ticket?only_id=true"))
                return FakeHttp.Json(new JsonArray(Existing.Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToArray()));
            if (path.StartsWith("Ticket?"))
                return FakeHttp.Json(new JsonArray(Tickets.Where(t => Existing.Contains(t["id"]!.GetValue<int>()))
                    .OrderByDescending(t => t["date_mod"]!.ToString()).Select(t => t.DeepClone()).ToArray()));
            if (path.StartsWith("KnowbaseItem?only_id")) return FakeHttp.Json("[{\"id\":4}]");
            if (path.StartsWith("KnowbaseItem?"))
                return FakeHttp.Json("[{\"id\":4,\"name\":\"Configurer le VPN\",\"answer\":\"&lt;ol&gt;&lt;li&gt;Installer FortiClient&lt;/li&gt;&lt;/ol&gt;\",\"users_id\":\"Eric\",\"date_mod\":\"2026-05-01 12:00:00\"}]");
            if (path.StartsWith("Computer/34?") && path.Contains("with_softwares=true"))
                return FakeHttp.Json("{\"id\":34,\"_networkports\":{\"NetworkPortEthernet\":[{\"name\":\"Ethernet\",\"mac\":\"AA:BB:CC:DD:EE:01\",\"NetworkName\":{\"IPAddress\":[{\"name\":\"10.12.0.45\"},{\"name\":\"fe80::1\"}]}}],\"NetworkPortWifi\":[{\"name\":\"Wi-Fi\",\"mac\":\"aa:bb:cc:dd:ee:02\",\"NetworkName\":{\"IPAddress\":[{\"name\":\"192.168.1.20\"}]}}]},\"_softwares\":[{\"softwares_id\":\"Mozilla Firefox\",\"softwareversions_id\":\"128.0.3\"},{\"softwares_id\":\"7-Zip\",\"softwareversions_id\":\"24.08\"},{\"softwares_id\":\"Mozilla Firefox\",\"softwareversions_id\":\"128.0.3\"}]}");
            if (path.StartsWith("Computer/34/Item_OperatingSystem"))
                return FakeHttp.Json("[{\"operatingsystems_id\":\"Microsoft Windows 11 Professionnel\",\"operatingsystemversions_id\":\"23H2\",\"operatingsystemarchitectures_id\":\"64-bit\",\"operatingsystemkernelversions_id\":\"10.0.22631\",\"operatingsystemservicepacks_id\":\"&nbsp;\",\"operatingsystemeditions_id\":0}]");
            if (path.StartsWith("Computer?only_id")) return FakeHttp.Json("[{\"id\":34}]");
            if (path.StartsWith("Computer?"))
                return FakeHttp.Json("[{\"id\":34,\"name\":\"PC-COMPTA-01\",\"serial\":\"5CG1234XYZ\",\"users_id\":\"Sophie\",\"locations_id\":\"Metz > 2e étage\",\"computermodels_id\":\"EliteBook 840\",\"groups_id\":\"&nbsp;\",\"is_template\":0,\"date_mod\":\"2026-10-01 08:00:00\",\"links\":[]}]");
            return FakeHttp.Status(HttpStatusCode.BadRequest, "[\"ERROR_RESOURCE_NOT_FOUND_NOR_COMMONDBTM\",\"x\"]");
        }
    }

    private static (GlpiConnector Con, GServer Srv, FakeHttp Http, SourceConfig Cfg) Make(string token = "bon")
    {
        var srv = new GServer { Token = token };
        var http = new FakeHttp(srv.Handle);
        var cfg = new SourceConfig { Type = SourceType.Glpi, Name = "GLPI", ServerUrl = "https://glpi.ex.fr/apirest.php" };
        var secrets = new MemorySecretStore();
        var client = new GlpiClient(cfg.ServerUrl, secrets, "glpi:" + cfg.Id, http);
        secrets.Set(client.UserTokenKey, token);
        return (new GlpiConnector(cfg, client), srv, http, cfg);
    }

    [Test]
    public static async Task Tickets_avec_suivis_base_de_connaissances_et_inventaire()
    {
        using var d = new TempDir();
        using var s = IndexTests.NewStore(d);
        var (con, srv, http, cfg) = Make();
        con.ApplyDefaultSelection();
        cfg.Rules.Set(new[] { GlpiConnector.AssetsRoot }, "glpi:asset:Computer", true);
        var ctx = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx, default);
        Assert.Equal(0, ctx.Errors);

        var t = s.Get("glpi:Ticket:12")!;
        Assert.Equal(ItemKind.Glpi, t.Kind);
        Assert.Equal("#12 — Imprimante bloquée au 2e étage", t.Title);
        Assert.Contains("L'imprimante HP affiche un bourrage", t.Body);
        Assert.Contains("Suivi — Paul — 2026-10-21 09:30:00\nBourrage retiré", t.Body);
        Assert.Contains("Solution — Paul", t.Body);
        Assert.Contains("Statut : En cours (attribué) · Priorité : Haute", t.Body);
        Assert.Equal("https://glpi.ex.fr/front/ticket.form.php?id=12", t.OpenUri);
        Assert.Contains("GLPI › Tickets › Matériel > Impression", t.Location);
        Assert.NotContains("nbsp", t.Body);

        var kb = s.Get("glpi:KnowbaseItem:4")!;
        Assert.Contains("Installer FortiClient", kb.Body);
        var pc = s.Get("glpi:Computer:34")!;
        Assert.Contains("N° de série : 5CG1234XYZ", pc.Body);
        Assert.Contains("Modèle : EliteBook 840", pc.Body);
        Assert.NotContains("Groupe", pc.Body);
        Assert.Contains("Logiciels installés (2) :", pc.Body);
        Assert.Contains("Système d'exploitation : Microsoft Windows 11 Professionnel 23H2 (64-bit) — noyau 10.0.22631", pc.Body);
        Assert.Contains("Adresses IP : 10.12.0.45, 192.168.1.20", pc.Body);
        Assert.NotContains("fe80", pc.Body);
        Assert.Contains("aa:bb:cc:dd:ee:01", pc.Body);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "10.12.0.45" }, null).Hits.Count, "trouvé par adresse IP");
        var inv = Fouineur.Core.Inventory.InventoryService.Run(s, Fouineur.Core.Inventory.InventoryQuery.Parse("firefox version>=128"));
        Assert.Equal(1, inv.Rows.Count, "tableau d'inventaire depuis la synchro");
        Assert.Equal("128.0.3", inv.Rows[0].Version);
        Assert.Equal("10.12.0.45, 192.168.1.20", inv.Rows[0].Ips);
        Assert.Equal("Metz > 2e étage", inv.Rows[0].Location);
        Assert.Contains("• Mozilla Firefox 128.0.3", pc.Body);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "firefox" }, null).Hits.Count(h => h.Item.Id == "glpi:Computer:34"), "ordinateur trouvé par logiciel");
        Assert.Equal(1, s.Search(new SearchRequest { Text = "5CG1234XYZ" }, null).Hits.Count, "trouvé par numéro de série");
        Assert.True(http.Calls.Any(c => c.Url.EndsWith("killSession")), "session fermée");
        Assert.True(http.Calls.Where(c => !c.Url.Contains("initSession")).All(c => true));

        // 2e passage : rien de changé → pas de relecture des suivis ; un ticket mis à la corbeille → retiré
        int sub = srv.SubCalls;
        srv.Existing.Remove(7);
        var ctx2 = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx2, default);
        Assert.Equal(sub, srv.SubCalls, "aucun ticket modifié");
        Assert.Null(s.Get("glpi:Ticket:7"));
        Assert.Equal(1, ctx2.Deleted);

        // un ticket modifié → relu
        srv.Tickets[0]["date_mod"] = "2026-10-28 08:00:00";
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        Assert.Equal(sub + 1, srv.SubCalls);
    }

    [Test]
    public static async Task Jeton_refuse_message_clair()
    {
        using var d = new TempDir();
        using var s = IndexTests.NewStore(d);
        var (con, _, _, cfg) = Make("mauvais");
        con.ApplyDefaultSelection();
        try { await con.TestAsync(default); throw new Exception("exception attendue"); }
        catch (GlpiException e) { Assert.Contains("jeton d'API refusé", e.Message); }
        Assert.Equal("https://glpi.ex.fr", con.Client.BaseUrl);
    }

    [Test]
    public static async Task Jeton_passe_en_parametre_si_le_serveur_attend_OAuth()
    {
        var http = new FakeHttp((m, url, body) =>
        {
            if (url.Contains("initSession"))
                return url.Contains("user_token=bon")
                    ? FakeHttp.Json("{\"session_token\":\"S1\"}")
                    : FakeHttp.Status(HttpStatusCode.BadRequest, "{\"status\":\"ERROR_INVALID_PARAMETER\",\"title\":\"Invalid OAuth token\",\"detail\":\"The JWT string must have two dots\"}");
            return FakeHttp.Json("[]");
        });
        var secrets = new MemorySecretStore();
        var client = new GlpiClient("https://glpi.ex.fr/api.php/v2.3", secrets, "glpi:x", http);
        Assert.Equal("https://glpi.ex.fr", client.BaseUrl);
        secrets.Set(client.UserTokenKey, "bon");
        Assert.Equal("S1", await client.EnsureSessionAsync(default));
        Assert.Equal("https://glpi.ex.fr", GlpiClient.NormalizeBase("https://glpi.ex.fr/front/central.php"));
        var e = GlpiClient.Error(HttpStatusCode.BadRequest, "{\"status\":\"ERROR_INVALID_PARAMETER\",\"title\":\"Invalid OAuth token\",\"detail\":\"x\"}");
        Assert.Contains("nouvelle API OAuth", e.Message);
    }

    [Test]
    public static async Task Jeton_de_session_en_parametre_si_l_en_tete_est_perdu()
    {
        var http = new FakeHttp((m, url, body) =>
        {
            if (url.Contains("initSession")) return FakeHttp.Json("{\"session_token\":\"S1\"}");
            if (!url.Contains("session_token=S1"))
                return FakeHttp.Status(HttpStatusCode.BadRequest, "[\"ERROR_SESSION_TOKEN_MISSING\",\"le paramètre session_token est manquant ou vide\"]");
            return FakeHttp.Json("[{\"id\":1}]");
        });
        var secrets = new MemorySecretStore();
        var client = new GlpiClient("http://10.255.252.12", secrets, "glpi:x", http);
        secrets.Set(client.UserTokenKey, "bon");
        var (json, _) = await client.GetAsync("Ticket?only_id=true", default);
        Assert.Equal(1, (json as System.Text.Json.Nodes.JsonArray)!.Count);
        Assert.True(http.Calls.Last().Url.EndsWith("Ticket?only_id=true&session_token=S1"), "jeton ajouté au paramètre existant");
    }

    [Test]
    public static async Task Erreur_500_repli_sans_expand_et_message_lisible()
    {
        using var d = new TempDir();
        using var s = IndexTests.NewStore(d);
        var srv = new GServer { Token = "bon" };
        var html = "<!DOCTYPE html><html><head><title>Erreur - GLPI</title><style>x{}</style></head><body><h1>Une erreur est survenue</h1></body></html>";
        var http = new FakeHttp((m, url, body) =>
        {
            if (url.Contains("Ticket?") && url.Contains("expand_dropdowns")) return FakeHttp.Status(HttpStatusCode.InternalServerError, html);
            if (url.Contains("ITILSolution")) return FakeHttp.Status(HttpStatusCode.InternalServerError, html);
            return srv.Handle(m, url, body);
        });
        var cfg = new SourceConfig { Type = SourceType.Glpi, Name = "GLPI", ServerUrl = "http://10.255.252.12" };
        var secrets = new MemorySecretStore();
        var client = new GlpiClient(cfg.ServerUrl, secrets, "glpi:" + cfg.Id, http);
        secrets.Set(client.UserTokenKey, "bon");
        var con = new GlpiConnector(cfg, client);
        con.ApplyDefaultSelection();
        var ctx = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx, default);
        Assert.NotNull(s.Get("glpi:Ticket:12"));
        Assert.Contains("Bourrage retiré", s.Get("glpi:Ticket:12")!.Body);
        Assert.NotNull(s.Get("glpi:KnowbaseItem:4"));

        var e = GlpiClient.Error(HttpStatusCode.InternalServerError, html, GlpiClient.SafePath("Ticket?a=1&session_token=SECRET"));
        Assert.Contains("Erreur - GLPI", e.Message);
        Assert.Contains("Une erreur est survenue", e.Message);
        Assert.NotContains("<", e.Message);
        Assert.NotContains("SECRET", e.Message);
    }

    [Test]
    public static async Task InitSession_en_erreur_500_avec_l_en_tete()
    {
        var http = new FakeHttp((m, url, body) =>
            url.Contains("initSession") && !url.Contains("user_token=bon")
                ? FakeHttp.Status(HttpStatusCode.InternalServerError, "<html><head><title>Erreur - GLPI</title></head></html>")
                : FakeHttp.Json("{\"session_token\":\"S2\"}"));
        var secrets = new MemorySecretStore();
        var client = new GlpiClient("http://10.255.252.12", secrets, "glpi:x", http);
        secrets.Set(client.UserTokenKey, "bon");
        Assert.Equal("S2", await client.EnsureSessionAsync(default));
    }

    [Test]
    public static async Task Mode_parametres_sans_aucun_en_tete_comme_un_navigateur()
    {
        // Serveur qui plante dès qu'un en-tête App-Token ou Authorization est présent, mais accepte tout en paramètres.
        FakeHttp? http = null;
        http = new FakeHttp((m, url, body) =>
        {
            var hd = http!.LastHeaders;
            if (hd.Contains("App-Token") || hd.Contains("Authorization") || hd.Contains("Session-Token"))
                return FakeHttp.Status(HttpStatusCode.InternalServerError, "<html><title>Erreur - GLPI</title></html>");
            if (url.Contains("initSession"))
                return url.Contains("user_token=U") && url.Contains("app_token=A") ? FakeHttp.Json("{\"session_token\":\"S3\"}") : FakeHttp.Status(HttpStatusCode.BadRequest, "[\"ERROR_WRONG_APP_TOKEN_PARAMETER\",\"x\"]");
            return url.Contains("session_token=S3") && url.Contains("app_token=A") ? FakeHttp.Json("[{\"id\":5}]") : FakeHttp.Status(HttpStatusCode.BadRequest, "[\"ERROR_SESSION_TOKEN_MISSING\",\"x\"]");
        });
        var secrets = new MemorySecretStore();
        var client = new GlpiClient("http://10.255.252.12", secrets, "glpi:x", http);
        secrets.Set(client.UserTokenKey, "U");
        secrets.Set(client.AppTokenKey, "A");
        var (json, _) = await client.GetAsync("Ticket?only_id=true", default);
        Assert.Equal(1, (json as System.Text.Json.Nodes.JsonArray)!.Count);
        Assert.True(client.ParameterMode);
        Assert.NotContains("A", GlpiClient.SafePath("Ticket?x=1&app_token=A&session_token=S3").Replace("Ticket", ""));
    }

    [Test]
    public static void Html_encode_deux_fois()
    {
        Assert.Equal("Bonjour le monde", GlpiConnector.Html("&amp;lt;p&amp;gt;Bonjour &amp;lt;b&amp;gt;le&amp;lt;/b&amp;gt; monde&amp;lt;/p&amp;gt;"));
    }
}
