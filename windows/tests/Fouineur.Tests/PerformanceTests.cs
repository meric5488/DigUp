using System.Diagnostics;
using Fouineur.Core.Index;
using Fouineur.Core.Model;

namespace Fouineur.Tests;

public static class PerformanceTests
{
    [Test]
    public static async Task La_recherche_n_attend_pas_la_synchronisation()
    {
        using var d = new TempDir();
        using var s = IndexTests.NewStore(d);
        s.Upsert(IndexTests.Item("1", ItemKind.Mail, "Devis", "devis pour le serveur"));
        var holding = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        // Une « synchronisation » garde une transaction d'écriture ouverte.
        var writer = Task.Run(() =>
        {
            using (s.Batch())
            {
                s.Upsert(IndexTests.Item("2", ItemKind.Mail, "Autre", "pas encore validé"));
                holding.Set();
                release.Wait(10_000);
            }
        });
        holding.Wait();
        var sw = Stopwatch.StartNew();
        var r = await Task.Run(() => s.Search(new SearchRequest { Text = "devis" }, null));
        sw.Stop();
        release.Set();
        await writer;
        Assert.Equal(1, r.Hits.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"bloquée {sw.ElapsedMilliseconds} ms");
        Assert.Equal(2, s.Count(), "la nouvelle écriture est visible une fois validée");
    }

    [Test]
    public static void Recherche_rapide_sur_50000_elements()
    {
        // ~1 minute de préparation : lancé seulement sur demande (FOUINEUR_PERF=1).
        if (Environment.GetEnvironmentVariable("FOUINEUR_PERF") != "1") { Console.WriteLine("       (ignoré : FOUINEUR_PERF=1 pour le lancer)"); return; }
        using var d = new TempDir();
        using var s = IndexTests.NewStore(d);
        var rnd = new Random(42);
        string[] words = "projet budget serveur facture réunion contrat devis planning sauvegarde réseau imprimante licence client rapport incident mise à jour sécurité messagerie téléphone formation".Split(' ');
        string Text(int n) => string.Join(' ', Enumerable.Range(0, n).Select(_ => words[rnd.Next(words.Length)] + rnd.Next(500)));
        var sw = Stopwatch.StartNew();
        using (s.Batch())
            for (int i = 0; i < 50_000; i++)
                s.Upsert(IndexTests.Item("id" + i, (ItemKind)(1 + i % 5), "Titre " + Text(4), Text(150) + " budget serveur", daysAgo: i % 900));
        Console.WriteLine($"       remplissage : {sw.ElapsedMilliseconds} ms");

        // vecteurs : 2 passages de 256 dimensions par élément
        var vec = new float[256];
        foreach (var p in s.GetPendingEmbeddings(100_000))
        {
            for (int k = 0; k < 256; k++) vec[k] = (float)rnd.NextDouble() - 0.5f;
            var v = Fouineur.Core.Embedding.EmbeddingGemmaPrompts.Truncate(vec, 256);
            s.SaveEmbeddings(p.RowId, new[] { (0, 100, v), (100, 100, v) });
        }
        Console.WriteLine($"       vecteurs : {sw.ElapsedMilliseconds} ms");

        var q = Fouineur.Core.Embedding.EmbeddingGemmaPrompts.Truncate(Enumerable.Range(0, 256).Select(k => (float)Math.Sin(k)).ToArray(), 256);
        s.Search(new SearchRequest { Text = "budget" }, q, 0.1f); // premier appel : chargement des vecteurs
        foreach (var query in new[] { "budget", "serveur facture", "\"budget serveur\"", "proj", "réunion -contrat" })
        {
            var t = Stopwatch.StartNew();
            var r = s.Search(new SearchRequest { Text = query, Limit = 200 }, q, 0.1f);
            t.Stop();
            Console.WriteLine($"       « {query} » : {r.Total} résultats en {t.ElapsedMilliseconds} ms");
            Assert.True(r.Hits.Count > 0);
            Assert.True(t.ElapsedMilliseconds < 1500, $"« {query} » : {t.ElapsedMilliseconds} ms");
        }
    }
}
