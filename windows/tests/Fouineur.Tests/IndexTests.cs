using Fouineur.Core.Embedding;
using Fouineur.Core.Index;
using Fouineur.Core.Model;
using Fouineur.Core.Sync;

namespace Fouineur.Tests;

/// <summary>Faux modèle : sac de mots haché (les textes qui partagent des mots se ressemblent).</summary>
public sealed class FakeEmbedder : IEmbedder
{
    public string ModelId => "fake:256";
    public int Dimensions => 256;
    public bool IsReady => true;
    public int Calls;
    public static readonly Dictionary<string, string> Synonyms = new() { ["voiture"] = "automobile", ["auto"] = "automobile" };

    public static float[] Vec(string text)
    {
        var v = new float[256];
        foreach (var w0 in QueryParser.Words(text.ToLowerInvariant()))
        {
            var w = Synonyms.TryGetValue(w0, out var s) ? s : w0;
            if (w.Length < 3) continue;
            uint h = 2166136261; foreach (var ch in w) { h ^= ch; h *= 16777619; }
            v[(int)(h % 256)] += 1;
        }
        return EmbeddingGemmaPrompts.Truncate(v, 256);
    }

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<(string Title, string Text)> docs, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(docs.Select(d => Vec(d.Title + " " + d.Text)).ToArray());
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) => Task.FromResult(Vec(query));
}

public static class IndexTests
{
    public static IndexStore NewStore(TempDir d) => new(Path.Combine(d.Path, "index.sqlite"));

    public static IndexedItem Item(string id, ItemKind kind, string title, string body, string author = "", int daysAgo = 0, string ext = ".txt", string source = "s1", params string[] anc) => new()
    {
        Id = id, SourceId = source, Kind = kind, Title = title, Body = body, Author = author,
        Date = DateTimeOffset.UtcNow.AddDays(-daysAgo), Extension = ext, Version = "v1", Ancestors = anc,
    };

