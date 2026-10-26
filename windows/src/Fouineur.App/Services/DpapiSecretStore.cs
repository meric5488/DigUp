using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Fouineur.Core.Configuration;

namespace Fouineur.App.Services;

/// <summary>Secrets chiffrés pour l'utilisateur Windows courant (DPAPI), dans un fichier unique.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _values;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fouineur/secrets/v1");

    public DpapiSecretStore(string path)
    {
        _path = path;
        _values = Load();
    }

    public string? Get(string key) { lock (_gate) return _values.TryGetValue(key, out var v) ? v : null; }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (value is null) _values.Remove(key); else _values[key] = value;
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var plain = Unprotect(File.ReadAllBytes(_path));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
        }
        catch (Exception e)
        {
            Infrastructure.Logger.Warn($"Secrets illisibles, ils seront redemandés ({e.Message}).");
            return new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var data = Protect(JsonSerializer.SerializeToUtf8Bytes(_values));
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, _path, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static byte[] Protect(byte[] data) => Run(data, true);
    private static byte[] Unprotect(byte[] data) => Run(data, false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = hIn.AddrOfPinnedObject() };
            var ent = new DATA_BLOB { cbData = Entropy.Length, pbData = hEnt.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "Fouineur", ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { hIn.Free(); hEnt.Free(); }
    }
}
