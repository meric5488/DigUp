using System.IO.Compression;
using System.Text;
using Fouineur.Core.Extraction;
using Fouineur.Core.Selection;

namespace Fouineur.Tests;

public static class SelectionTests
{
    [Test]
    public static void La_regle_la_plus_proche_gagne()
    {
        var r = new SelectionRules();
        r.Set(Array.Empty<string>(), "site", true);
        r.Set(new[] { "site" }, "bib", false);
        r.Set(new[] { "site", "bib" }, "dossier", true);
        Assert.True(r.IsIncluded(new[] { "site", "autre" }));
        Assert.False(r.IsIncluded(new[] { "site", "bib", "x" }));
        Assert.True(r.IsIncluded(new[] { "site", "bib", "dossier", "sous", "fichier" }), "héritage pour les nouveaux éléments");
        Assert.False(r.IsIncluded(new[] { "ailleurs" }));
    }

    [Test]
    public static void Cocher_un_parent_efface_les_regles_des_enfants()
    {
        var r = new SelectionRules();
        r.Set(new[] { "site" }, "a", true);
        r.Set(new[] { "site" }, "b", true);
        Assert.Equal(CheckState.Indeterminate, r.GetState(Array.Empty<string>(), "site"));
        Assert.True(r.AnyIncludedUnder(Array.Empty<string>(), "site"));
        r.Set(Array.Empty<string>(), "site", true);
        Assert.Equal(1, r.Rules.Count);
        Assert.Equal(CheckState.Checked, r.GetState(Array.Empty<string>(), "site"));
        Assert.Equal(CheckState.Checked, r.GetState(new[] { "site" }, "a"));
        r.Set(new[] { "site" }, "a", false);
        Assert.Equal(CheckState.Indeterminate, r.GetState(Array.Empty<string>(), "site"));
        r.Set(new[] { "site" }, "a", true);
        Assert.Equal(1, r.Rules.Count, "règle redondante supprimée");
        r.Set(Array.Empty<string>(), "site", false);
        Assert.Equal(0, r.Rules.Count);
        Assert.Equal(CheckState.Unchecked, r.GetState(Array.Empty<string>(), "site"));
    }

    [Test]
    public static void Empreinte_change_seulement_pour_le_perimetre_concerne()
    {
        var r = new SelectionRules();
        r.Set(Array.Empty<string>(), "d1", true);
        var f1 = r.Fingerprint(new[] { "d1" });
        var f2 = r.Fingerprint(new[] { "d2" });
        r.Set(new[] { "d2" }, "x", true);
        Assert.Equal(f1, r.Fingerprint(new[] { "d1" }));
        Assert.True(f2 != r.Fingerprint(new[] { "d2" }));
        var clone = r.Clone();
        clone.Set(Array.Empty<string>(), "d1", false);
        Assert.True(r.IsIncluded(new[] { "d1" }), "le clone est indépendant");
    }
}