    [Test]
    public static void Recherche_sans_accents_et_par_prefixe()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.Upsert(Item("a", ItemKind.Fichier, "Réunion", "Compte rendu de la réunion de l'école primaire"));
        s.Upsert(Item("b", ItemKind.Mail, "Facture", "Votre facture d'électricité"));
        var r = s.Search(new SearchRequest { Text = "ecole" }, null);
        Assert.Equal(1, r.Hits.Count);
        Assert.Equal("a", r.Hits[0].Item.Id);
        Assert.Contains("\u0002école\u0003", r.Hits[0].Snippet);
        r = s.Search(new SearchRequest { Text = "electri" }, null);
        Assert.Equal("b", r.Hits.Single().Item.Id);
    }

    [Test]
    public static void Comptes_par_categorie_et_filtre()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.Upsert(Item("m1", ItemKind.Mail, "Budget 2026", "budget prévisionnel"));
        s.Upsert(Item("m2", ItemKind.Mail, "Re: budget", "ok pour le budget"));
        s.Upsert(Item("t1", ItemKind.Teams, "Fil", "on parle du budget ici"));
        s.Upsert(Item("f1", ItemKind.SharePoint, "Budget.xlsx", "lignes du budget", ext: ".xlsx"));
        var r = s.Search(new SearchRequest { Text = "budget" }, null);
        Assert.Equal(4, r.Total);
        Assert.Equal(2, r.CountsByKind[ItemKind.Mail]);
        Assert.Equal(1, r.CountsByKind[ItemKind.Teams]);
        var mails = s.Search(new SearchRequest { Text = "budget", Kind = ItemKind.Mail }, null);
        Assert.Equal(2, mails.Hits.Count);
        Assert.True(mails.Hits.All(h => h.Item.Kind == ItemKind.Mail));
        Assert.Equal(4, mails.Total, "les comptes restent ceux de toutes les catégories");
        Assert.Equal("m1", mails.Hits[0].Item.Id, "le titre pèse plus lourd");
    }

    [Test]
    public static void Operateurs_exclusion_expression_type_auteur()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.Upsert(Item("1", ItemKind.Fichier, "contrat.pdf", "contrat de maintenance annuel", ext: ".pdf"));
        s.Upsert(Item("2", ItemKind.Fichier, "contrat.docx", "contrat de location", ext: ".docx"));
        s.Upsert(Item("3", ItemKind.Mail, "Relance", "le contrat de maintenance arrive à échéance", author: "Paul Martin"));
        Assert.Equal(2, s.Search(new SearchRequest { Text = "contrat -location" }, null).Hits.Count);
        Assert.Equal(2, s.Search(new SearchRequest { Text = "\"contrat de maintenance\"" }, null).Hits.Count);
        Assert.Equal("1", s.Search(new SearchRequest { Text = "contrat type:pdf" }, null).Hits.Single().Item.Id);
        Assert.Equal("3", s.Search(new SearchRequest { Text = "de:martin contrat" }, null).Hits.Single().Item.Id);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "type:docx" }, null).Hits.Count, "filtre seul = navigation");
    }

    [Test]
    public static void Requetes_bizarres_ne_plantent_pas()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.Upsert(Item("1", ItemKind.Fichier, "x", "AND OR NOT NEAR ( ) * ^ : \" texte"));
        foreach (var q in new[] { "AND", "\"", "(", "*", "NEAR(", "-", "a:b:c", "^x", "l'été", "C#", "100%" })
            s.Search(new SearchRequest { Text = q }, null);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "AND" }, null).Hits.Count, "mot-clé FTS cherché comme un mot");
    }

    [Test]
    public static void Mise_a_jour_et_suppression_suivent_le_texte_integral()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.Upsert(Item("1", ItemKind.Fichier, "note", "ancien contenu pomme"));
        s.Upsert(Item("1", ItemKind.Fichier, "note", "nouveau contenu poire"));
        Assert.Equal(0, s.Search(new SearchRequest { Text = "pomme" }, null).Hits.Count);
        Assert.Equal(1, s.Search(new SearchRequest { Text = "poire" }, null).Hits.Count);
        Assert.Equal(1, s.Count());
        Assert.True(s.Delete("1"));
        Assert.Equal(0, s.Search(new SearchRequest { Text = "poire" }, null).Hits.Count);
        Assert.Equal(0, s.Count());
    }

    [Test]
    public static void Filtre_de_date_et_navigation_sans_texte()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.Upsert(Item("vieux", ItemKind.Mail, "vieux", "rapport", daysAgo: 400));
        s.Upsert(Item("recent", ItemKind.Mail, "recent", "rapport", daysAgo: 2));
        s.Upsert(Item("mois", ItemKind.Fichier, "mois", "rapport", daysAgo: 20));
        Assert.Equal(1, s.Search(new SearchRequest { Text = "rapport", Date = DateFilter.SeptJours }, null).Hits.Count);
        Assert.Equal(2, s.Search(new SearchRequest { Text = "rapport", Date = DateFilter.TrenteJours }, null).Hits.Count);
        var browse = s.Search(new SearchRequest { Text = "" }, null);
        Assert.Equal(3, browse.Hits.Count);
        Assert.Equal("recent", browse.Hits[0].Item.Id, "les plus récents d'abord");
        Assert.Equal(2, browse.CountsByKind[ItemKind.Mail]);
    }

    [Test]
    public static async Task Recherche_hybride_par_le_sens()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.Upsert(Item("1", ItemKind.Fichier, "Parc", "entretien de l'automobile de service et pneus neige"));
        s.Upsert(Item("2", ItemKind.Fichier, "Cuisine", "recette de tarte aux pommes"));
        s.Upsert(Item("3", ItemKind.Mail, "Voiture", "la voiture est au garage"));
        var emb = new FakeEmbedder();
        var w = new EmbeddingWorker(s, emb);
        int n = await w.RunAsync(null, default);
        Assert.Equal(3, n);
        Assert.Equal(0, s.PendingEmbeddingCount());
        var search = new SearchService(s, () => emb, () => 0.2f);
        var r = await search.SearchAsync(new SearchRequest { Text = "voiture" }, default);
        Assert.Equal("3", r.Hits[0].Item.Id, "correspondance texte + sens en tête");
        Assert.True(r.Hits.Any(h => h.Item.Id == "1" && h.MatchedMeaning && !h.MatchedText), "trouvé par le sens seul (automobile)");
        Assert.False(r.Hits.Any(h => h.Item.Id == "2"));
        Assert.True(r.SemanticUsed);

        // modification du texte → à revectoriser ; titre seul inchangé → pas de revectorisation
        s.Upsert(Item("2", ItemKind.Fichier, "Cuisine", "recette de tarte aux poires"));
        Assert.Equal(1, s.PendingEmbeddingCount());
        var it3 = Item("3", ItemKind.Mail, "Voiture", "la voiture est au garage"); it3.Location = "ailleurs";
        s.Upsert(it3);
        Assert.Equal(1, s.PendingEmbeddingCount());
    }

    [Test]
    public static async Task Changement_de_modele_revectorise_tout()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.Upsert(Item("1", ItemKind.Fichier, "a", "texte un"));
        await new EmbeddingWorker(s, new FakeEmbedder()).RunAsync(null, default);
        Assert.Equal(0, s.PendingEmbeddingCount());
        s.SetMeta("embed_model", "autre");
        new EmbeddingWorker(s, new FakeEmbedder()).EnsureModel();
        Assert.Equal(1, s.PendingEmbeddingCount());
    }

    [Test]
    public static void Etats_arborescence_et_messages()
    {
        using var d = new TempDir();
        using var s = NewStore(d);
        s.SetState("src", "k", "v");
        Assert.Equal("v", s.GetState("src", "k"));
        s.SetState("src", "k", null);
        Assert.Null(s.GetState("src", "k"));
        s.SetTreeNode("src", "root", null, "Racine");
        s.SetTreeNode("src", "a", "root", "A");
        s.SetTreeNode("src", "b", "a", "B");
        var (ids, names) = s.GetTreePath("src", "b");
        Assert.Equal("root,a,b", string.Join(",", ids));
        Assert.Equal("Racine,A,B", string.Join(",", names));
        s.UpsertMessage("src", "m1", "doc1", DateTimeOffset.UtcNow.AddMinutes(-5), "Alice", "salut", "");
        s.UpsertMessage("src", "m2", "doc1", DateTimeOffset.UtcNow, "Bob", "bonjour", "");
        Assert.Equal("Alice", s.GetMessages("src", "doc1")[0].Author);
        Assert.Equal("doc1", s.DeleteMessage("src", "m1"));
        Assert.Equal(1, s.GetMessages("src", "doc1").Count);
    }

    [Test]
    public static void Chunker_couvre_tout_le_texte()
    {
        var text = string.Join(" ", Enumerable.Range(0, 2000).Select(i => "mot" + i));
        var chunks = Chunker.Split(text, 1000, 100, 100);
        Assert.True(chunks.Count > 5);
        Assert.Equal(0, chunks[0].Start);
        var last = chunks[^1];
        Assert.Equal(text.Length, last.Start + last.Length);
        for (int i = 1; i < chunks.Count; i++) Assert.True(chunks[i].Start < chunks[i - 1].Start + chunks[i - 1].Length, "chevauchement");
        Assert.Equal(1, Chunker.Split("").Count);
    }

    [Test]
    public static void Parseur_de_requete()
    {
        var p = QueryParser.Parse("budget \"plan de charge\" -brouillon de:paul type:pdf,docx");
        Assert.Contains("\"budget\"*", p.FtsQuery);
        Assert.Contains("\"plan de charge\"", p.FtsQuery);
        Assert.Contains("NOT", p.FtsQuery);
        Assert.Contains("author : (\"paul\"*)", p.FtsQuery);
        Assert.Equal(".pdf,.docx", string.Join(",", p.Extensions));
        Assert.Equal("budget plan de charge", p.SemanticText);
        Assert.True(QueryParser.Parse("   ").IsEmpty);
    }
}
