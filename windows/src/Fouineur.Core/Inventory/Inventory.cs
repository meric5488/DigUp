using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fouineur.Core.Ai;
using Fouineur.Core.Configuration;
using Fouineur.Core.Index;

namespace Fouineur.Core.Inventory;

public enum VersionOp { Aucun, Egal, CommencePar, Inferieur, InferieurOuEgal, Superieur, SuperieurOuEgal }

/// <summary>Critères d'une requête d'inventaire (tous facultatifs, combinés par ET).</summary>
public sealed class InventoryFilter
{
    public string? Software { get; set; }
    /// <summary>Postes qui n'ont PAS le logiciel (ex. « postes sans antivirus »).</summary>
    public bool WithoutSoftware { get; set; }
    public VersionOp VersionOp { get; set; }
    public string? Version { get; set; }
    public string? Os { get; set; }
    public string? Ip { get; set; }
    public string? Asset { get; set; }
    public string? Location { get; set; }
    public string? User { get; set; }

    public bool IsEmpty => Software is null && Os is null && Ip is null && Asset is null && Location is null && User is null;

    private static string OpText(VersionOp op) => op switch
    {
        VersionOp.Egal => "=", VersionOp.CommencePar => "commence par", VersionOp.Inferieur => "<",
        VersionOp.InferieurOuEgal => "≤", VersionOp.Superieur => ">", VersionOp.SuperieurOuEgal => "≥", _ => "",
    };

    /// <summary>Texte lisible, affiché au-dessus du tableau (« Interprété : … »).</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Software is not null) parts.Add((WithoutSoftware ? "sans le logiciel « " : "logiciel « ") + Software + " »");
        if (Version is not null && VersionOp != VersionOp.Aucun) parts.Add($"version {OpText(VersionOp)} {Version}");
        if (Os is not null) parts.Add($"système « {Os} »");
        if (Ip is not null) parts.Add($"IP « {Ip} »");
        if (Asset is not null) parts.Add($"nom « {Asset} »");
        if (Location is not null) parts.Add($"lieu « {Location} »");
        if (User is not null) parts.Add($"utilisateur « {User} »");
        return parts.Count == 0 ? "tout l'inventaire" : string.Join(", ", parts);
    }
}

public sealed record InventoryRow(
    string ItemId, string Asset, string AssetType, string Software, string Version,
    string Os, string Ips, string User, string Location, string Serial, string OpenUri, DateTimeOffset Date);

public sealed record InventoryResult(InventoryFilter Filter, List<InventoryRow> Rows, int AssetCount, int ScannedCount, bool HasSoftwareData);

