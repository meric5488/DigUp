using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fouineur.App.Services;

/// <summary>
/// Miniatures de l'Explorateur (IShellItemImageFactory), calculées sur un fil STA dédié.
/// </summary>
public sealed class ShellThumbnails : IDisposable
{
    private readonly BlockingCollection<(string Path, int Size, TaskCompletionSource<ImageSource?> Tcs)> _queue = new();
    private readonly Thread _thread;
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new();

    public ShellThumbnails()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Miniatures" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<ImageSource?> GetAsync(string path, int size = 256)
    {
        var key = path + "|" + size;
        if (_cache.TryGetValue(key, out var img)) return Task.FromResult(img);
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((path, size, tcs));
        return tcs.Task.ContinueWith(t => { _cache[key] = t.Result; return t.Result; }, TaskScheduler.Default);
    }

    private void Loop()
    {
        foreach (var (path, size, tcs) in _queue.GetConsumingEnumerable())
        {
            try { tcs.SetResult(Get(path, size)); }
            catch (Exception) { tcs.SetResult(null); }
        }
    }

    private static ImageSource? Get(string path, int size)
    {
        if (!File.Exists(path)) return null;
        var iid = typeof(IShellItemImageFactory).GUID;
        Controls.PreviewHost.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var obj);
        var factory = (IShellItemImageFactory)obj;
        try
        {
            // Miniature seulement (pas d'icône générique : l'application affiche son propre pictogramme).
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_RESIZETOFIT | SIIGBF_THUMBNAILONLY, out var hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbmp); }
        }
        finally { Marshal.FinalReleaseComObject(factory); }
    }

    public static ImageSource? FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }
    }

    public void Dispose() => _queue.CompleteAdding();

    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }
}
