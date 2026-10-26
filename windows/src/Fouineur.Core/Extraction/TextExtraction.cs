using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Fouineur.Core.Extraction;

public interface ITextExtractor
{
    IEnumerable<string> Extensions { get; }
    /// <summary>Extrait le texte ; le flux est positionnable (copié en mémoire si besoin).</summary>
    string Extract(Stream stream, string extension, CancellationToken ct);
}

/// <summary>Registre des extracteurs, par extension. L'application Windows y ajoute PDF.</summary>
public sealed class TextExtractors
{
    private readonly Dictionary<string, ITextExtractor> _map = new(StringComparer.OrdinalIgnoreCase);
    public int MaxChars { get; set; } = 1_000_000;
    public long MaxBytes { get; set; } = 60L * 1024 * 1024;

    public static TextExtractors CreateDefault()
    {
        var r = new TextExtractors();
        r.Register(new PlainTextExtractor());
        r.Register(new HtmlExtractor());
        r.Register(new OpenXmlExtractor());
        r.Register(new OpenDocumentExtractor());
        r.Register(new RtfExtractor());
        r.Register(new EmlExtractor());
        return r;
    }

    public void Register(ITextExtractor e)
    {
        foreach (var x in e.Extensions) _map[x] = e;
    }

    public bool CanExtract(string extension) => _map.ContainsKey(extension);

    public IReadOnlyCollection<string> SupportedExtensions => _map.Keys;

    public string Extract(Stream s, string extension, CancellationToken ct = default)
    {
        if (!_map.TryGetValue(extension, out var e)) return "";
        Stream input = s;
        if (!s.CanSeek)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            input = ms;
        }
        var text = e.Extract(input, extension, ct);
        text = Normalize(text);
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    public string ExtractFile(string path, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!CanExtract(ext)) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        return Extract(fs, ext, ct);
    }

    internal static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        int nl = 0; bool sp = false;
        foreach (var ch0 in s)
        {
            var ch = ch0 == ' ' ? ' ' : ch0;
            if (ch == '\r') continue;
            if (ch == '\n') { nl++; sp = false; continue; }
            if (char.IsWhiteSpace(ch)) { sp = true; continue; }
            if (char.IsControl(ch)) continue;
            if (nl > 0) { if (sb.Length > 0) sb.Append(nl > 1 ? "\n\n" : "\n"); nl = 0; sp = false; }
            else if (sp) { if (sb.Length > 0) sb.Append(' '); sp = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

internal static class TextDecoding
{
    public static string ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Decode(ms.ToArray());
    }

    /// <summary>UTF-8 si valide (avec ou sans BOM), UTF-16 si BOM, sinon Windows-1252.</summary>
    public static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        int off = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(b, off, b.Length - off); }
        catch (DecoderFallbackException) { return Latin1Ish(b); }
    }

    private static string Latin1Ish(byte[] b)
    {
        // Windows-1252 sans dépendre de CodePagesEncodingProvider : Latin-1 + table 0x80–0x9F.
        const string cp1252 = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";
        var sb = new StringBuilder(b.Length);
        foreach (var x in b) sb.Append(x is >= 0x80 and <= 0x9F ? cp1252[x - 0x80] : (char)x);
        return sb.ToString();
    }
}

public sealed class PlainTextExtractor : ITextExtractor
{
    public IEnumerable<string> Extensions => new[]
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml",
        ".cs", ".vb", ".fs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".go", ".rs", ".c", ".h", ".cpp", ".hpp",
        ".php", ".rb", ".swift", ".sql", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".css", ".scss", ".vue", ".svelte",
        ".properties", ".env", ".tex", ".srt", ".vtt", ".reg",
    };

    public string Extract(Stream s, string ext, CancellationToken ct) => TextDecoding.ReadAll(s);
}

public sealed class HtmlExtractor : ITextExtractor
{
    public IEnumerable<string> Extensions => new[] { ".html", ".htm", ".xhtml", ".mht", ".mhtml" };
    public string Extract(Stream s, string ext, CancellationToken ct) => HtmlText.ToText(TextDecoding.ReadAll(s));
}

/// <summary>Conversion HTML → texte simple (corps de mails, messages Teams, pages).</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Blocks();
    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/tr|/h[1-6]|/blockquote|/table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();
    [GeneratedRegex(@"<\s*(td|th)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Cells();
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex Tags();

    public static string ToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        if (!html.Contains('<') && !html.Contains('&')) return html;
        var s = Comments().Replace(html, " ");
        s = Blocks().Replace(s, " ");
        s = Breaks().Replace(s, "\n");
        s = Cells().Replace(s, " \t");
        s = Tags().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        return TextExtractors.Normalize(s);
    }
}

