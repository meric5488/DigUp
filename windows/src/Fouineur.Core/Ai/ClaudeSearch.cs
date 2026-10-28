using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Fouineur.Core.Configuration;
using Fouineur.Core.Index;
using Fouineur.Core.Model;
using Fouineur.Core.Sync;

namespace Fouineur.Core.Ai;

/// <summary>Ce que Claude a compris de la question.</summary>
public sealed record SearchPlan(string Keywords, ItemKind? Kind, DateFilter Date, string? Author, string Summary)
{
    public string Describe()
    {
        var parts = new List<string>();
        if (Kind is { } k) parts.Add(ItemKindNames.Label(k));
        if (Date != DateFilter.Tout) parts.Add(Date switch
        {
            DateFilter.SeptJours => "7 derniers jours",
            DateFilter.TrenteJours => "30 derniers jours",
            _ => "dernière année",
        });
        if (!string.IsNullOrWhiteSpace(Author)) parts.Add("de " + Author);
        parts.Add($"« {Keywords} »");
        return string.Join(" · ", parts);
    }
}

/// <summary>Documents retenus pour la réponse, numérotés [1], [2]… dans l'ordre de la liste.</summary>
public sealed class ClaudeContext
{
    public required string Question { get; init; }
    public required SearchPlan Plan { get; init; }
    public List<SearchHit> Sources { get; } = new();
    public List<string> Passages { get; } = new();
    /// <summary>Résultats écartés parce que leur source n'autorise pas l'envoi à Claude.</summary>
    public int Excluded { get; set; }
    public int CharsSent => Passages.Sum(p => p.Length);
}

/// <summary>
/// Recherche assistée par Claude, en trois temps :
/// 1) un modèle rapide traduit la question en mots-clés et filtres ;
/// 2) Fouineur cherche localement (texte + sens) ;
/// 3) seuls les extraits des meilleurs documents sont envoyés à Claude, qui répond en citant [n].
/// </summary>
public sealed class ClaudeSearchService
{
    private readonly ClaudeClient _client;
    private readonly SearchService _search;
    private readonly IndexStore _store;
    private readonly Func<ClaudeSettings> _settings;
    private readonly Func<string, bool> _sourceAllowed;

    public ClaudeSearchService(ClaudeClient client, SearchService search, IndexStore store, Func<ClaudeSettings> settings, Func<string, bool> sourceAllowed)
    {
        _client = client; _search = search; _store = store; _settings = settings; _sourceAllowed = sourceAllowed;
    }

    public bool IsConfigured => _client.HasKey;

    // ─── 1. Analyse de la question ───

    private static readonly string[] Categories = { "tout", "mails", "teams", "sharepoint", "onedrive", "fichiers", "glpi" };
    private static readonly string[] Periods = { "tout", "7j", "30j", "1an" };

