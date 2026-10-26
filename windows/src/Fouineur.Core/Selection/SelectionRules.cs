using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Fouineur.Core.Selection;

public enum CheckState { Unchecked, Checked, Indeterminate }

/// <summary>Une règle posée sur un nœud (site, bibliothèque, dossier, équipe, canal, dossier mail…).</summary>
public sealed class SelectionRule
{
    public bool Include { get; set; }
    /// <summary>Chaîne des ancêtres du nœud au moment où la règle a été posée (racine en premier).</summary>
    public List<string> Ancestors { get; set; } = new();
    /// <summary>Libellé lisible (pour le récapitulatif de la sélection).</summary>
    public string Label { get; set; } = "";
}

/// <summary>
/// Règles inclure/exclure sur des identifiants de nœuds. La règle la plus proche gagne ;
/// les nouveaux éléments héritent de la règle de leur conteneur.
/// </summary>
public sealed class SelectionRules
{
    public Dictionary<string, SelectionRule> Rules { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Valeur si aucune règle ne s'applique.</summary>
    public bool DefaultIncluded { get; set; }

    /// <param name="chain">Chaîne [racine … nœud] (le nœud lui-même en dernier).</param>
    public bool IsIncluded(IReadOnlyList<string> chain)
    {
        for (int i = chain.Count - 1; i >= 0; i--)
            if (Rules.TryGetValue(chain[i], out var r)) return r.Include;
        return DefaultIncluded;
    }

    public bool IsIncluded(IReadOnlyList<string> ancestors, string nodeId)
    {
        if (Rules.TryGetValue(nodeId, out var r)) return r.Include;
        return IsIncluded(ancestors);
    }

    /// <summary>Le nœud ou au moins un de ses descendants est-il inclus ? (faut-il descendre dedans pendant la synchro)</summary>
    public bool AnyIncludedUnder(IReadOnlyList<string> ancestors, string nodeId)
    {
        if (IsIncluded(ancestors, nodeId)) return true;
        return Rules.Any(kv => kv.Value.Include && kv.Value.Ancestors.Contains(nodeId));
    }

    /// <summary>Règles d'inclusion posées sous le nœud (descendants seulement).</summary>
    public IEnumerable<(string NodeId, SelectionRule Rule)> IncludedDescendants(string nodeId) =>
        Rules.Where(kv => kv.Value.Include && kv.Value.Ancestors.Contains(nodeId)).Select(kv => (kv.Key, kv.Value));

    public CheckState GetState(IReadOnlyList<string> ancestors, string nodeId)
    {
        bool me = IsIncluded(ancestors, nodeId);
        bool differs = Rules.Any(kv => kv.Value.Include != me && kv.Value.Ancestors.Contains(nodeId));
        if (differs) return CheckState.Indeterminate;
        return me ? CheckState.Checked : CheckState.Unchecked;
    }

    /// <summary>Coche ou décoche un nœud : toute la sous-arborescence prend cette valeur (y compris les futurs éléments).</summary>
    public void Set(IReadOnlyList<string> ancestors, string nodeId, bool include, string label = "")
    {
        foreach (var k in Rules.Where(kv => kv.Value.Ancestors.Contains(nodeId)).Select(kv => kv.Key).ToList())
            Rules.Remove(k);
        Rules.Remove(nodeId);
        if (IsIncluded(ancestors) != include)
            Rules[nodeId] = new SelectionRule { Include = include, Ancestors = ancestors.ToList(), Label = label };
    }

    /// <summary>Empreinte des règles concernant un sous-arbre : sert à savoir s'il faut relire un périmètre.</summary>
    public string Fingerprint(string? underNodeId = null)
    {
        var relevant = Rules
            .Where(kv => underNodeId is null || kv.Key == underNodeId || kv.Value.Ancestors.Contains(underNodeId))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Value.Include ? "+" : "-") + kv.Key);
        var s = (DefaultIncluded ? "D+" : "D-") + "\n" + string.Join("\n", relevant);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }

    /// <summary>Empreinte des règles qui touchent un nœud : celles de sa chaîne d'ancêtres et de ses descendants.</summary>
    public string Fingerprint(IReadOnlyList<string> chain)
    {
        if (chain.Count == 0) return Fingerprint();
        var node = chain[^1];
        var relevant = Rules
            .Where(kv => chain.Contains(kv.Key) || kv.Value.Ancestors.Contains(node))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Value.Include ? "+" : "-") + kv.Key);
        var s = (DefaultIncluded ? "D+" : "D-") + "\n" + string.Join("\n", relevant);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }

    [JsonIgnore]
    public bool IsEmpty => Rules.Count == 0 && !DefaultIncluded;

    public SelectionRules Clone() => new()
    {
        DefaultIncluded = DefaultIncluded,
        Rules = Rules.ToDictionary(kv => kv.Key, kv => new SelectionRule
        {
            Include = kv.Value.Include, Ancestors = kv.Value.Ancestors.ToList(), Label = kv.Value.Label,
        }, StringComparer.Ordinal),
    };
}
