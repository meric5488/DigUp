using System.Text.Json.Nodes;
using Fouineur.Core.Inventory;
using Fouineur.Core.Model;

namespace Fouineur.Tests;

public static class InventoryTests
{
    private static IndexedItem Pc(string id, string name, string os, string ip, string user, string lieu, params (string, string)[] sw)
    {
        var it = new IndexedItem
        {
            Id = $"glpi:Computer:{id}", SourceId = "g", Kind = ItemKind.Glpi, Title = name, Author = user,
            Date = DateTimeOffset.Now, Location = "GLPI", OpenUri = $"https://glpi.ex.fr/front/computer.form.php?id={id}",
            Extension = ".glpi", Version = "v", Body = name,
        };
        it.SetExtra("inv", "1").SetExtra("os", os).SetExtra("ip", ip).SetExtra("lieu", lieu).SetExtra("glpiType", "Ordinateur");
        it.Extra["sw"] = new JsonArray(sw.Select(p => (JsonNode)new JsonArray(p.Item1, p.Item2)).ToArray());
        return it;
    }

    private static Fouineur.Core.Index.IndexStore Fill(TempDir d)
    {
        var s = IndexTests.NewStore(d);
        s.Upsert(Pc("1", "PC-COMPTA-01", "Microsoft Windows 10 Professionnel 22H2", "10.12.0.45", "Sophie", "Metz > 2e étage",
            ("Mozilla Firefox", "115.12.0"), ("Microsoft Office Professionnel Plus 2016", "16.0.4266.1001"), ("7-Zip", "19.00")));
        s.Upsert(Pc("2", "PC-DIR-02", "Microsoft Windows 11 Professionnel 23H2", "10.12.0.46, 192.168.1.20", "Eric", "Metz > 3e étage",
            ("Mozilla Firefox", "128.0.3"), ("Microsoft 365 Apps for enterprise", "16.0.17928"), ("7-Zip", "24.08")));
        s.Upsert(Pc("3", "SRV-FICHIERS", "Microsoft Windows Server 2019 Standard", "10.20.0.10", "", "Salle serveur",
            ("7-Zip", "9.20")));
        // Un ticket ne fait pas partie de l'inventaire
        s.Upsert(new IndexedItem { Id = "glpi:Ticket:5", SourceId = "g", Kind = ItemKind.Glpi, Title = "Firefox plante", Body = "firefox", Version = "v", Date = DateTimeOffset.Now });
        return s;
    }

    [Test]
    public static void Logiciel_et_version_avec_la_syntaxe_directe()
    {
        using var d = new TempDir();
        using var s = Fill(d);
        var r = InventoryService.Run(s, InventoryQuery.Parse("logiciel:firefox"));
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(3, r.ScannedCount, "le ticket n'est pas compté");
        Assert.Equal("115.12.0", r.Rows[0].Version);

        r = InventoryService.Run(s, InventoryQuery.Parse("logiciel:firefox version<128"));
        Assert.Equal(1, r.Rows.Count);
        Assert.Equal("PC-COMPTA-01", r.Rows[0].Asset);

        // Comparaison naturelle : 9.20 < 19.00 < 24.08
        r = InventoryService.Run(s, InventoryQuery.Parse("7-zip version<20"));
        Assert.Equal(2, r.AssetCount);

        // Année dans le nom : Office 2016 est « antérieur à 2019 » même si sa version est 16.0
        r = InventoryService.Run(s, InventoryQuery.Parse("logiciel:office version<2019"));
        Assert.Equal(1, r.Rows.Count);
        Assert.Contains("2016", r.Rows[0].Software);
    }

    [Test]
    public static void Systeme_ip_lieu_et_postes_sans_logiciel()
    {
        using var d = new TempDir();
        using var s = Fill(d);
        var r = InventoryService.Run(s, InventoryQuery.Parse(@"os:""windows 10"""));
        Assert.Equal(1, r.Rows.Count);
        Assert.Equal("", r.Rows[0].Software, "sans critère logiciel : une ligne par poste");

        Assert.Equal(2, InventoryService.Run(s, InventoryQuery.Parse("ip:10.12.")).AssetCount);
        Assert.Equal(1, InventoryService.Run(s, InventoryQuery.Parse("ip:192.168.1.20")).AssetCount);
        Assert.Equal(2, InventoryService.Run(s, InventoryQuery.Parse("lieu:metz")).AssetCount);
        Assert.Equal(1, InventoryService.Run(s, InventoryQuery.Parse("sans:firefox")).AssetCount, "le serveur n'a pas Firefox");
        Assert.Equal(1, InventoryService.Run(s, InventoryQuery.Parse("os:server 7-zip")).AssetCount, "texte libre = logiciel");
        Assert.Equal(1, InventoryService.Run(s, InventoryQuery.Parse("logiciel:firefox utilisateur:éric")).AssetCount, "sans accents");

        var csv = InventoryService.ToCsv(InventoryService.Run(s, InventoryQuery.Parse("firefox")).Rows);
        Assert.Contains("Poste;Type;Logiciel;Version", csv);
        Assert.Contains("PC-DIR-02;Ordinateur;Mozilla Firefox;128.0.3", csv);
    }

    [Test]
    public static void Versions_naturelles()
    {
        Assert.True(InventoryQuery.CompareVersions("9.2", "10.0") < 0);
        Assert.True(InventoryQuery.CompareVersions("128.0.3", "115.12") > 0);
        Assert.Equal(0, InventoryQuery.CompareVersions("24.08", "24.8"));
        Assert.True(InventoryQuery.CompareVersions("16.0", "16.0.1") < 0);
        Assert.True(InventoryQuery.HasSyntax("logiciel:x"));
        Assert.False(InventoryQuery.HasSyntax("liste des postes avec firefox"));
    }

    [Test]
    public static void Claude_traduit_la_question_en_filtre()
    {
        var resp = JsonNode.Parse("""
            {"content":[{"type":"tool_use","name":"inventaire","input":{"logiciel":"office","version_operateur":"inferieur","version":"2019","systeme":"windows 10","ip":"","sans_logiciel":false}}]}
            """)!.AsObject();
        var f = ClaudeInventoryPlanner.Parse(resp);
        Assert.Equal("office", f.Software);
        Assert.Equal(VersionOp.Inferieur, f.VersionOp);
        Assert.Equal("windows 10", f.Os);
        Assert.Null(f.Ip);
        Assert.Contains("version < 2019", f.Describe());
    }
}