    public async Task<SearchPlan> PlanAsync(string question, CancellationToken ct)
    {
        var today = DateTime.Now.ToString("dddd d MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
        var body = new JsonObject
        {
            ["model"] = _settings().AnalysisModel,
            ["max_tokens"] = 400,
            ["system"] = $"""
                Tu prépares une recherche dans un index local de mails, conversations Teams, documents SharePoint/OneDrive, fichiers et GLPI (tickets, problèmes, changements, base de connaissances, inventaire du parc) d'une entreprise française.
                Nous sommes le {today}. Traduis la question en mots-clés de recherche efficaces (les mots probables dans les documents, en français,
                sans mots vides, avec au besoin un synonyme utile) et en filtres. N'invente pas de filtre que la question ne demande pas.
                Utilise toujours l'outil « rechercher ».
                """,
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "rechercher",
                ["description"] = "Lance la recherche dans l'index local.",
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["mots_cles"] = new JsonObject { ["type"] = "string", ["description"] = "3 à 8 mots-clés séparés par des espaces." },
                        ["categorie"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Categories.Select(c => (JsonNode)c!).ToArray()) },
                        ["periode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Periods.Select(c => (JsonNode)c!).ToArray()) },
                        ["auteur"] = new JsonObject { ["type"] = "string", ["description"] = "Expéditeur ou auteur si la question en nomme un, sinon vide." },
                        ["resume"] = new JsonObject { ["type"] = "string", ["description"] = "La question reformulée en une phrase courte." },
                    },
                    ["required"] = new JsonArray("mots_cles", "categorie", "periode"),
                },
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "rechercher" },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = question }),
        };
        var resp = await _client.CreateAsync(body, ct);
        return ParsePlan(resp, question);
    }

    internal static SearchPlan ParsePlan(JsonObject resp, string question)
    {
        var input = (resp["content"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(b => b["type"]?.GetValue<string>() == "tool_use")?["input"];
        string S(string k) => input?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var keywords = S("mots_cles");
        if (keywords.Length == 0) keywords = question;
        ItemKind? kind = S("categorie") switch
        {
            "mails" => ItemKind.Mail, "teams" => ItemKind.Teams, "sharepoint" => ItemKind.SharePoint,
            "onedrive" => ItemKind.OneDrive, "fichiers" => ItemKind.Fichier, "glpi" => ItemKind.Glpi, _ => null,
        };
        var date = S("periode") switch
        {
            "7j" => DateFilter.SeptJours, "30j" => DateFilter.TrenteJours, "1an" => DateFilter.UnAn, _ => DateFilter.Tout,
        };
        var author = S("auteur");
        return new SearchPlan(keywords, kind, date, author.Length == 0 ? null : author, S("resume"));
    }

    // ─── 2. Recherche locale et extraits ───

    public async Task<ClaudeContext> RetrieveAsync(string question, SearchPlan plan, ItemKind? forcedKind, CancellationToken ct)
    {
        var settings = _settings();
        var ctx = new ClaudeContext { Question = question, Plan = plan };
        var kind = forcedKind ?? plan.Kind;

        // Essais du plus précis au plus large, jusqu'à avoir assez de documents.
        var attempts = new List<SearchRequest>();
        string authorFilter = string.IsNullOrWhiteSpace(plan.Author) ? "" :
            " " + string.Join(' ', QueryParser.Words(plan.Author).Select(w => "de:" + w));
        if (authorFilter.Length > 0)
            attempts.Add(new SearchRequest { Text = plan.Keywords + authorFilter, Kind = kind, Date = plan.Date, Limit = 60 });
        attempts.Add(new SearchRequest { Text = plan.Keywords, Kind = kind, Date = plan.Date, Limit = 60 });
        attempts.Add(new SearchRequest { Text = AnyOf(plan.Keywords), Kind = kind, Date = DateFilter.Tout, Limit = 60 });
        attempts.Add(new SearchRequest { Text = AnyOf(question), Kind = forcedKind, Limit = 60 });

        var seen = new HashSet<string>();
        var terms = QueryParser.Words(plan.Keywords + " " + (plan.Author ?? ""));
        foreach (var req in attempts)
        {
            if (ctx.Sources.Count >= settings.MaxDocuments) break;
            var resp = await _search.SearchAsync(req, ct);
            foreach (var hit in resp.Hits)
            {
                if (ctx.Sources.Count >= settings.MaxDocuments) break;
                if (!seen.Add(hit.Item.Id)) continue;
                if (!_sourceAllowed(hit.Item.SourceId)) { ctx.Excluded++; continue; }
                var full = _store.Get(hit.Item.Id) ?? hit.Item;
                ctx.Sources.Add(hit);
                ctx.Passages.Add(Snippets.Passage(full.Title + "\n" + full.Body, terms, Math.Clamp(settings.PassageChars, 300, 6000)));
            }
        }
        return ctx;
    }

    /// <summary>« a b c » → « a OR b OR c » (au moins un des mots), pour élargir quand la recherche stricte ne trouve rien.</summary>
    private static string AnyOf(string text)
    {
        var words = QueryParser.Words(text).Where(w => w.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        return words.Count == 0 ? text : string.Join(" OR ", words);
    }

    // ─── 3. Réponse ───

    internal static JsonObject BuildAnswerRequest(ClaudeContext ctx, string model)
    {
        var sb = new StringBuilder();
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        for (int i = 0; i < ctx.Sources.Count; i++)
        {
            var it = ctx.Sources[i].Item;
            sb.Append($"<document numero=\"{i + 1}\" type=\"{ItemKindNames.Label(it.Kind)}\" titre=\"{Esc(it.Title)}\"");
            if (it.Author.Length > 0) sb.Append($" auteur=\"{Esc(it.Author)}\"");
            sb.Append($" date=\"{it.Date.ToLocalTime().ToString("d MMMM yyyy HH:mm", fr)}\" emplacement=\"{Esc(it.Location)}\">\n");
            sb.Append(ctx.Passages[i]).Append("\n</document>\n");
        }
        sb.Append("\nQuestion : ").Append(ctx.Question);

        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 1500,
            ["system"] = $"""
                Tu es l'assistant de recherche de Fouineur, un moteur de recherche interne (mails, Teams, SharePoint, OneDrive, fichiers, GLPI).
                Nous sommes le {DateTime.Now.ToString("dddd d MMMM yyyy", fr)}.
                Réponds en français, de façon concise et directe, uniquement à partir des documents fournis.
                Après chaque information, cite le ou les documents sous la forme [1], [2]… (le numéro du document).
                Si les documents ne permettent pas de répondre, dis-le clairement et indique ceux qui s'en rapprochent le plus.
                Texte simple : pas de titres Markdown ni de tableaux ; des tirets pour les listes si nécessaire.
                """,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = sb.ToString() }),
        };
    }

    private static string Esc(string s) => s.Replace("\"", "'").Replace("\n", " ");

    public IAsyncEnumerable<string> AnswerAsync(ClaudeContext ctx, CancellationToken ct)
    {
        if (ctx.Sources.Count == 0) return NoResults();
        return _client.StreamTextAsync(BuildAnswerRequest(ctx, _settings().AnswerModel), ct);
    }

    private static async IAsyncEnumerable<string> NoResults([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return "Aucun document de l'index ne correspond à cette question. Essayez d'autres mots, ou vérifiez que la source concernée est synchronisée et autorisée pour Claude.";
    }

    /// <summary>Petit appel de vérification de la clé et du modèle.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        var resp = await _client.CreateAsync(new JsonObject
        {
            ["model"] = _settings().AnswerModel,
            ["max_tokens"] = 20,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Réponds simplement : OK" }),
        }, ct);
        return (resp["content"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(b => b["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>() ?? "";
    }
}
