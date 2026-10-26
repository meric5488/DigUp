using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Fouineur.Core.Extraction;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Fouineur.App.Services;

/// <summary>Texte des PDF (PdfPig), page par page.</summary>
public sealed class PdfExtractor : ITextExtractor
{
    public IEnumerable<string> Extensions => new[] { ".pdf" };
    public int MaxPages { get; set; } = 500;

    public string Extract(Stream stream, string extension, CancellationToken ct)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        int n = 0;
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            try { sb.AppendLine(ContentOrderTextExtractor.GetText(page)); }
            catch (Exception) { sb.AppendLine(page.Text); }
            if (++n >= MaxPages || sb.Length > 2_000_000) break;
        }
        return sb.ToString();
    }
}

/// <summary>
/// Anciens formats Office (.doc, .xls, .ppt), .msg, .vsd… via les filtres d'indexation Windows (IFilter),
/// s'ils sont installés sur le poste (Office ou « Microsoft Office Filter Pack »).
/// </summary>
public sealed class IFilterExtractor : ITextExtractor
{
    private readonly string[] _exts;
    public IFilterExtractor(params string[] extensions) { _exts = extensions; }
    public IEnumerable<string> Extensions => _exts;

    public string Extract(Stream stream, string extension, CancellationToken ct)
    {
        // LoadIFilter a besoin d'un chemin : on passe par un fichier temporaire si le flux n'en est pas un.
        string path; bool temp = false;
        if (stream is FileStream fs) path = fs.Name;
        else
        {
            path = Path.Combine(Path.GetTempPath(), "fouineur-" + Guid.NewGuid().ToString("N") + extension);
            using (var o = File.Create(path)) stream.CopyTo(o);
            temp = true;
        }
        try { return Run(path, ct); }
        finally { if (temp) try { File.Delete(path); } catch (IOException) { } }
    }

    private static string Run(string path, CancellationToken ct)
    {
        int hr = LoadIFilter(path, IntPtr.Zero, out var unk);
        if (hr != 0 || unk == IntPtr.Zero) return "";
        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(unk);
            var filter = (IFilter)obj;
            const int flags = 1 | 2 | 8 | 16; // paragraphes, sauts de ligne, espaces, attributs d'index
            if (filter.Init(flags, 0, IntPtr.Zero, out _) != 0) return "";
            var sb = new StringBuilder();
            var buf = new char[8192];
            while (sb.Length < 2_000_000)
            {
                ct.ThrowIfCancellationRequested();
                hr = filter.GetChunk(out var chunk);
                if (hr == FILTER_E_END_OF_CHUNKS) break;
                if (hr != 0) { if (hr == FILTER_E_EMBEDDING_UNAVAILABLE || hr == FILTER_E_LINK_UNAVAILABLE) continue; break; }
                if ((chunk.flags & CHUNK_TEXT) == 0) continue;
                if (chunk.breakType != 0) sb.Append('\n');
                while (true)
                {
                    int n = buf.Length;
                    hr = filter.GetText(ref n, buf);
                    if (hr == FILTER_E_NO_MORE_TEXT || hr == FILTER_E_NO_TEXT) break;
                    if (hr != 0 && hr != FILTER_S_LAST_TEXT) break;
                    sb.Append(buf, 0, Math.Min(n, buf.Length));
                    if (hr == FILTER_S_LAST_TEXT) break;
                }
            }
            return sb.ToString();
        }
        catch (Exception) { return ""; }
        finally
        {
            if (obj is not null) Marshal.ReleaseComObject(obj);
            Marshal.Release(unk);
        }
    }

    private const int FILTER_E_END_OF_CHUNKS = unchecked((int)0x80041700);
    private const int FILTER_E_NO_MORE_TEXT = unchecked((int)0x80041701);
    private const int FILTER_E_NO_TEXT = unchecked((int)0x80041705);
    private const int FILTER_E_EMBEDDING_UNAVAILABLE = unchecked((int)0x80041707);
    private const int FILTER_E_LINK_UNAVAILABLE = unchecked((int)0x80041708);
    private const int FILTER_S_LAST_TEXT = 0x00041709;
    private const int CHUNK_TEXT = 1;

    [DllImport("query.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadIFilter(string pwcsPath, IntPtr pUnkOuter, out IntPtr ppIUnk);

    [StructLayout(LayoutKind.Sequential)]
    private struct FULLPROPSPEC { public Guid guidPropSet; public uint ulKind; public IntPtr value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STAT_CHUNK
    {
        public uint idChunk;
        public int breakType;
        public int flags;
        public uint locale;
        public FULLPROPSPEC attribute;
        public uint idChunkSource;
        public uint cwcStartSource;
        public uint cwcLenSource;
    }

    [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig] int Init(int grfFlags, int cAttributes, IntPtr aAttributes, out int pdwFlags);
        [PreserveSig] int GetChunk(out STAT_CHUNK pStat);
        [PreserveSig] int GetText(ref int pcwcBuffer, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] awcBuffer);
        [PreserveSig] int GetValue(out IntPtr ppPropValue);
        [PreserveSig] int BindRegion(IntPtr origPos, ref Guid riid, out IntPtr ppunk);
    }
}
