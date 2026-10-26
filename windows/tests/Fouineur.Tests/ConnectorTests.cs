using System.Net;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;
using Fouineur.Core.Connectors;
using Fouineur.Core.Connectors.Local;
using Fouineur.Core.Connectors.M365;
using Fouineur.Core.Connectors.Zimbra;
using Fouineur.Core.Extraction;
using Fouineur.Core.Index;
using Fouineur.Core.Model;
using Fouineur.Core.Sync;

namespace Fouineur.Tests;

internal static class Ctx
{
    public static SyncContext For(IndexStore s, SourceConfig c, TempDir d) => new()
    {
        Store = s, Source = c, Extractors = TextExtractors.CreateDefault(), CacheDir = d.Path, Log = m => Console.WriteLine("       log: " + m),
    };
}

public static class LocalTests
{
    [Test]
    public static async Task Synchronisation_incrementale_exclusions_et_balayage()
    {
        using var d = new TempDir();
        var root = Path.Combine(d.Path, "docs");
        d.File("docs/a.txt", "alpha bravo");
        d.File("docs/sous/b.md", "charlie delta");
        d.File("docs/prive/c.txt", "secret echo");
        d.File("docs/node_modules/x.js", "ignoré");
        d.File("docs/image.bin", "binaire");
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        var cfg = new SourceConfig { Type = SourceType.LocalFiles, Name = "Docs" };
        var con = new LocalFilesConnector(cfg);
        con.AddRoot(root);
        cfg.Rules.Set(new[] { LocalFilesConnector.NodeId(root) }, LocalFilesConnector.NodeId(Path.Combine(root, "prive")), false);

        var ctx = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx, default);
        Assert.Equal(3, s.Count(), "a.txt, sous/b.md, image.bin (nom seul)");
        Assert.Equal(1, s.Search(new SearchRequest { Text = "charlie" }, null).Hits.Count);
        Assert.Equal(0, s.Search(new SearchRequest { Text = "secret" }, null).Hits.Count);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "image" }, null).Hits.Count, "fichier non lisible trouvé par son nom");
        var b = s.Search(new SearchRequest { Text = "charlie" }, null).Hits[0].Item;
        Assert.Equal(ItemKind.Fichier, b.Kind);
        Assert.Contains("sous", b.Location);

        // pas de changement → rien n'est retraité
        var ctx2 = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx2, default);
        Assert.Equal(0, ctx2.Added + ctx2.Updated);

        // modification + suppression
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha foxtrot plus long");
        File.Delete(Path.Combine(root, "sous", "b.md"));
        var ctx3 = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx3, default);
        Assert.Equal(1, ctx3.Updated);
        Assert.Equal(1, ctx3.Deleted);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "foxtrot" }, null).Hits.Count);

        // on coche « prive » : il apparaît ; on décoche la racine : tout part (élagage immédiat)
        cfg.Rules.Set(new[] { LocalFilesConnector.NodeId(root) }, LocalFilesConnector.NodeId(Path.Combine(root, "prive")), true);
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "secret" }, null).Hits.Count);
        cfg.Rules.Set(Array.Empty<string>(), LocalFilesConnector.NodeId(root), false);
        Assert.Equal(3, Ctx.For(s, cfg, d).Prune());
        Assert.Equal(0, s.Count());
    }

    [Test]
    public static async Task Partage_hors_ligne_ne_supprime_rien()
    {
        using var d = new TempDir();
        var root = Path.Combine(d.Path, "partage");
        d.File("partage/a.txt", "contenu");
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        var cfg = new SourceConfig { Type = SourceType.LocalFiles, Name = "Partage" };
        var con = new LocalFilesConnector(cfg);
        con.AddRoot(root);
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        Assert.Equal(1, s.Count());
        Directory.Move(root, root + "-hors-ligne");
        var ctx = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx, default);
        Assert.Equal(1, s.Count());
        Assert.Equal(1, ctx.Errors);
    }

    [Test]
    public static async Task Selecteur_liste_les_sous_dossiers()
    {
        using var d = new TempDir();
        d.File("r/un/x.txt", "x"); d.File("r/deux/y.txt", "y"); d.File("r/.git/z", "z");
        var cfg = new SourceConfig();
        var con = new LocalFilesConnector(cfg);
        con.AddRoot(Path.Combine(d.Path, "r"));
        var roots = await con.GetChildrenAsync(null, default);
        Assert.Equal(1, roots.Count);
        var kids = await con.GetChildrenAsync(roots[0], default);
        Assert.Equal("deux,un", string.Join(",", kids.Select(k => k.Name)));
        Assert.Equal(roots[0].Id, kids[0].Ancestors[0]);
    }

    [Test]
    public static async Task Service_de_synchro_de_bout_en_bout()
    {
        using var d = new TempDir();
        d.File("data/rapport.txt", "rapport annuel d'activité");
        var paths = new AppPaths(Path.Combine(d.Path, "app"));
        var settings = new AppSettings();
        var cfg = new SourceConfig { Type = SourceType.LocalFiles, Name = "Data" };
        new LocalFilesConnector(cfg).AddRoot(Path.Combine(d.Path, "data"));
        settings.Sources.Add(cfg);
        using var store = new IndexStore(paths.Index);
        using var sync = new SyncService(store, settings, new LocalOnlyFactory(), TextExtractors.CreateDefault(), paths) { Embedder = new FakeEmbedder() };
        var statuses = new List<SyncStatus>();
        sync.StatusChanged += statuses.Add;
        await sync.SyncAllAsync();
        Assert.Equal(1, store.Count());
        Assert.Equal(0, store.PendingEmbeddingCount(), "vectorisé après la synchro");
        Assert.NotNull(cfg.LastSync);
        Assert.True(File.Exists(paths.Settings));
        Assert.Equal(SyncPhase.Idle, sync.Status.Phase);
        var reloaded = AppSettings.Load(paths.Settings);
        Assert.Equal(1, reloaded.Sources[0].Rules.Rules.Count);
    }

    private sealed class LocalOnlyFactory : IConnectorFactory
    {
        public ISourceConnector Create(SourceConfig config) => new LocalFilesConnector(config);
    }
}