public static class ExtractionTests
{
    private static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, c) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false));
                w.Write(c);
            }
        ms.Position = 0;
        return ms;
    }

    private static readonly TextExtractors X = TextExtractors.CreateDefault();

    [Test]
    public static void Word()
    {
        const string w = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        var doc = $"<w:document {w}><w:body><w:p><w:r><w:t>Bonjour</w:t></w:r><w:r><w:tab/><w:t xml:space=\"preserve\"> le monde</w:t></w:r></w:p><w:p><w:r><w:t>Seconde ligne</w:t></w:r></w:p></w:body></w:document>";
        var hdr = $"<w:hdr {w}><w:p><w:r><w:t>En-tête confidentiel</w:t></w:r></w:p></w:hdr>";
        var t = X.Extract(Zip(("word/document.xml", doc), ("word/header1.xml", hdr)), ".docx");
        Assert.Contains("Bonjour le monde", t);
        Assert.Contains("\nSeconde ligne", t);
        Assert.Contains("En-tête confidentiel", t);
    }

    [Test]
    public static void Excel_avec_chaines_partagees()
    {
        const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";
        var t = X.Extract(Zip(
            ("xl/workbook.xml", $"<workbook {ns}><sheets><sheet name=\"Budget\" sheetId=\"1\"/></sheets></workbook>"),
            ("xl/sharedStrings.xml", $"<sst {ns}><si><t>Poste</t></si><si><t>Montant</t></si><si><r><t>Loyer</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet {ns}><sheetData><row><c t=\"s\"><v>0</v></c><c t=\"s\"><v>1</v></c></row><row><c t=\"s\"><v>2</v></c><c><v>1200</v></c><c t=\"inlineStr\"><is><t>note</t></is></c></row></sheetData></worksheet>")), ".xlsx");
        Assert.Contains("Budget", t);
        Assert.Contains("Poste Montant", t.Replace('\t', ' ').Replace("  ", " "));
        Assert.Contains("Loyer", t);
        Assert.Contains("1200", t);
        Assert.Contains("note", t);
    }

    [Test]
    public static void PowerPoint_dans_l_ordre_des_diapos()
    {
        const string a = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        string Slide(string s) => $"<p:sld xmlns:p=\"x\" {a}><a:p><a:r><a:t>{s}</a:t></a:r></a:p></p:sld>";
        var t = X.Extract(Zip(("ppt/slides/slide10.xml", Slide("Dix")), ("ppt/slides/slide2.xml", Slide("Deux")), ("ppt/notesSlides/notesSlide2.xml", Slide("Note orale"))), ".pptx");
        Assert.True(t.IndexOf("Deux") < t.IndexOf("Dix"));
        Assert.Contains("Note orale", t);
    }

    [Test]
    public static void OpenDocument_texte()
    {
        var c = "<office:document-content xmlns:office=\"o\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><office:body><text:p>Premier<text:s/>paragraphe</text:p><text:h>Titre</text:h></office:body></office:document-content>";
        var t = X.Extract(Zip(("content.xml", c)), ".odt");
        Assert.Contains("Premier paragraphe", t);
        Assert.Contains("Titre", t);
    }

    [Test]
    public static void Rtf_html_texte()
    {
        var rtf = @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Caf\'e9 cr\u232?me\par Ligne 2}";
        var t = X.Extract(new MemoryStream(Encoding.ASCII.GetBytes(rtf)), ".rtf");
        Assert.Contains("Café crème", t);
        Assert.Contains("Ligne 2", t);
        var html = "<html><head><title>x</title><style>.a{}</style></head><body><p>Un &amp; deux</p><script>alert(1)</script><div>trois</div></body></html>";
        var h = X.Extract(new MemoryStream(Encoding.UTF8.GetBytes(html)), ".html");
        Assert.Contains("Un & deux", h);
        Assert.Contains("trois", h);
        Assert.NotContains("alert", h);
        var latin = X.Extract(new MemoryStream(new byte[] { 0x65, 0x74, 0xE9, 0x20, 0x80 }), ".txt");
        Assert.Equal("eté €", latin);
        Assert.False(X.CanExtract(".exe"));
    }

    [Test]
    public static void Eml_multipart_et_entetes_encodes()
    {
        var eml = "Subject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Réunion d'équipe")) + "?=\r\n" +
                  "From: =?iso-8859-1?Q?Ren=E9?= <rene@ex.fr>\r\nTo: a@ex.fr\r\n" +
                  "Content-Type: multipart/mixed; boundary=\"XX\"\r\n\r\n" +
                  "--XX\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nOrdre du jour : caf=C3=A9 et =\r\nbudget\r\n" +
                  "--XX\r\nContent-Type: application/pdf\r\nContent-Disposition: attachment; filename=\"cr.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\nAAAA\r\n--XX--\r\n";
        var t = X.Extract(new MemoryStream(Encoding.ASCII.GetBytes(eml)), ".eml");
        Assert.Contains("Réunion d'équipe", t);
        Assert.Contains("René", t);
        Assert.Contains("café et budget", t);
        Assert.Contains("[Pièce jointe] cr.pdf", t);
    }
}
