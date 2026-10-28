using System.Text;

namespace Fouineur.Core.Index;

/// <summary>
/// Requête utilisateur traduite pour FTS5.
/// Syntaxe : mots (préfixes), "expression exacte", -exclu, de:auteur, titre:mot, type:pdf.
/// </summary>
public sealed class ParsedQuery
{
    public string FtsQuery { get; init; } = "";
    /// <summary>Texte « positif » (sans opérateurs) pour la recherche par le sens.</summary>
    public string SemanticText { get; init; } = "";
    public List<string> Extensions { get; init; } = new();
    /// <summary>Mots positifs de la requête (pour surligner les extraits).</summary>
    public List<string> Terms { get; init; } = new();
    public bool HasFieldFilters { get; init; }
    public bool IsEmpty => FtsQuery.Length == 0 && SemanticText.Length == 0;
}

public static class QueryParser
{
    public static ParsedQuery Parse(string? input)
    {
        input ??= "";
        var positives = new List<string>();
        var negatives = new List<string>();
        var semantic = new List<string>();
        var exts = new List<string>();
        var terms = new List<string>();
        bool fieldFilters = false;

        bool orNext = false;
        foreach (var (raw, quoted) in Tokenize(input))
        {
            string tok = raw;
            if (!quoted && (tok == "OR" || tok == "OU") && positives.Count > 0) { orNext = true; continue; }
            bool neg = false;
            if (!quoted && tok.StartsWith('-') && tok.Length > 1) { neg = true; tok = tok[1..]; }

            string? column = null;
            if (!quoted)
            {
                int colon = tok.IndexOf(':');
                if (colon > 0)
                {
                    var key = tok[..colon].ToLowerInvariant();
                    var val = tok[(colon + 1)..];
                    switch (key)
                    {
                        case "type" or "ext":
                            foreach (var e in val.Split(',', StringSplitOptions.RemoveEmptyEntries))
                                exts.Add("." + e.TrimStart('.').ToLowerInvariant());
                            continue;
                        case "de" or "from" or "auteur":
                            column = "author"; tok = val; fieldFilters = true; break;
                        case "titre" or "title" or "sujet":
                            column = "title"; tok = val; fieldFilters = true; break;
                        case "dans" or "in":
                            column = "location"; tok = val; fieldFilters = true; break;
                    }
                }
            }

            var words = Words(tok);
            if (words.Count == 0) continue;
            string expr = quoted
                ? "\"" + string.Join(' ', words) + "\""
                : string.Join(" ", words.Select(w => "\"" + w + "\"*"));
            if (column is not null) expr = $"{column} : ({expr})";

            if (neg) negatives.Add(expr);
            else
            {
                terms.AddRange(words);
                var p = words.Count > 1 && !quoted ? "(" + expr + ")" : expr;
                if (orNext) positives[^1] = $"({positives[^1]} OR {p})";
                else positives.Add(p);
                orNext = false;
                if (column is null) semantic.Add(string.Join(' ', words));
            }
        }

        string fts = "";
        if (positives.Count > 0)
        {
            fts = string.Join(" AND ", positives);
            foreach (var n in negatives) fts = $"({fts}) NOT ({n})";
        }

        return new ParsedQuery
        {
            FtsQuery = fts,
            SemanticText = string.Join(' ', semantic),
            Extensions = exts,
            Terms = terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            HasFieldFilters = fieldFilters,
        };
    }

    private static IEnumerable<(string Token, bool Quoted)> Tokenize(string s)
    {
        var sb = new StringBuilder();
        bool inQuote = false;
        foreach (var ch in s)
        {
            if (ch == '"' || ch == '«' || ch == '»' || ch == '“' || ch == '”')
            {
                if (sb.Length > 0) { yield return (sb.ToString(), inQuote); sb.Clear(); }
                inQuote = !inQuote;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (sb.Length > 0) { yield return (sb.ToString(), false); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return (sb.ToString(), inQuote);
    }

    /// <summary>Découpe comme le tokeniseur unicode61 : lettres et chiffres seulement.</summary>
    public static List<string> Words(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}