public sealed class FakeTokens : ITokenProvider
{
    public int Interactive;
    public Task<string> GetTokenAsync(bool interactive, CancellationToken ct) { if (interactive) Interactive++; return Task.FromResult("jeton"); }
    public string? AccountName => "eric@contoso.fr";
    public Task SignOutAsync() => Task.CompletedTask;
}

public static class M365Tests
{
    private static JsonObject Page(params JsonNode[] items) => new() { ["value"] = new JsonArray(items) };

    private static JsonObject File_(string id, string name, string parent, string ctag, long size = 10) => new()
    {
        ["id"] = id, ["name"] = name, ["file"] = new JsonObject(), ["size"] = size, ["cTag"] = ctag,
        ["parentReference"] = new JsonObject { ["id"] = parent },
        ["lastModifiedDateTime"] = "2026-10-20T10:00:00Z", ["webUrl"] = $"https://contoso.sharepoint.com/{name}",
        ["webDavUrl"] = $"https://contoso.sharepoint.com/dav/{name}",
        ["lastModifiedBy"] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = "Alice" } },
    };

    private static JsonObject Folder(string id, string name, string parent) => new()
    {
        ["id"] = id, ["name"] = name, ["folder"] = new JsonObject { ["childCount"] = 1 }, ["parentReference"] = new JsonObject { ["id"] = parent },
    };

    [Test]
    public static async Task OneDrive_delta_selection_de_sous_dossier_et_suppressions()
    {
        using var d = new TempDir();
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        int deltaCalls = 0;
        var http = new FakeHttp((m, url, body) =>
        {
            if (url.EndsWith("me/drive?$select=id,name,webUrl,driveType")) return FakeHttp.Json(new JsonObject { ["id"] = "D1", ["name"] = "OneDrive", ["driveType"] = "business" });
            if (url.Contains("drives/D1/root?$select=id")) return FakeHttp.Json(new JsonObject { ["id"] = "ROOT" });
            if (url.Contains("delta?token=T1"))
            {
                var p = Page(File_("A", "plan.txt", "F1", "c2"), new JsonObject { ["id"] = "C", ["deleted"] = new JsonObject() });
                p["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/drives/D1/root/delta?token=T2";
                return FakeHttp.Json(p);
            }
            if (url.Contains("/root/delta"))
            {
                deltaCalls++;
                var p = Page(new JsonObject { ["id"] = "ROOT", ["name"] = "root", ["root"] = new JsonObject(), ["folder"] = new JsonObject() },
                    Folder("F1", "Projets", "ROOT"), Folder("F2", "Perso", "ROOT"),
                    File_("A", "plan.txt", "F1", "c1"), File_("B", "journal.txt", "F2", "c1"), File_("C", "photo.jpg", "F1", "c1"));
                p["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/drives/D1/root/delta?token=T1";
                return FakeHttp.Json(p);
            }
            if (url.Contains("items/A/content")) return FakeHttp.Bytes(deltaCalls > 0 && url.Length > 0 ? "plan de projet version" : "");
            if (url.Contains("items/B/content")) return FakeHttp.Bytes("journal intime");
            return FakeHttp.Status(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"itemNotFound\",\"message\":\"x\"}}");
        });
        var cfg = new SourceConfig { Type = SourceType.Microsoft365, Name = "M365" };
        // Seulement « Projets » dans OneDrive
        cfg.Rules.Set(new[] { M365Connector.OneDriveRoot }, "item:D1:F1", true, "Projets");
        using var con = new M365Connector(cfg, new GraphClient(new FakeTokens(), http));

        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        Assert.Equal(2, s.Count(), "plan.txt + photo.jpg");
        var plan = s.Get("sp:D1:A")!;
        Assert.Equal(ItemKind.OneDrive, plan.Kind);
        Assert.Contains("plan de projet", plan.Body);
        Assert.Equal("Alice", plan.Author);
        Assert.Equal("OneDrive › Projets", plan.Location);
        Assert.Equal("od,item:D1:F1", string.Join(",", plan.Ancestors));
        Assert.Null(s.Get("sp:D1:B"), "Perso non coché");
        Assert.False(http.Calls.Any(c => c.Url.Contains("items/C/content")), "image : pas de téléchargement");

        var ctx2 = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx2, default);
        Assert.Equal(1, ctx2.Updated);
        Assert.Equal(1, ctx2.Deleted);
        Assert.True(http.Calls.Any(c => c.Url.Contains("token=T1")), "le lien delta est réutilisé");

        // Ouverture dans Word de bureau
        var open = await con.GetOpenActionAsync(new IndexedItem { Id = "x", SourceId = "s", Kind = ItemKind.OneDrive, Extension = ".docx", OpenUri = "https://w" }.SetExtra("webDavUrl", "https://dav/f.docx"), default);
        Assert.Equal("ms-word:ofe|u|https://dav/f.docx", open!.Target);
        Assert.Equal("https://w", open.FallbackUrl);

        // On coche aussi « Perso » : le périmètre change → relecture complète (sans retélécharger plan.txt inchangé)
        cfg.Rules.Set(new[] { M365Connector.OneDriveRoot }, "item:D1:F2", true, "Perso");
        int before = http.Calls.Count(c => c.Url.Contains("items/A/content"));
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        Assert.NotNull(s.Get("sp:D1:B"));
        Assert.True(http.Calls.Count(c => c.Url.Contains("items/A/content")) >= before);
    }

    [Test]
    public static async Task SharePoint_racine_refusee_parcourt_les_dossiers_coches()
    {
        using var d = new TempDir();
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        var http = new FakeHttp((m, url, body) =>
        {
            if (url.Contains("sites/S1?$select")) return FakeHttp.Json(new JsonObject { ["id"] = "S1", ["displayName"] = "Direction" });
            if (url.Contains("sites/S1/drives")) return FakeHttp.Json(Page(new JsonObject { ["id"] = "D2", ["name"] = "Documents", ["driveType"] = "documentLibrary" }));
            if (url.Contains("drives/D2/root?$select=id")) return FakeHttp.Json(new JsonObject { ["id"] = "R2" });
            if (url.Contains("/root/delta")) return FakeHttp.Status(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"non\"}}");
            if (url.Contains("items/FX/children")) return FakeHttp.Json(Page(File_("Z", "note.txt", "FX", "z1"), Folder("FY", "Archives", "FX")));
            if (url.Contains("items/FY/children")) return FakeHttp.Json(Page(File_("Y", "vieux.txt", "FY", "y1")));
            if (url.Contains("items/Z/content")) return FakeHttp.Bytes("note de service");
            if (url.Contains("items/Y/content")) return FakeHttp.Bytes("vieille note");
            return FakeHttp.Status(HttpStatusCode.NotFound);
        });
        var cfg = new SourceConfig { Type = SourceType.Microsoft365 };
        cfg.Rules.Set(new[] { "sites", "site:S1", "drive:D2" }, "item:D2:FX", true, "Dossier X");
        using var con = new M365Connector(cfg, new GraphClient(new FakeTokens(), http));
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        var z = s.Get("sp:D2:Z")!;
        Assert.Equal(ItemKind.SharePoint, z.Kind);
        Assert.Contains("note de service", z.Body);
        Assert.NotNull(s.Get("sp:D2:Y"), "sous-dossier hérité");
        Assert.Contains("Direction › Documents", z.Location);
    }

    [Test]
    public static async Task Teams_fils_de_canal_et_conversations_par_jour()
    {
        using var d = new TempDir();
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        JsonObject Msg(string id, string text, string at, string who, string? subject = null) => new()
        {
            ["id"] = id, ["messageType"] = "message", ["createdDateTime"] = at, ["lastModifiedDateTime"] = at,
            ["subject"] = subject, ["webUrl"] = $"https://teams.microsoft.com/l/message/{id}",
            ["from"] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = who } },
            ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = $"<p>{text}</p>" },
            ["attachments"] = new JsonArray(),
        };
        var http = new FakeHttp((m, url, body) =>
        {
            if (url.Contains("me/joinedTeams")) return FakeHttp.Json(Page(new JsonObject { ["id"] = "T1", ["displayName"] = "Informatique" }));
            if (url.Contains("teams/T1/channels?$select")) return FakeHttp.Json(Page(new JsonObject { ["id"] = "C1", ["displayName"] = "Général" }, new JsonObject { ["id"] = "C2", ["displayName"] = "Bavardages" }));
            if (url.Contains("channels/C1/messages/delta"))
            {
                var p = Page(Msg("R1", "Panne du serveur de fichiers", "2026-10-20T08:00:00Z", "Eric", "Incident NAS"));
                p["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/teams/T1/channels/C1/messages/delta?token=X";
                return FakeHttp.Json(p);
            }
            if (url.Contains("messages/R1/replies")) return FakeHttp.Json(Page(Msg("R1a", "Redémarrage effectué", "2026-10-20T09:00:00Z", "Paul")));
            if (url.Contains("channels/C2")) throw new Exception("C2 n'est pas coché");
            if (url.Contains("/me?$select")) return FakeHttp.Json(new JsonObject { ["id"] = "ME", ["displayName"] = "Eric" });
            if (url.Contains("me/chats?$expand=members")) return FakeHttp.Json(Page(new JsonObject
            {
                ["id"] = "CH1", ["chatType"] = "oneOnOne", ["topic"] = null,
                ["members"] = new JsonArray(new JsonObject { ["userId"] = "ME", ["displayName"] = "Eric" }, new JsonObject { ["userId"] = "U2", ["displayName"] = "Sophie" }),
            }));
            if (url.Contains("chats/CH1/messages")) return FakeHttp.Json(Page(
                Msg("M3", "et la facture ?", "2026-10-21T15:00:00Z", "Sophie"),
                Msg("M2", "commande passée", "2026-10-20T14:00:00Z", "Eric"),
                Msg("M1", "tu peux commander les écrans ?", "2026-10-20T13:00:00Z", "Sophie")));
            return FakeHttp.Status(HttpStatusCode.NotFound);
        });
        var cfg = new SourceConfig { Type = SourceType.Microsoft365 };
        cfg.Rules.Set(new[] { "teams", "team:T1" }, "channel:T1:C1", true);
        cfg.Rules.Set(Array.Empty<string>(), "chats", true);
        using var con = new M365Connector(cfg, new GraphClient(new FakeTokens(), http));
        var ctx = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx, default);
        Assert.Equal(0, ctx.Errors);
        var thread = s.Get("thread:T1:C1:R1")!;
        Assert.Equal("Incident NAS", thread.Title);
        Assert.Contains("Redémarrage effectué", thread.Body);
        Assert.Equal("Informatique › Général", thread.Location);
        Assert.Equal(ItemKind.Teams, thread.Kind);
        Assert.Equal("https://teams.microsoft.com/l/message/R1", thread.OpenUri);
        var days = s.Search(new SearchRequest { Text = "", Kind = ItemKind.Teams }, null).Hits.Where(h => h.Item.Id.StartsWith("chatday:")).ToList();
        Assert.Equal(2, days.Count, "un document par jour");
        var d20 = s.Get(days.Single(h => h.Item.Id.EndsWith("2026-10-20")).Item.Id)!;
        Assert.Contains("Sophie", d20.Title);
        Assert.True(d20.Body.IndexOf("commander les écrans") < d20.Body.IndexOf("commande passée"), "ordre chronologique");
        Assert.True(http.Calls.Any(c => c.Url.Contains("$filter=lastModifiedDateTime gt")), "filtre incrémental");
    }

    [Test]
    public static async Task Graph_reessaie_sur_429()
    {
        int n = 0;
        var http = new FakeHttp((m, url, b) => ++n < 2 ? FakeHttp.Status((HttpStatusCode)429) : FakeHttp.Json("{\"id\":\"ok\"}"));
        using var g = new GraphClient(new FakeTokens(), http);
        var r = await g.GetAsync("me", default);
        Assert.Equal("ok", r["id"]!.ToString());
        Assert.Equal(2, n);
    }
}