/// <summary>Word, Excel, PowerPoint (formats OOXML), lus directement dans l'archive ZIP.</summary>
public sealed class OpenXmlExtractor : ITextExtractor
{
    public IEnumerable<string> Extensions => new[] { ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".pptx", ".pptm", ".ppsx", ".vsdx" };

    public string Extract(Stream s, string ext, CancellationToken ct)
    {
        using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
        var sb = new StringBuilder();
        switch (ext)
        {
            case ".docx" or ".docm" or ".dotx":
                foreach (var name in new[] { "word/document.xml" }
                    .Concat(zip.Entries.Select(e => e.FullName).Where(n => Regex.IsMatch(n, @"^word/(header|footer|footnotes|endnotes|comments)\d*\.xml$")).Order()))
                {
                    var e = zip.GetEntry(name);
                    if (e is not null) ReadWordXml(e, sb, ct);
                }
                break;
            case ".pptx" or ".pptm" or ".ppsx":
                foreach (var e in zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/(slides/slide|notesSlides/notesSlide)\d+\.xml$"))
                             .OrderBy(e => e.FullName.Contains("notes") ? 1 : 0).ThenBy(e => SlideNo(e.FullName)))
                {
                    ReadParagraphs(e, sb, "http://schemas.openxmlformats.org/drawingml/2006/main", ct);
                    sb.Append("\n\n");
                }
                break;
            case ".xlsx" or ".xlsm":
                ReadExcel(zip, sb, ct);
                break;
            case ".vsdx":
                foreach (var e in zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^visio/pages/page\d+\.xml$")))
                    ReadAllText(e, sb, ct);
                break;
        }
        return sb.ToString();
    }

    private static int SlideNo(string n)
    {
        var m = Regex.Match(n, @"(\d+)\.xml$");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>Texte d'un élément ; le lecteur reste sur la balise fermante (contrairement à ReadElementContentAsString).</summary>
    internal static string ReadText(XmlReader r)
    {
        if (r.IsEmptyElement) return "";
        var sb = new StringBuilder();
        int depth = r.Depth;
        while (r.Read())
        {
            if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace) sb.Append(r.Value);
            else if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;
        }
        return sb.ToString();
    }

    private static XmlReader Open(ZipArchiveEntry e) =>
        XmlReader.Create(e.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = false, XmlResolver = null });

    private static void ReadWordXml(ZipArchiveEntry e, StringBuilder sb, CancellationToken ct)
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var r = Open(e);
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == w)
            {
                switch (r.LocalName)
                {
                    case "t": sb.Append(ReadText(r)); break;
                    case "tab": sb.Append('\t'); break;
                    case "br" or "cr": sb.Append('\n'); break;
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == w && r.LocalName is "p")
            {
                sb.Append('\n');
                ct.ThrowIfCancellationRequested();
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == w && r.LocalName is "tc") sb.Append('\t');
        }
        sb.Append('\n');
    }

    private static void ReadParagraphs(ZipArchiveEntry e, StringBuilder sb, string ns, CancellationToken ct)
    {
        using var r = Open(e);
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == ns && r.LocalName == "t") sb.Append(ReadText(r));
            else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == ns && r.LocalName == "p") sb.Append('\n');
        }
    }

    private static void ReadAllText(ZipArchiveEntry e, StringBuilder sb, CancellationToken ct)
    {
        using var r = Open(e);
        while (r.Read())
            if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) sb.Append(r.Value).Append(' ');
        sb.Append('\n');
    }

    private static void ReadExcel(ZipArchive zip, StringBuilder sb, CancellationToken ct)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst is not null)
        {
            using var r = Open(sst);
            StringBuilder? cur = null;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == ns && r.LocalName == "si") cur = new StringBuilder();
                else if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == ns && r.LocalName == "t" && cur is not null) cur.Append(ReadText(r));
                else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == ns && r.LocalName == "si" && cur is not null) { shared.Add(cur.ToString()); cur = null; }
            }
        }
        // Noms des feuilles
        var sheetNames = new List<string>();
        var wb = zip.GetEntry("xl/workbook.xml");
        if (wb is not null)
        {
            using var r = Open(wb);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet") sheetNames.Add(r.GetAttribute("name") ?? "");
        }
        int idx = 0;
        foreach (var e in zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^xl/worksheets/sheet\d+\.xml$")).OrderBy(e => SlideNo(e.FullName)))
        {
            if (idx < sheetNames.Count) sb.Append(sheetNames[idx]).Append('\n');
            idx++;
            using var r = Open(e);
            string? type = null; bool inV = false, inIs = false;
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == ns)
                {
                    if (r.LocalName == "c") type = r.GetAttribute("t");
                    else if (r.LocalName == "v") inV = true;
                    else if (r.LocalName == "is") inIs = true;
                    else if (r.LocalName == "t" && inIs) { sb.Append(ReadText(r)).Append('\t'); }
                }
                else if (r.NodeType == XmlNodeType.Text && inV)
                {
                    var v = r.Value;
                    if (type == "s" && int.TryParse(v, out var si) && si < shared.Count) sb.Append(shared[si]);
                    else if (type is "str" or "inlineStr" or null or "n" or "b") sb.Append(v);
                    sb.Append('\t');
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == ns)
                {
                    if (r.LocalName == "v") inV = false;
                    else if (r.LocalName == "is") inIs = false;
                    else if (r.LocalName == "row") sb.Append('\n');
                }
            }
            sb.Append("\n\n");
            if (sb.Length > 2_000_000) break;
        }
    }
}

