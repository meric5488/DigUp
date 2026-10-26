using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fouineur.Core.Model;

/// <summary>Catégorie de résultat, telle qu'affichée dans les onglets de recherche.</summary>
public enum ItemKind
{
    Mail = 1,
    Teams = 2,
    SharePoint = 3,
    OneDrive = 4,
    Fichier = 5,
}

public static class ItemKindNames
{
    public static string Label(ItemKind k) => k switch
    {
        ItemKind.Mail => "Mails",
        ItemKind.Teams => "Teams",
        ItemKind.SharePoint => "SharePoint",
        ItemKind.OneDrive => "OneDrive",
        ItemKind.Fichier => "Fichiers",
        _ => k.ToString(),
    };

    public static readonly ItemKind[] All =
        { ItemKind.Mail, ItemKind.Teams, ItemKind.SharePoint, ItemKind.OneDrive, ItemKind.Fichier };
}

/// <summary>
/// Un élément indexé : fichier, mail, fil Teams, conversation d'une journée…
/// </summary>
public sealed class IndexedItem
{
    /// <summary>Identifiant global, unique dans l'index (préfixé par la source).</summary>
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required ItemKind Kind { get; init; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    /// <summary>Emplacement lisible : « Site › Bibliothèque › Dossier », « Compte › Boîte de réception »…</summary>
    public string Location { get; set; } = "";
    /// <summary>Lien ou chemin d'origine (chemin local/UNC, webUrl, lien Teams…). Peut être vide (mail Zimbra : résolu à l'ouverture).</summary>
    public string OpenUri { get; set; } = "";
    /// <summary>Extension en minuscules, avec le point (".docx"), ou ".eml" / ".teams".</summary>
    public string Extension { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Chaîne des conteneurs depuis la racine de la source (sert aux règles de sélection).</summary>
    public IReadOnlyList<string> Ancestors { get; set; } = Array.Empty<string>();
    /// <summary>Empreinte de version (mtime+taille, cTag, rev…) : évite de retraiter un élément inchangé.</summary>
    public string Version { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Données propres au connecteur (driveId, itemId, webUrl du dossier parent…).</summary>
    public JsonObject Extra { get; set; } = new();

    public string? GetExtra(string key) => Extra.TryGetPropertyValue(key, out var v) && v is not null ? v.ToString() : null;

    public IndexedItem SetExtra(string key, string? value)
    {
        if (value is null) Extra.Remove(key); else Extra[key] = value;
        return this;
    }

    internal static string AncestorsToColumn(IReadOnlyList<string> a) =>
        a.Count == 0 ? "|" : "|" + string.Join('|', a.Select(x => x.Replace("|", "/"))) + "|";

    internal static IReadOnlyList<string> AncestorsFromColumn(string? s) =>
        string.IsNullOrEmpty(s) || s == "|" ? Array.Empty<string>() : s.Trim('|').Split('|');

    internal string ExtraJson => Extra.ToJsonString();

    internal static JsonObject ParseExtra(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new JsonObject();
        try { return JsonNode.Parse(s) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}

/// <summary>Résultat de recherche : l'élément, sans le corps complet, plus un extrait surligné.</summary>
public sealed class SearchHit
{
    public required IndexedItem Item { get; init; }
    /// <summary>Extrait ; les termes trouvés sont encadrés par \u0002 … \u0003.</summary>
    public string Snippet { get; init; } = "";
    public double Score { get; init; }
    public bool MatchedText { get; init; }
    public bool MatchedMeaning { get; init; }

    public const char HighlightStart = '\u0002';
    public const char HighlightEnd = '\u0003';
}

public enum DateFilter { Tout, SeptJours, TrenteJours, UnAn }

public sealed class SearchRequest
{
    public string Text { get; init; } = "";
    /// <summary>Catégorie affichée ; null = toutes.</summary>
    public ItemKind? Kind { get; init; }
    public DateFilter Date { get; init; } = DateFilter.Tout;
    public string? SourceId { get; init; }
    public int Limit { get; init; } = 200;
    /// <summary>Utiliser la recherche par le sens (vecteurs) en plus du texte.</summary>
    public bool Semantic { get; init; } = true;
}

public sealed class SearchResponse
{
    public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();
    /// <summary>Nombre de résultats par catégorie, avant filtrage par catégorie.</summary>
    public IReadOnlyDictionary<ItemKind, int> CountsByKind { get; init; } = new Dictionary<ItemKind, int>();
    public int Total => CountsByKind.Values.Sum();
    public TimeSpan Elapsed { get; init; }
    public bool SemanticUsed { get; init; }
}