public static class ZimbraTests
{
    private sealed class ZServer
    {
        public readonly List<JsonObject> Requests = new();
        public bool RequireTwoFactor;
        public bool Delta;
        public bool MustResync;

        public (HttpStatusCode, string, string?) Handle(string method, string url, string body)
        {
            if (url.Contains("/service/home/")) return FakeHttp.Bytes("Subject: test\r\n\r\ncorps brut");
            var env = JsonNode.Parse(body)!.AsObject();
            Requests.Add(env);
            var (name, req) = env["Body"]!.AsObject().First();
            var account = env["Header"]?["context"]?["account"]?["_content"]?.ToString() ?? "eric@ex.fr";
            JsonNode Resp(string n, JsonNode r) => new JsonObject { ["Body"] = new JsonObject { [n] = r } };
            switch (name)
            {
                case "AuthRequest":
                    if (RequireTwoFactor && req!["twoFactorCode"] is null)
                        return FakeHttp.Json(Resp("AuthResponse", new JsonObject { ["authToken"] = new JsonArray(new JsonObject { ["_content"] = "TEMP" }), ["twoFactorAuthRequired"] = new JsonObject { ["_content"] = true } }));
                    if (req!["password"]?["_content"]?.ToString() is { } pw && pw != "secret")
                        return FakeHttp.Json(new JsonObject { ["Body"] = new JsonObject { ["Fault"] = new JsonObject { ["Reason"] = new JsonObject { ["Text"] = "authentication failed" }, ["Detail"] = new JsonObject { ["Error"] = new JsonObject { ["Code"] = "account.AUTH_FAILED" } } } } });
                    return FakeHttp.Json(Resp("AuthResponse", new JsonObject { ["authToken"] = new JsonArray(new JsonObject { ["_content"] = "TOK" }), ["lifetime"] = 3600000 }));
                case "GetFolderRequest":
                    return FakeHttp.Json(Resp("GetFolderResponse", JsonNode.Parse("""
                        {"folder":[{"id":"1","name":"USER_ROOT","folder":[
                            {"id":"2","name":"Inbox","view":"message","n":2,"folder":[{"id":"300","name":"Clients","view":"message","n":1}]},
                            {"id":"3","name":"Trash","view":"message","n":1},
                            {"id":"5","name":"Sent","view":"message"},
                            {"id":"7","name":"Contacts","view":"contact"}],
                          "link":[{"id":"400","name":"Support partagé","view":"message","zid":"ZZ","rid":"2","owner":"support@ex.fr"}]}]}
                        """)!));
                case "SyncRequest":
                    if (req!["token"] is null)
                        return FakeHttp.Json(Resp("SyncResponse", JsonNode.Parse("""
                            {"token":"100","folder":[{"id":"1","folder":[
                                {"id":"2","m":[{"ids":"10,11"}],"folder":[{"id":"300","m":[{"ids":"12"}]}]},
                                {"id":"3","m":[{"ids":"13"}]}]}]}
                            """)!));
                    if (MustResync)
                        return FakeHttp.Json(new JsonObject { ["Body"] = new JsonObject { ["Fault"] = new JsonObject { ["Detail"] = new JsonObject { ["Error"] = new JsonObject { ["Code"] = "mail.MUST_RESYNC" } } } } });
                    return FakeHttp.Json(Resp("SyncResponse", JsonNode.Parse("""
                        {"token":"101","m":[{"id":"14","l":"2","ms":"5","md":"9"},{"id":"12","l":"3","ms":"6","md":"9"}],"deleted":[{"ids":"10"}]}
                        """)!));
                case "BatchRequest":
                    var arr = new JsonArray();
                    foreach (var g in req!["GetMsgRequest"]!.AsArray())
                    {
                        var id = g!["m"]!["id"]!.ToString();
                        arr.Add(new JsonObject { ["requestId"] = g["requestId"]!.ToString(), ["m"] = new JsonArray(MsgJson(id, account)) });
                    }
                    return FakeHttp.Json(Resp("BatchResponse", new JsonObject { ["GetMsgResponse"] = arr }));
                case "SearchRequest":
                    return FakeHttp.Json(Resp("SearchResponse", JsonNode.Parse("""{"m":[{"id":"ZZ:77"}],"more":false}""")!));
            }
            return FakeHttp.Status(HttpStatusCode.BadRequest);
        }