/// <summary>LibreOffice / OpenDocument (odt, ods, odp).</summary>
public sealed class OpenDocumentExtractor : ITextExtractor
{
    public IEnumerable<string> Extensions => new[] { ".odt", ".ods", ".odp", ".odg" };

    public string Extract(Stream s, string ext, CancellationToken ct)
    {
        using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
        var e = zip.GetEntry("content.xml");
        if (e is null) return "";
        const string text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        var sb = new StringBuilder();
        using var r = XmlReader.Create(e.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (r.Read())
        {
            if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace) sb.Append(r.Value);
            else if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == text && r.LocalName is "s") sb.Append(' ');
            else if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == text && r.LocalName is "tab") sb.Append('\t');
            else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == text && r.LocalName is "p" or "h") sb.Append('\n');
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName is "table-cell") sb.Append('\t');
        }
        return sb.ToString();
    }
}

/// <summary>RTF : suppression des mots de contrôle et des groupes non textuels.</summary>
public sealed class RtfExtractor : ITextExtractor
{
    public IEnumerable<string> Extensions => new[] { ".rtf" };

    public string Extract(Stream s, string ext, CancellationToken ct) => ToText(TextDecoding.ReadAll(s));

    internal static string ToText(string rtf)
    {
        var sb = new StringBuilder();
        var skipStack = new Stack<bool>();
        bool skip = false;
        int i = 0;
        string[] destinations = { "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "themedata", "colorschememapping", "latentstyles", "datastore", "xmlnstbl", "listtable", "listoverridetable", "rsidtbl", "generator" };
        while (i < rtf.Length)
        {
            char c = rtf[i];
            if (c == '{') { skipStack.Push(skip); i++; continue; }
            if (c == '}') { skip = skipStack.Count > 0 && skipStack.Pop(); i++; continue; }
            if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                char n = rtf[i];
                if (n is '\\' or '{' or '}') { if (!skip) sb.Append(n); i++; continue; }
                if (n == '*') { skip = true; i++; continue; }
                if (n == '\'')
                {
                    if (i + 2 < rtf.Length && int.TryParse(rtf.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    { if (!skip) sb.Append(TextDecoding.Decode(new[] { (byte)code })); i += 3; continue; }
                }
                int start = i;
                while (i < rtf.Length && char.IsAsciiLetter(rtf[i])) i++;
                var word = rtf[start..i];
                int ps = i;
                if (i < rtf.Length && (rtf[i] == '-' || char.IsAsciiDigit(rtf[i]))) { i++; while (i < rtf.Length && char.IsAsciiDigit(rtf[i])) i++; }
                var param = rtf[ps..i];
                if (i < rtf.Length && rtf[i] == ' ') i++;
                if (destinations.Contains(word)) skip = true;
                else if (!skip)
                {
                    if (word is "par" or "line" or "row") sb.Append('\n');
                    else if (word is "tab" or "cell") sb.Append('\t');
                    else if (word == "u" && int.TryParse(param, out var u))
                    {
                        sb.Append((char)(u < 0 ? u + 65536 : u));
                        if (i < rtf.Length && rtf[i] == '?') i++;
                    }
                }
                continue;
            }
            if (c is '\r' or '\n') { i++; continue; }
            if (!skip) sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}

/// <summary>Message .eml (MIME) : en-têtes utiles + partie texte.</summary>
public sealed class EmlExtractor : ITextExtractor
{
    public IEnumerable<string> Extensions => new[] { ".eml" };

    public string Extract(Stream s, string ext, CancellationToken ct)
    {
        var msg = MimeLite.Parse(TextDecoding.ReadAll(s));
        return $"{msg.Subject}\n{msg.From}\n{msg.To}\n\n{msg.Text}";
    }
}

/// <summary>Analyse MIME très simplifiée (suffit pour l'indexation).</summary>
public static class MimeLite
{
    public sealed record Message(string Subject, string From, string To, string Text);

    public static Message Parse(string raw)
    {
        var (headers, body) = Split(raw);
        string text = ExtractText(headers, body, 0);
        return new Message(Decode2047(Header(headers, "subject")), Decode2047(Header(headers, "from")), Decode2047(Header(headers, "to")), text);
    }

    private static (Dictionary<string, string> Headers, string Body) Split(string raw)
    {
        raw = raw.Replace("\r\n", "\n");
        int sep = raw.IndexOf("\n\n", StringComparison.Ordinal);
        var head = sep < 0 ? raw : raw[..sep];
        var body = sep < 0 ? "" : raw[(sep + 2)..];
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var line in head.Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && last is not null) { h[last] += " " + line.Trim(); continue; }
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            last = line[..c].Trim();
            h[last] = line[(c + 1)..].Trim();
        }
        return (h, body);
    }

    private static string Header(Dictionary<string, string> h, string k) => h.TryGetValue(k, out var v) ? v : "";

    private static string Param(string header, string name)
    {
        var m = Regex.Match(header, name + @"\s*=\s*(""([^""]*)""|([^;\s]+))", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : "";
    }

    private static string ExtractText(Dictionary<string, string> headers, string body, int depth)
    {
        var ct = Header(headers, "content-type");
        if (ct.Length == 0) ct = "text/plain";
        var lower = ct.ToLowerInvariant();
        if (lower.StartsWith("multipart/") && depth < 8)
        {
            var boundary = Param(ct, "boundary");
            if (boundary.Length == 0) return "";
            var parts = body.Split("--" + boundary);
            string? plain = null, html = null;
            var others = new StringBuilder();
            foreach (var p in parts.Skip(1))
            {
                if (p.StartsWith("--")) break;
                var (ph, pb) = Split(p.TrimStart('\n'));
                var pct = Header(ph, "content-type").ToLowerInvariant();
                var disp = Header(ph, "content-disposition").ToLowerInvariant();
                if (disp.StartsWith("attachment"))
                {
                    var fn = Param(Header(ph, "content-disposition"), "filename");
                    if (fn.Length > 0) others.Append("\n[Pièce jointe] ").Append(Decode2047(fn));
                    continue;
                }
                if (pct.StartsWith("multipart/")) others.Append('\n').Append(ExtractText(ph, pb, depth + 1));
                else if (pct.StartsWith("text/html")) html ??= DecodeBody(ph, pb);
                else if (pct.StartsWith("text/plain") || pct.Length == 0) plain ??= DecodeBody(ph, pb);
            }
            var main = plain ?? (html is null ? "" : HtmlText.ToText(html));
            return main + others;
        }
        var text = DecodeBody(headers, body);
        return lower.StartsWith("text/html") ? HtmlText.ToText(text) : text;
    }

    private static string DecodeBody(Dictionary<string, string> h, string body)
    {
        var enc = Header(h, "content-transfer-encoding").ToLowerInvariant();
        var charset = Param(Header(h, "content-type"), "charset");
        byte[] bytes;
        if (enc == "base64")
        {
            try { bytes = Convert.FromBase64String(Regex.Replace(body, @"\s+", "")); }
            catch (FormatException) { return ""; }
        }
        else if (enc == "quoted-printable") bytes = QuotedPrintable(body);
        else bytes = Encoding.Latin1.GetBytes(body);
        return GetString(bytes, charset);
    }

    private static string GetString(byte[] b, string charset)
    {
        try
        {
            if (charset.Length > 0 && !charset.Equals("windows-1252", StringComparison.OrdinalIgnoreCase) && !charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase))
                return Encoding.GetEncoding(charset).GetString(b);
        }
        catch (ArgumentException) { }
        return TextDecoding.Decode(b);
    }

    private static byte[] QuotedPrintable(string s)
    {
        var o = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '=' && i + 1 < s.Length && s[i + 1] == '\n') { i++; continue; }
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            { o.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; continue; }
            o.Add((byte)(s[i] & 0xFF));
        }
        return o.ToArray();
    }

    /// <summary>Décodage des en-têtes encodés RFC 2047 (=?utf-8?B?…?= / =?…?Q?…?=).</summary>
    public static string Decode2047(string s)
    {
        return Regex.Replace(s, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=\s*", m =>
        {
            try
            {
                var cs = m.Groups[1].Value;
                byte[] b = m.Groups[2].Value.ToUpperInvariant() == "B"
                    ? Convert.FromBase64String(m.Groups[3].Value)
                    : QuotedPrintable(m.Groups[3].Value.Replace('_', ' '));
                return GetString(b, cs);
            }
            catch (FormatException) { return m.Value; }
        });
    }
}
