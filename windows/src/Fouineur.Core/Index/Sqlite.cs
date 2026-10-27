using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Fouineur.Core.Index;

/// <summary>
/// Accès direct à la bibliothèque native SQLite, sans paquet NuGet.
/// Ordre de recherche : e_sqlite3 (livré avec l'application, FTS5 garanti), sqlite3, winsqlite3 (Windows), libsqlite3 (Linux).
/// </summary>
internal static unsafe class Native
{
    private const string Lib = "fouineur_sqlite";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
    }

    internal static string? LoadedLibrary { get; private set; }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "e_sqlite3", "sqlite3", "winsqlite3" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libe_sqlite3", "libsqlite3.dylib", "/usr/lib/libsqlite3.dylib" }
                : new[] { "libe_sqlite3", "libsqlite3.so.0", "libsqlite3.so" };
        foreach (var c in candidates)
        {
            if (NativeLibrary.TryLoad(c, asm, path, out var h)) { LoadedLibrary = c; return h; }
            if (NativeLibrary.TryLoad(c, out h)) { LoadedLibrary = c; return h; }
        }
        throw new DllNotFoundException("Bibliothèque SQLite introuvable (e_sqlite3, sqlite3 ou winsqlite3).");
    }

    internal static void EnsureLoaded() { /* déclenche le constructeur statique */ }

    public const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    public const int SQLITE_OPEN_READWRITE = 0x2, SQLITE_OPEN_CREATE = 0x4, SQLITE_OPEN_FULLMUTEX = 0x10000;
    public const int SQLITE_INTEGER = 1, SQLITE_FLOAT = 2, SQLITE_TEXT = 3, SQLITE_BLOB = 4, SQLITE_NULL = 5;
    public static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_open_v2(byte* filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_prepare_v2(IntPtr db, byte* sql, int nBytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_reset(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_clear_bindings(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_text(IntPtr stmt, int i, byte* val, int n, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_int64(IntPtr stmt, int i, long val);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_double(IntPtr stmt, int i, double val);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_blob(IntPtr stmt, int i, byte* val, int n, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_null(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_column_type(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern long sqlite3_column_int64(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern double sqlite3_column_double(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte* sqlite3_column_text(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void* sqlite3_column_blob(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_column_bytes(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern long sqlite3_last_insert_rowid(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sqlite3_libversion();
}

public sealed class SqliteException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Connexion SQLite minimale. Non thread-safe : l'appelant sérialise (voir <see cref="IndexStore"/>).</summary>
internal sealed unsafe class SqliteDb : IDisposable
{
    private IntPtr _db;
    private readonly Dictionary<string, SqliteStmt> _cache = new();

    public SqliteDb(string path)
    {
        Native.EnsureLoaded();
        var bytes = Utf8Z(path);
        fixed (byte* p = bytes)
        {
            int rc = Native.sqlite3_open_v2(p, out _db,
                Native.SQLITE_OPEN_READWRITE | Native.SQLITE_OPEN_CREATE | Native.SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
            if (rc != Native.SQLITE_OK) throw new SqliteException($"Ouverture de l'index impossible : {ErrMsg()}", rc);
        }
        Native.sqlite3_busy_timeout(_db, 10_000);
    }

    public static string Version => Marshal.PtrToStringUTF8(Native.sqlite3_libversion()) ?? "?";
    public int Changes => Native.sqlite3_changes(_db);
    public long LastInsertRowId => Native.sqlite3_last_insert_rowid(_db);

    /// <summary>Remet à zéro toutes les requêtes en cache : libère l'instantané de lecture (sinon, en WAL, on ne voit plus les nouvelles écritures).</summary>
    public void ResetAll()
    {
        foreach (var st in _cache.Values) st.Reset();
    }

    internal string ErrMsg() => Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(_db)) ?? "erreur inconnue";

    internal static byte[] Utf8Z(string s)
    {
        var n = Encoding.UTF8.GetByteCount(s);
        var b = new byte[n + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    /// <summary>Requête préparée mise en cache (réutilisée après reset).</summary>
    public SqliteStmt Prepare(string sql)
    {
        if (_cache.TryGetValue(sql, out var s)) { s.Reset(); return s; }
        s = new SqliteStmt(this, sql, cached: true);
        _cache[sql] = s;
        return s;
    }

    internal IntPtr PrepareRaw(string sql)
    {
        var bytes = Utf8Z(sql);
        fixed (byte* p = bytes)
        {
            int rc = Native.sqlite3_prepare_v2(_db, p, -1, out var stmt, IntPtr.Zero);
            if (rc != Native.SQLITE_OK) throw new SqliteException($"{ErrMsg()} — dans : {sql}", rc);
            return stmt;
        }
    }

    /// <summary>Exécute une ou plusieurs instructions (sans paramètres), en suivant le pointeur « tail » de SQLite.</summary>
    public void Exec(string sql)
    {
        var bytes = Utf8Z(sql);
        fixed (byte* p0 = bytes)
        {
            byte* p = p0;
            byte* end = p0 + bytes.Length - 1;
            while (p < end)
            {
                byte* tail;
                int rc = Native.sqlite3_prepare_v2(_db, p, (int)(end - p), out var stmt, (IntPtr)(&tail));
                if (rc != Native.SQLITE_OK) throw new SqliteException($"{ErrMsg()} — dans : {sql}", rc);
                if (stmt == IntPtr.Zero) break; // reste = blancs/commentaires
                try
                {
                    while (true)
                    {
                        rc = Native.sqlite3_step(stmt);
                        if (rc == Native.SQLITE_ROW) continue;
                        if (rc == Native.SQLITE_DONE) break;
                        throw new SqliteException($"{ErrMsg()} — dans : {sql}", rc);
                    }
                }
                finally { Native.sqlite3_finalize(stmt); }
                p = tail;
            }
        }
    }

    public int Execute(string sql, params object?[] args)
    {
        var st = Prepare(sql);
        st.BindAll(args);
        st.StepAll();
        return Changes;
    }

    public T? Scalar<T>(string sql, params object?[] args)
    {
        var st = Prepare(sql);
        st.BindAll(args);
        if (!st.Step()) return default;
        object? v = st.Value(0);
        if (v is null) return default;
        if (typeof(T) == typeof(int)) return (T)(object)Convert.ToInt32(v);
        if (typeof(T) == typeof(long)) return (T)(object)Convert.ToInt64(v);
        if (typeof(T) == typeof(string)) return (T)(object)Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!;
        if (typeof(T) == typeof(double)) return (T)(object)Convert.ToDouble(v);
        return (T)v;
    }

    public void Dispose()
    {
        foreach (var s in _cache.Values) s.Finalize_();
        _cache.Clear();
        if (_db != IntPtr.Zero) { Native.sqlite3_close_v2(_db); _db = IntPtr.Zero; }
    }
}

internal sealed unsafe class SqliteStmt : IDisposable
{
    private readonly SqliteDb _db;
    private IntPtr _st;
    private readonly bool _cached;
    private readonly string _sql;

    public SqliteStmt(SqliteDb db, string sql, bool cached)
    {
        _db = db; _sql = sql; _cached = cached;
        _st = db.PrepareRaw(sql);
    }

    public void Reset()
    {
        Native.sqlite3_reset(_st);
        Native.sqlite3_clear_bindings(_st);
    }

    public void BindAll(object?[] args)
    {
        for (int i = 0; i < args.Length; i++) Bind(i + 1, args[i]);
    }

    public void Bind(int i, object? v)
    {
        int rc;
        switch (v)
        {
            case null: rc = Native.sqlite3_bind_null(_st, i); break;
            case string s:
                {
                    var b = System.Text.Encoding.UTF8.GetBytes(s);
                    fixed (byte* p = b) rc = Native.sqlite3_bind_text(_st, i, b.Length == 0 ? (byte*)&i : p, b.Length, Native.SQLITE_TRANSIENT);
                    break;
                }
            case int n: rc = Native.sqlite3_bind_int64(_st, i, n); break;
            case long n: rc = Native.sqlite3_bind_int64(_st, i, n); break;
            case bool b: rc = Native.sqlite3_bind_int64(_st, i, b ? 1 : 0); break;
            case double d: rc = Native.sqlite3_bind_double(_st, i, d); break;
            case float f: rc = Native.sqlite3_bind_double(_st, i, f); break;
            case byte[] blob:
                fixed (byte* p = blob) rc = Native.sqlite3_bind_blob(_st, i, blob.Length == 0 ? (byte*)&i : p, blob.Length, Native.SQLITE_TRANSIENT);
                break;
            case Enum e: rc = Native.sqlite3_bind_int64(_st, i, Convert.ToInt64(e)); break;
            default: throw new ArgumentException($"Type non pris en charge : {v.GetType()}");
        }
        if (rc != Native.SQLITE_OK) throw new SqliteException($"Liaison du paramètre {i} : {_db.ErrMsg()}", rc);
    }

    /// <summary>Avance d'une ligne ; false à la fin.</summary>
    public bool Step()
    {
        int rc = Native.sqlite3_step(_st);
        if (rc == Native.SQLITE_ROW) return true;
        if (rc == Native.SQLITE_DONE) return false;
        var msg = _db.ErrMsg();
        Native.sqlite3_reset(_st);
        throw new SqliteException($"{msg} — dans : {_sql}", rc);
    }

    public void StepAll() { while (Step()) { } }

    public int ColumnCount => Native.sqlite3_column_count(_st);

    public object? Value(int i) => Native.sqlite3_column_type(_st, i) switch
    {
        Native.SQLITE_NULL => null,
        Native.SQLITE_INTEGER => Native.sqlite3_column_int64(_st, i),
        Native.SQLITE_FLOAT => Native.sqlite3_column_double(_st, i),
        Native.SQLITE_BLOB => Blob(i),
        _ => Text(i),
    };

    public long Int64(int i) => Native.sqlite3_column_int64(_st, i);
    public double Double(int i) => Native.sqlite3_column_double(_st, i);
    public bool IsNull(int i) => Native.sqlite3_column_type(_st, i) == Native.SQLITE_NULL;

    public string Text(int i)
    {
        var p = Native.sqlite3_column_text(_st, i);
        if (p == null) return "";
        int n = Native.sqlite3_column_bytes(_st, i);
        return System.Text.Encoding.UTF8.GetString(p, n);
    }

    public byte[] Blob(int i)
    {
        var p = Native.sqlite3_column_blob(_st, i);
        int n = Native.sqlite3_column_bytes(_st, i);
        if (p == null || n == 0) return Array.Empty<byte>();
        var b = new byte[n];
        new ReadOnlySpan<byte>(p, n).CopyTo(b);
        return b;
    }

    /// <summary>Copie un BLOB de float32 directement dans un tableau.</summary>
    public float[] Floats(int i)
    {
        var p = Native.sqlite3_column_blob(_st, i);
        int n = Native.sqlite3_column_bytes(_st, i);
        if (p == null || n < 4) return Array.Empty<float>();
        var f = new float[n / 4];
        new ReadOnlySpan<byte>(p, f.Length * 4).CopyTo(MemoryMarshal.AsBytes(f.AsSpan()));
        return f;
    }

    internal void Finalize_()
    {
        if (_st != IntPtr.Zero) { Native.sqlite3_finalize(_st); _st = IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (!_cached) Finalize_();
    }
}
