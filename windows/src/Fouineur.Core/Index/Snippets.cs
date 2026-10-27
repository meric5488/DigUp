using System.Globalization;
using System.Text;
using Fouineur.Core.Model;

namespace Fouineur.Core.Index;

/// <summary>
/// Extraits de résultats : une fenêtre de texte autour du premier mot trouvé, mots surlignés
/// (\u0002 … \u0003). Comparaison sans casse ni accents, mots trouvés par leur début.
/// </summary>
public static class Snippets
{
    private static readonly CompareInfo Cmp = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static string Make(string text, IReadOnlyList<string> terms, int width = 240)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int first = -1;
        foreach (var t in terms)
        {
            int i = FindWord(text, t, 0, out _);
            if (i >= 0 && (first < 0 || i < first)) first = i;
        }
        if (first < 0) return "";
        int start = Math.Max(0, first - width / 4);
        return Window(text, start, width, terms);
    }

    public static string Window(string text, int start, int width, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || start >= text.Length) return "";
        start = Math.Max(0, start);
        // caler sur un début de mot
        if (start > 0) { while (start < text.Length && start > 0 && !char.IsWhiteSpace(text[start - 1])) start++; }
        int end = Math.Min(text.Length, start + width);
        if (end < text.Length) { int sp = text.LastIndexOf(' ', end - 1, Math.Min(40, end - start)); if (sp > start) end = sp; }
        var slice = Collapse(text.AsSpan(start, end - start));
        var marked = Mark(slice, terms);
        return (start > 0 ? "…" : "") + marked + (end < text.Length ? "…" : "");
    }

    /// <summary>Position d'un mot commençant par <paramref name="term"/> (début de mot obligatoire).</summary>
    private static int FindWord(string text, string term, int from, out int length)
    {
        length = 0;
        while (from < text.Length)
        {
            int i = Cmp.IndexOf(text.AsSpan(from), term.AsSpan(), Opts, out int len);
            if (i < 0) return -1;
            i += from;
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
            {
                int end = i + len;
                while (end < text.Length && char.IsLetterOrDigit(text[end])) end++;
                length = end - i;
                return i;
            }
            from = i + Math.Max(1, len);
        }
        return -1;
    }

    private static string Mark(string s, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        int pos = 0;
        while (pos < s.Length)
        {
            int best = -1, bestLen = 0;
            foreach (var t in terms)
            {
                int i = FindWord(s, t, pos, out int len);
                if (i >= 0 && (best < 0 || i < best)) { best = i; bestLen = len; }
            }
            if (best < 0) { sb.Append(s, pos, s.Length - pos); break; }
            sb.Append(s, pos, best - pos).Append(SearchHit.HighlightStart).Append(s, best, bestLen).Append(SearchHit.HighlightEnd);
            pos = best + bestLen;
        }
        return sb.ToString();
    }

    private static string Collapse(ReadOnlySpan<char> s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; }
            else { sb.Append(ch); space = false; }
        }
        return sb.ToString().Trim();
    }
}
