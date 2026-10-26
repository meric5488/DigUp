using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Fouineur.App.Controls;

/// <summary>
/// Héberge les gestionnaires d'aperçu de Windows (les mêmes que le volet de visualisation de l'Explorateur) :
/// Word, Excel, PowerPoint, PDF, Outlook, images… selon ce qui est installé.
/// </summary>
public sealed class PreviewHost : HwndHost
{
    private IntPtr _hwnd;
    private IPreviewHandler? _handler;
    private IStream? _stream;
    private string? _pendingPath;

    public string? CurrentFile { get; private set; }

    /// <summary>Indique si Windows dispose d'un gestionnaire d'aperçu pour cette extension.</summary>
    public static bool HasHandler(string extension) => GetHandlerClsid(extension) is not null;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_pendingPath is not null) { var p = _pendingPath; _pendingPath = null; Show(p); }
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Unload();
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>Affiche l'aperçu du fichier ; renvoie faux si aucun gestionnaire ne convient.</summary>
    public bool Show(string path)
    {
        Unload();
        if (_hwnd == IntPtr.Zero) { _pendingPath = path; return HasHandler(Path.GetExtension(path)); }
        var clsid = GetHandlerClsid(Path.GetExtension(path));
        if (clsid is null) return false;
        try
        {
            var type = Type.GetTypeFromCLSID(clsid.Value, throwOnError: true)!;
            var obj = Activator.CreateInstance(type)!;
            if (obj is IInitializeWithStream iws)
            {
                int hr = SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out var stream);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr)!;
                _stream = stream;
                iws.Initialize(stream, STGM_READ);
            }
            else if (obj is IInitializeWithFile iwf) iwf.Initialize(path, STGM_READ);
            else if (obj is IInitializeWithItem iwi)
            {
                var iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
                iwi.Initialize((IShellItem)item, STGM_READ);
            }
            else { Marshal.FinalReleaseComObject(obj); return false; }

            _handler = (IPreviewHandler)obj;
            var r = ClientRect();
            _handler.SetWindow(_hwnd, ref r);
            TrySetVisuals(obj);
            _handler.DoPreview();
            CurrentFile = path;
            return true;
        }
        catch (Exception e)
        {
            Infrastructure.Logger.Warn($"Aperçu impossible pour {Path.GetFileName(path)} : {e.Message}");
            Unload();
            return false;
        }
    }

    private void TrySetVisuals(object obj)
    {
        if (obj is not IPreviewHandlerVisuals v) return;
        try
        {
            var bg = (TryFindResource("SolidBackgroundFillColorBase") as Color?) ?? (Background as SolidColorBrush)?.Color;
            if (bg is { } c) v.SetBackgroundColor((uint)(c.R | (c.G << 8) | (c.B << 16)));
        }
        catch (Exception) { /* facultatif */ }
    }

    public Brush? Background { get; set; }

    public void Unload()
    {
        CurrentFile = null;
        if (_handler is not null)
        {
            try { _handler.Unload(); } catch (Exception) { }
            try { Marshal.FinalReleaseComObject(_handler); } catch (Exception) { }
            _handler = null;
        }
        if (_stream is not null)
        {
            try { Marshal.FinalReleaseComObject(_stream); } catch (Exception) { }
            _stream = null;
        }
    }

    private RECT ClientRect()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new RECT { left = 0, top = 0, right = (int)(ActualWidth * dpi.DpiScaleX), bottom = (int)(ActualHeight * dpi.DpiScaleY) };
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_handler is not null)
        {
            var r = ClientRect();
            try { _handler.SetRect(ref r); } catch (Exception) { }
        }
    }

    private static Guid? GetHandlerClsid(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        uint size = 64;
        var sb = new StringBuilder((int)size);
        int hr = AssocQueryString(ASSOCF_INIT_DEFAULTTOSTAR, ASSOCSTR_SHELLEXTENSION, extension, "{8895b1c6-b41f-4c1c-a562-0d564250836f}", sb, ref size);
        if (hr != 0) return null;
        return Guid.TryParse(sb.ToString(), out var g) ? g : null;
    }

    // ─── Interop ───

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    private const uint STGM_READ = 0, STGM_SHARE_DENY_NONE = 0x40;
    private const int ASSOCF_INIT_DEFAULTTOSTAR = 0x4;
    private const int ASSOCSTR_SHELLEXTENSION = 16;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate, IntPtr pstmTemplate, out IStream ppstm);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPreviewHandlerVisuals
    {
        void SetBackgroundColor(uint color);
        void SetFont(IntPtr plf);
        void SetTextColor(uint color);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithStream
    {
        void Initialize(IStream pstream, uint grfMode);
    }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithItem
    {
        void Initialize(IShellItem psi, uint grfMode);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem { }
}