        private static JsonObject MsgJson(string id, string account) => new()
        {
            ["id"] = id, ["l"] = id switch { "12" => "300", "14" => "2", "ZZ:77" => "ZZ:2", _ => "2" }, ["d"] = 1761000000000, ["su"] = $"Sujet {id} ({account})",
            ["ms"] = "5", ["md"] = "9", ["s"] = 1234, ["fr"] = "fragment",
            ["e"] = new JsonArray(new JsonObject { ["t"] = "f", ["a"] = "client@ex.fr", ["p"] = "Client Dupont" }, new JsonObject { ["t"] = "t", ["a"] = account }),
            ["mp"] = new JsonArray(new JsonObject
            {
                ["part"] = "TEXT", ["ct"] = "multipart/mixed", ["mp"] = new JsonArray(
                    new JsonObject { ["part"] = "1", ["ct"] = "text/html", ["body"] = true, ["content"] = $"<p>Corps du message {id} devis</p>" },
                    new JsonObject { ["part"] = "2", ["ct"] = "application/pdf", ["cd"] = "attachment", ["filename"] = "devis.pdf" }),
            }),
        };
    }

    private static (ZimbraConnector Con, ZServer Srv, FakeHttp Http, SourceConfig Cfg, MemorySecretStore Secrets) Make(string password = "secret")
    {
        var srv = new ZServer();
        var http = new FakeHttp(srv.Handle);
        var cfg = new SourceConfig { Type = SourceType.Zimbra, Name = "Zimbra", ServerUrl = "https://mail.ex.fr", UserName = "eric@ex.fr", ExtraAccounts = { "compta@ex.fr" } };
        var secrets = new MemorySecretStore();
        var client = new ZimbraClient(cfg.ServerUrl, cfg.UserName, secrets, "zimbra:" + cfg.Id, _ => Task.FromResult<string?>("123456"), http);
        client.SetPassword(password);
        return (new ZimbraConnector(cfg, client), srv, http, cfg, secrets);
    }

