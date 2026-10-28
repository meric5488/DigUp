using System.Net;
using System.Text.Json.Nodes;
using Fouineur.Core.Ai;
using Fouineur.Core.Configuration;
using Fouineur.Core.Embedding;
using Fouineur.Core.Index;
using Fouineur.Core.Model;
using Fouineur.Core.Sync;

namespace Fouineur.Tests;

public static class ClaudeTests
{
    private static string Sse(params string[] texts)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{}}\n\n");
        sb.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
        foreach (var t in texts)
            sb.Append("event: content_block_delta\ndata: ").Append(new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = t },
            }.ToJsonString()).Append("\n\n");
        sb.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        return sb.ToString();
    }

    private static (ClaudeSearchService Svc, FakeHttp Http, IndexStore Store, TempDir Dir) Make(Func<string, string, string, (HttpStatusCode, string, string?)> route, Func<string, bool>? allowed = null)
    {
        var d = new TempDir();
        var s = IndexTests.NewStore(d);
        s.Upsert(IndexTests.Item("m1", ItemKind.Mail, "Contrat de maintenance serveur Metz", "Le contrat de maintenance du serveur de Metz expire le 31 mars 2027. Renouvellement à prévoir.", author: "Paul Martin <paul@ex.fr>", source: "zimbra"));
        s.Upsert(IndexTests.Item("f1", ItemKind.SharePoint, "Inventaire.xlsx", "serveur Metz, garantie, contrat maintenance HPE", source: "m365"));
        s.Upsert(IndexTests.Item("t1", ItemKind.Teams, "Fil incident", "le serveur de Metz a redémarré", source: "teams"));
        var http = new FakeHttp(route);
        var client = new ClaudeClient(() => "sk-ant-test", http);
        var search = new SearchService(s, () => NoEmbedder.Instance);
        var svc = new ClaudeSearchService(client, search, s, () => new ClaudeSettings { MaxDocuments = 5 }, allowed ?? (_ => true));
        return (svc, http, s, d);
    }

    private static readonly JsonObject PlanResponse = new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "tool_use", ["name"] = "rechercher",
            ["input"] = new JsonObject { ["mots_cles"] = "contrat maintenance serveur Metz", ["categorie"] = "tout", ["periode"] = "tout", ["auteur"] = "", ["resume"] = "Date de fin du contrat" },
        }),
    };

    [Test]
    public static async Task Plan_puis_recherche_puis_reponse_en_flux()
    {
        var (svc, http, store, dir) = Make((m, url, body) =>
        {
            var req = JsonNode.Parse(body)!;
            if (req["stream"]?.GetValue<bool>() == true) return (HttpStatusCode.OK, Sse("Le contrat expire ", "le 31 mars 2027 [1]."), "text/event-stream");
            return FakeHttp.Json(PlanResponse);
        });
        using (store) using (dir)
        {
            var plan = await svc.PlanAsync("quand expire le contrat de maintenance du serveur de Metz ?", default);
            Assert.Equal("contrat maintenance serveur Metz", plan.Keywords);
            Assert.Null(plan.Kind);
            var ctx = await svc.RetrieveAsync("quand expire le contrat ?", plan, null, default);
            Assert.True(ctx.Sources.Count >= 2);
            Assert.Equal("m1", ctx.Sources[0].Item.Id, "le mail le plus pertinent en premier");
            Assert.Contains("31 mars 2027", ctx.Passages[0]);
            var answer = string.Concat(await ToList(svc.AnswerAsync(ctx, default)));
            Assert.Equal("Le contrat expire le 31 mars 2027 [1].", answer);

            // en-têtes et contenu envoyés
            var call = http.Calls.Last();
            Assert.True(call.Url == ClaudeClient.Endpoint);
            var sent = JsonNode.Parse(call.Body)!;
            Assert.Equal("claude-sonnet-5-5", sent["model"]!.GetValue<string>());
            Assert.Contains("<document numero=\"1\"", sent["messages"]![0]!["content"]!.GetValue<string>());
            var planCall = JsonNode.Parse(http.Calls[0].Body)!;
            Assert.Equal("claude-haiku-5-5", planCall["model"]!.GetValue<string>());
            Assert.Equal("rechercher", planCall["tool_choice"]!["name"]!.GetValue<string>());
        }
    }

    [Test]
    public static async Task Sources_non_autorisees_jamais_envoyees()
    {
        var (svc, http, store, dir) = Make((m, url, body) => (HttpStatusCode.OK, Sse("ok"), "text/event-stream"), allowed: src => src != "zimbra");
        using (store) using (dir)
        {
            var plan = new SearchPlan("contrat maintenance serveur Metz", null, DateFilter.Tout, null, "");
            var ctx = await svc.RetrieveAsync("question", plan, null, default);
            Assert.False(ctx.Sources.Any(h => h.Item.SourceId == "zimbra"));
            Assert.True(ctx.Excluded >= 1);
            await ToList(svc.AnswerAsync(ctx, default));
            Assert.NotContains("31 mars 2027", http.Calls.Last().Body);
        }
    }

    [Test]
    public static async Task Recherche_elargie_si_rien_ne_correspond_strictement()
    {
        var (svc, _, store, dir) = Make((m, u, b) => FakeHttp.Json("{}"));
        using (store) using (dir)
        {
            // « garantie » et « imprimante » ne sont jamais ensemble : la recherche stricte échoue, l'élargie trouve.
            var plan = new SearchPlan("garantie imprimante", ItemKind.Mail, DateFilter.SeptJours, null, "");
            var ctx = await svc.RetrieveAsync("garantie imprimante", plan, null, default);
            Assert.True(ctx.Sources.Any(h => h.Item.Id == "f1"));
        }
    }

    [Test]
    public static async Task Erreurs_de_l_API_lisibles()
    {
        var (svc, _, store, dir) = Make((m, u, b) => FakeHttp.Status(HttpStatusCode.Unauthorized, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}"));
        using (store) using (dir)
        {
            try { await svc.PlanAsync("x", default); throw new Exception("exception attendue"); }
            catch (ClaudeException e) { Assert.Equal(401, e.Status); Assert.Contains("clé d'API refusée", e.Message); }
        }
        var noKey = new ClaudeClient(() => null, new FakeHttp((m, u, b) => FakeHttp.Json("{}")));
        Assert.False(noKey.HasKey);
        await Assert.Throws<ClaudeException>(() => noKey.CreateAsync(new JsonObject(), default));
    }

    [Test]
    public static void Plan_mal_forme_et_ou_dans_la_requete()
    {
        var p = ClaudeSearchService.ParsePlan(new JsonObject { ["content"] = new JsonArray() }, "ma question");
        Assert.Equal("ma question", p.Keywords);
        var p2 = ClaudeSearchService.ParsePlan(new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["input"] = new JsonObject { ["mots_cles"] = "devis", ["categorie"] = "mails", ["periode"] = "30j", ["auteur"] = "Dupont" } }),
        }, "q");
        Assert.Equal(ItemKind.Mail, p2.Kind);
        Assert.Equal(DateFilter.TrenteJours, p2.Date);
        Assert.Equal("Mails · 30 derniers jours · de Dupont · « devis »", p2.Describe());

        var q = QueryParser.Parse("garantie OR imprimante");
        Assert.Contains(" OR ", q.FtsQuery);
    }

    private static async Task<List<string>> ToList(IAsyncEnumerable<string> e)
    {
        var l = new List<string>();
        await foreach (var x in e) l.Add(x);
        return l;
    }
}