/// <summary>Syntaxe directe : logiciel:firefox version&lt;128 os:"windows 10" ip:10.12. poste:PC- lieu:metz utilisateur:sophie sans:antivirus</summary>
public static class InventoryQuery
{
    private static readonly Regex Token = new(
        @"(?<key>logiciel|logiciels|soft|software|sans|version|os|systeme|système|ip|poste|nom|lieu|utilisateur|user)\s*(?<op><=|>=|<|>|=|:)\s*(?:""(?<val>[^""]*)""|(?<val>[^\s""]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Le texte contient-il au moins un critère de la syntaxe directe ?</summary>
    public static bool HasSyntax(string text) => Token.IsMatch(text);

    public static InventoryFilter Parse(string text)
    {
        var f = new InventoryFilter();
        var rest = Token.Replace(text, m =>
        {
            var key = m.Groups["key"].Value.ToLowerInvariant();
            var op = m.Groups["op"].Value;
            var val = m.Groups["val"].Value.Trim();
            if (val.Length == 0) return " ";
            switch (key)
            {
                case "logiciel" or "logiciels" or "soft" or "software": f.Software = val; break;
                case "sans": f.Software = val; f.WithoutSoftware = true; break;
                case "version":
                    f.Version = val;
                    f.VersionOp = op switch
                    {
                        "<" => VersionOp.Inferieur, "<=" => VersionOp.InferieurOuEgal, ">" => VersionOp.Superieur,
                        ">=" => VersionOp.SuperieurOuEgal, "=" => VersionOp.Egal, _ => VersionOp.CommencePar,
                    };
                    break;
                case "os" or "systeme" or "système": f.Os = val; break;
                case "ip": f.Ip = val; break;
                case "poste" or "nom": f.Asset = val; break;
                case "lieu": f.Location = val; break;
                case "utilisateur" or "user": f.User = val; break;
            }
            return " ";
        }).Trim();
        // Texte libre restant : le nom du logiciel (« firefox », « office 2016 »).
        if (rest.Length > 0 && f.Software is null) f.Software = rest;
        return f;
    }

    // ─── Comparaisons ───

    internal static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Tous les mots du critère figurent dans le texte (ordre libre, sans accents ni casse).</summary>
    public static bool AllWords(string? haystack, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return true;
        if (string.IsNullOrEmpty(haystack)) return false;
        var h = Fold(haystack);
        return Fold(needle).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(h.Contains);
    }

    private static readonly Regex Segment = new(@"\d+|[a-z]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Comparaison « naturelle » de versions : 9.2 &lt; 10.0, 128.0.3 &gt; 115.12, 24.08 &lt; 24.9 ? non : 08 = 8.</summary>
    public static int CompareVersions(string a, string b)
    {
        var x = Segment.Matches(a).Select(m => m.Value).ToList();
        var y = Segment.Matches(b).Select(m => m.Value).ToList();
        for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            if (i >= x.Count) return -1;
            if (i >= y.Count) return 1;
            int c = long.TryParse(x[i], out var nx) && long.TryParse(y[i], out var ny)
                ? nx.CompareTo(ny)
                : string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }

    private static readonly Regex Year = new(@"\b(19|20)\d{2}\b");

    /// <summary>
    /// Version testée : si le critère est une année (« office &lt; 2019 ») et que le nom du logiciel en contient une,
    /// on compare l'année du produit (Office 2016 a la version 16.0… comme Office 2019).
    /// </summary>
    public static bool VersionMatches(string name, string version, VersionOp op, string? wanted)
    {
        if (op == VersionOp.Aucun || string.IsNullOrWhiteSpace(wanted)) return true;
        var subject = version;
        if (Year.IsMatch(wanted.Trim()) && wanted.Trim().Length == 4 && Year.Match(name) is { Success: true } y) subject = y.Value;
        if (subject.Length == 0) return false;
        if (op == VersionOp.CommencePar) return subject.StartsWith(wanted.Trim(), StringComparison.OrdinalIgnoreCase);
        int c = CompareVersions(subject, wanted);
        return op switch
        {
            VersionOp.Egal => c == 0, VersionOp.Inferieur => c < 0, VersionOp.InferieurOuEgal => c <= 0,
            VersionOp.Superieur => c > 0, VersionOp.SuperieurOuEgal => c >= 0, _ => true,
        };
    }
}

/// <summary>Parcourt toutes les fiches d'inventaire GLPI indexées (sur ce poste, sans réseau) et construit le tableau.</summary>
public static class InventoryService
{
    public static InventoryResult Run(IndexStore store, InventoryFilter f)
    {
        var rows = new List<InventoryRow>();
        var assets = new HashSet<string>();
        int scanned = 0;
        bool hasSoftware = false;
        foreach (var it in store.GetByIdPrefix("glpi:"))
        {
            if (it.GetExtra("inv") != "1") continue;
            scanned++;
            var os = it.GetExtra("os") ?? "";
            var ips = it.GetExtra("ip") ?? "";
            var lieu = it.GetExtra("lieu") ?? "";
            var type = it.GetExtra("glpiType") ?? "";
            var serial = it.GetExtra("serial") ?? "";
            if (!InventoryQuery.AllWords(os, f.Os)) continue;
            if (f.Ip is not null && !ips.Split(',').Any(ip => ip.Trim().StartsWith(f.Ip.Trim(), StringComparison.OrdinalIgnoreCase) || ip.Contains(f.Ip.Trim()))) continue;
            if (!InventoryQuery.AllWords(it.Title, f.Asset)) continue;
            if (!InventoryQuery.AllWords(lieu, f.Location)) continue;
            if (!InventoryQuery.AllWords(it.Author, f.User)) continue;

            var sw = (it.Extra["sw"] as JsonArray ?? new JsonArray()).OfType<JsonArray>()
                .Select(p => (Name: p.Count > 0 ? p[0]?.ToString() ?? "" : "", Version: p.Count > 1 ? p[1]?.ToString() ?? "" : ""))
                .ToList();
            if (sw.Count > 0) hasSoftware = true;
            InventoryRow Row(string s, string v) => new(it.Id, it.Title, type, s, v, os, ips, it.Author, lieu, serial, it.OpenUri, it.Date);

            if (f.Software is null)
            {
                rows.Add(Row("", ""));
                assets.Add(it.Id);
                continue;
            }
            var matches = sw.Where(p => InventoryQuery.AllWords(p.Name + " " + p.Version, f.Software)).ToList();
            if (f.WithoutSoftware)
            {
                if (matches.Count == 0 && sw.Count > 0) { rows.Add(Row("", "")); assets.Add(it.Id); }
                continue;
            }
            foreach (var m in matches.Where(m => InventoryQuery.VersionMatches(m.Name, m.Version, f.VersionOp, f.Version)))
            {
                rows.Add(Row(m.Name, m.Version));
                assets.Add(it.Id);
            }
        }
        rows.Sort((a, b) =>
        {
            int c = string.Compare(a.Asset, b.Asset, StringComparison.CurrentCultureIgnoreCase);
            return c != 0 ? c : string.Compare(a.Software, b.Software, StringComparison.CurrentCultureIgnoreCase);
        });
        return new InventoryResult(f, rows, assets.Count, scanned, hasSoftware);
    }

    /// <summary>CSV pour Excel en français : point-virgule, UTF-8 avec BOM (écrit par l'appelant).</summary>
    public static string ToCsv(IEnumerable<InventoryRow> rows)
    {
        static string Q(string s) => s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.Append("Poste;Type;Logiciel;Version;Système;Adresses IP;Utilisateur;Lieu;N° de série;Fiche GLPI\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(";", new[] { r.Asset, r.AssetType, r.Software, r.Version, r.Os, r.Ips, r.User, r.Location, r.Serial, r.OpenUri }.Select(Q))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Texte tabulé (copier-coller dans Excel ou un mail).</summary>
    public static string ToTabs(IEnumerable<InventoryRow> rows)
    {
        static string C(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        var sb = new StringBuilder("Poste\tType\tLogiciel\tVersion\tSystème\tAdresses IP\tUtilisateur\tLieu\tN° de série\r\n");
        foreach (var r in rows)
            sb.Append(string.Join("\t", new[] { r.Asset, r.AssetType, r.Software, r.Version, r.Os, r.Ips, r.User, r.Location, r.Serial }.Select(C))).Append("\r\n");
        return sb.ToString();
    }
}

/// <summary>
/// Claude ne sert qu'à comprendre la question (logiciel, version, système…) : seule la question est envoyée,
/// jamais l'inventaire. Le tableau est ensuite calculé localement.
/// </summary>
public sealed class ClaudeInventoryPlanner(ClaudeClient client, Func<ClaudeSettings> settings)
{
    private static readonly string[] Ops = { "aucun", "egal", "commence_par", "inferieur", "inferieur_ou_egal", "superieur", "superieur_ou_egal" };

    public async Task<InventoryFilter> PlanAsync(string question, CancellationToken ct)
    {
        JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
        var body = new JsonObject
        {
            ["model"] = settings().AnalysisModel,
            ["max_tokens"] = 300,
            ["system"] = """
                Tu traduis une question sur l'inventaire informatique (GLPI) d'une entreprise française en critères de filtre.
                Chaque poste a : un nom, un système d'exploitation, des adresses IP, un utilisateur, un lieu et la liste de ses logiciels installés (nom + version).
                Donne le nom du logiciel tel qu'il apparaît probablement dans un inventaire (ex. « Mozilla Firefox » → « firefox », « Word 2016 » → « office 2016 »).
                Pour « antérieure à », « plus ancienne que » utilise inferieur ; pour une édition d'Office ou de Windows désignée par une année, mets l'année comme version.
                Laisse vide tout critère que la question ne demande pas. Utilise toujours l'outil « inventaire ».
                """,
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "inventaire",
                ["description"] = "Filtre l'inventaire local des postes.",
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["logiciel"] = Str("Nom (ou mots du nom) du logiciel recherché, sinon vide."),
                        ["sans_logiciel"] = new JsonObject { ["type"] = "boolean", ["description"] = "Vrai si l'on cherche les postes qui N'ONT PAS ce logiciel." },
                        ["version_operateur"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Ops.Select(o => (JsonNode)o!).ToArray()) },
                        ["version"] = Str("Version de référence, sinon vide."),
                        ["systeme"] = Str("Mots du système d'exploitation (ex. « windows 10 », « server 2019 »), sinon vide."),
                        ["ip"] = Str("Début d'adresse IP ou réseau (ex. « 10.12. »), sinon vide."),
                        ["poste"] = Str("Mots du nom du poste, sinon vide."),
                        ["lieu"] = Str("Lieu, site ou étage, sinon vide."),
                        ["utilisateur"] = Str("Utilisateur du poste, sinon vide."),
                    },
                    ["required"] = new JsonArray("logiciel", "version_operateur"),
                },
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "inventaire" },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = question }),
        };
        return Parse(await client.CreateAsync(body, ct));
    }

    internal static InventoryFilter Parse(JsonObject resp)
    {
        var input = (resp["content"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(b => b["type"]?.GetValue<string>() == "tool_use")?["input"] as JsonObject;
        string? S(string k) => input?[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
        var f = new InventoryFilter
        {
            Software = S("logiciel"), Version = S("version"), Os = S("systeme"), Ip = S("ip"),
            Asset = S("poste"), Location = S("lieu"), User = S("utilisateur"),
            WithoutSoftware = input?["sans_logiciel"] is JsonValue b && b.TryGetValue<bool>(out var w) && w,
            VersionOp = S("version_operateur") switch
            {
                "egal" => VersionOp.Egal, "commence_par" => VersionOp.CommencePar, "inferieur" => VersionOp.Inferieur,
                "inferieur_ou_egal" => VersionOp.InferieurOuEgal, "superieur" => VersionOp.Superieur,
                "superieur_ou_egal" => VersionOp.SuperieurOuEgal, _ => VersionOp.Aucun,
            },
        };
        if (f.Software is null) f.WithoutSoftware = false;
        if (f.Version is null) f.VersionOp = VersionOp.Aucun;
        else if (f.VersionOp == VersionOp.Aucun) f.VersionOp = VersionOp.CommencePar;
        return f;
    }
}