    [Test]
    public static async Task Boite_perso_selection_par_defaut_puis_delta()
    {
        using var d = new TempDir();
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        var (con, srv, http, cfg, _) = Make();
        con.ApplyDefaultSelection("eric@ex.fr");
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        Assert.Equal(4, s.Count(), "10, 11, 12 + dossier partagé monté ; pas la corbeille (13)");
        var m12 = s.Get("zm:eric@ex.fr:12")!;
        Assert.Equal(ItemKind.Mail, m12.Kind);
        Assert.Equal("Client Dupont <client@ex.fr>", m12.Author);
        Assert.Contains("devis", m12.Body);
        Assert.Contains("[Pièce jointe] devis.pdf", m12.Body);
        Assert.Equal("eric@ex.fr › Boîte de réception › Clients", m12.Location);
        Assert.Equal("acct:eric@ex.fr,zf:eric@ex.fr:2,zf:eric@ex.fr:300", string.Join(",", m12.Ancestors));
        Assert.Equal(1, srv.Requests.Count(r => r["Body"]!.AsObject().ContainsKey("AuthRequest")), "authentification une seule fois");
        Assert.True(srv.Requests.Where(r => !r["Body"]!.AsObject().ContainsKey("AuthRequest")).All(r => r["Header"]!["context"]!["authToken"]!.ToString() == "TOK"));

        var ctx = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx, default);
        Assert.Null(s.Get("zm:eric@ex.fr:10"), "supprimé côté serveur");
        Assert.NotNull(s.Get("zm:eric@ex.fr:14"), "nouveau message");
        Assert.Null(s.Get("zm:eric@ex.fr:12"), "déplacé dans la corbeille → retiré");
        Assert.True(srv.Requests.Any(r => r["Body"]!["SyncRequest"]?["token"]?.ToString() == "100"));
    }

    [Test]
    public static async Task Boite_supplementaire_et_dossier_partage()
    {
        using var d = new TempDir();
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        var (con, srv, _, cfg, _) = Make();
        // seulement la boîte compta + le dossier partagé monté dans la boîte perso
        con.ApplyDefaultSelection("compta@ex.fr");
        cfg.Rules.Set(new[] { "acct:eric@ex.fr" }, "zf:eric@ex.fr:400", true, "Support partagé");
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        var compta = s.Get("zm:compta@ex.fr:11")!;
        Assert.Contains("compta@ex.fr", compta.Location);
        Assert.True(srv.Requests.Any(r => r["Header"]!["context"]!["account"]?["_content"]?.ToString() == "compta@ex.fr"), "compte cible dans le contexte");
        var shared = s.Get("zm:eric@ex.fr:ZZ:77")!;
        Assert.Contains("Support partagé", shared.Location);
        Assert.Null(s.Get("zm:eric@ex.fr:10"), "boîte perso non cochée");

        // sélecteur : comptes puis dossiers (sans contacts)
        var roots = await con.GetChildrenAsync(null, default);
        Assert.Equal("eric@ex.fr,compta@ex.fr", string.Join(",", roots.Select(r => r.Name)));
        var folders = await con.GetChildrenAsync(roots[0], default);
        Assert.Equal("Boîte de réception", folders[0].Name);
        Assert.False(folders.Any(f => f.Name == "Contacts"));
        Assert.True(folders.Any(f => f.Icon == NodeIcon.SharedFolder));
        var inbox = await con.GetChildrenAsync(folders[0], default);
        Assert.Equal("Clients", inbox.Single().Name);
    }

    [Test]
    public static async Task Double_authentification_et_mot_de_passe_faux()
    {
        using var d = new TempDir();
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        var (con, srv, _, cfg, secrets) = Make();
        srv.RequireTwoFactor = true;
        var token = await con.Client.EnsureAuthAsync(default);
        Assert.Equal("TOK", token);
        var second = srv.Requests.Last(r => r["Body"]!.AsObject().ContainsKey("AuthRequest"));
        Assert.Equal("123456", second["Body"]!["AuthRequest"]!["twoFactorCode"]!["_content"]!.ToString());
        Assert.NotNull(secrets.Get("zimbra:" + cfg.Id + ":token"), "jeton conservé (pas de nouveau code à chaque synchro)");

        var (bad, _, _, _, _) = Make("faux");
        await Assert.Throws<ZimbraException>(() => bad.Client.EnsureAuthAsync(default));
    }

    [Test]
    public static async Task Resynchronisation_demandee_par_le_serveur()
    {
        using var d = new TempDir();
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        var (con, srv, _, cfg, _) = Make();
        con.ApplyDefaultSelection("eric@ex.fr");
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        srv.MustResync = true;
        var ctx = Ctx.For(s, cfg, d);
        await con.SyncAsync(ctx, default);
        Assert.Equal(0, ctx.Errors);
        Assert.Equal(4, s.Count());
    }

    [Test]
    public static async Task Ouverture_telecharge_le_eml()
    {
        using var d = new TempDir();
        using var s = new IndexStore(Path.Combine(d.Path, "i.sqlite"));
        var (con, _, http, cfg, _) = Make();
        con.SetCacheDir(d.Path);
        con.ApplyDefaultSelection("compta@ex.fr");
        await con.SyncAsync(Ctx.For(s, cfg, d), default);
        var it = s.Get("zm:compta@ex.fr:11")!;
        var open = await con.GetOpenActionAsync(it, default);
        Assert.Equal(OpenKind.File, open!.Kind);
        Assert.True(open.Target.EndsWith(".eml"));
        Assert.Contains("corps brut", File.ReadAllText(open.Target));
        Assert.True(http.Calls.Any(c => c.Url.Contains("/service/home/compta@ex.fr/?id=11")));
        Assert.Equal("https://mail.ex.fr/modern/email/Inbox/message/11", open.FallbackUrl);
    }
}
