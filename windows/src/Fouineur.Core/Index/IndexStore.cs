using System.Globalization;
using System.Runtime.InteropServices;
using Fouineur.Core.Model;

namespace Fouineur.Core.Index;

/// <summary>
/// Index unique dans un fichier SQLite : éléments + texte intégral (FTS5) + vecteurs + états de synchronisation.
/// Toutes les méthodes sont thread-safe. Les écritures passent par une connexion (et un verrou) dédiée ;
/// la recherche utilise une connexion de lecture séparée : grâce au journal WAL, elle n'attend jamais la synchronisation.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private readonly SqliteDb _db;
    private readonly object _lock = new();
    private readonly SqliteDb _rdb;          // lecture (recherche, aperçu, compteurs)
    private readonly object _rlock = new();
    private long _vecVersion;
    private volatile VectorCache? _vecCache;
    private int _vecRefreshing;
    private DateTime _vecLastRefresh = DateTime.MinValue;
    /// <summary>Délai minimal entre deux rechargements des vecteurs pendant une vectorisation en cours.</summary>
    public TimeSpan VectorRefreshInterval { get; set; } = TimeSpan.FromSeconds(20);
    public const int SchemaVersion = 2;

    public string Path { get; }

    public IndexStore(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteDb(path);
        _db.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32768;");
        CreateSchema();
        _rdb = OpenReader(path);
    }

    private static SqliteDb OpenReader(string path)
    {
        var db = new SqliteDb(path);
        db.Exec("PRAGMA query_only=1; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456;");
        return db;
    }

    public static string SqliteVersion => SqliteDb.Version;
    public static string? SqliteLibrary => Native.LoadedLibrary;

    private void CreateSchema()
    {
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS items(
                id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                kind INTEGER NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                author TEXT NOT NULL DEFAULT '',
                date INTEGER NOT NULL DEFAULT 0,
                location TEXT NOT NULL DEFAULT '',
                open_uri TEXT NOT NULL DEFAULT '',
                ext TEXT NOT NULL DEFAULT '',
                size INTEGER NOT NULL DEFAULT 0,
                ancestors TEXT NOT NULL DEFAULT '|',
                version TEXT NOT NULL DEFAULT '',
                extra TEXT NOT NULL DEFAULT '{}',
                body TEXT NOT NULL DEFAULT '',
                embedded INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS items_source ON items(source);
            CREATE INDEX IF NOT EXISTS items_kind_date ON items(kind, date DESC);
            CREATE INDEX IF NOT EXISTS items_date ON items(date DESC);
            CREATE INDEX IF NOT EXISTS items_embedded ON items(embedded);
            CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
                title, author, location, body,
                content='items', content_rowid='rowid',
                tokenize="unicode61 remove_diacritics 2",
                prefix='2 3'
            );
            CREATE TRIGGER IF NOT EXISTS items_ai AFTER INSERT ON items
            BEGIN
                INSERT INTO items_fts(rowid, title, author, location, body) VALUES (new.rowid, new.title, new.author, new.location, new.body);
            END;
            CREATE TRIGGER IF NOT EXISTS items_ad AFTER DELETE ON items
            BEGIN
                INSERT INTO items_fts(items_fts, rowid, title, author, location, body) VALUES ('delete', old.rowid, old.title, old.author, old.location, old.body);
            END;
            CREATE TRIGGER IF NOT EXISTS items_au AFTER UPDATE OF title, author, location, body ON items
            BEGIN
                INSERT INTO items_fts(items_fts, rowid, title, author, location, body) VALUES ('delete', old.rowid, old.title, old.author, old.location, old.body);
                INSERT INTO items_fts(rowid, title, author, location, body) VALUES (new.rowid, new.title, new.author, new.location, new.body);
            END;
            CREATE TABLE IF NOT EXISTS chunks(
                item_rowid INTEGER NOT NULL,
                seq INTEGER NOT NULL,
                start INTEGER NOT NULL,
                length INTEGER NOT NULL,
                vec BLOB NOT NULL,
                PRIMARY KEY(item_rowid, seq)
            );
            CREATE TABLE IF NOT EXISTS state(source TEXT NOT NULL, key TEXT NOT NULL, value TEXT, PRIMARY KEY(source, key));
            CREATE TABLE IF NOT EXISTS tree(source TEXT NOT NULL, id TEXT NOT NULL, parent TEXT, name TEXT NOT NULL DEFAULT '', PRIMARY KEY(source, id));
            CREATE TABLE IF NOT EXISTS messages(
                source TEXT NOT NULL, id TEXT NOT NULL, doc TEXT NOT NULL,
                ts INTEGER NOT NULL, author TEXT NOT NULL DEFAULT '', text TEXT NOT NULL DEFAULT '', web_url TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(source, id)
            );
            CREATE INDEX IF NOT EXISTS messages_doc ON messages(source, doc);
            """);
        lock (_lock) _db.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES('schema', ?)", SchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    // ───────────────────────────── Transactions ─────────────────────────────

    /// <summary>Regroupe des écritures (beaucoup plus rapide). Imbrication gérée.</summary>
    public IDisposable Batch() => new BatchScope(this);

    private int _batchDepth;

    private sealed class BatchScope : IDisposable
    {
        private readonly IndexStore _s; private bool _done;
        public BatchScope(IndexStore s)
        {
            _s = s;
            Monitor.Enter(s._lock);
            if (s._batchDepth++ == 0) s._db.Exec("BEGIN IMMEDIATE;");
        }
        public void Dispose()
        {
            if (_done) return; _done = true;
            try { if (--_s._batchDepth == 0) _s._db.Exec("COMMIT;"); }
            finally { Monitor.Exit(_s._lock); }
        }
    }

    // ───────────────────────────── Éléments ─────────────────────────────

    /// <summary>Insère ou met à jour. Remet à zéro les vecteurs si le texte change.</summary>
    public void Upsert(IndexedItem it)
    {
        lock (_lock)
        {
            var old = _db.Scalar<string>("SELECT title || char(1) || body FROM items WHERE id = ?", it.Id);
            bool textChanged = old is null || old != it.Title + "\u0001" + it.Body;
            _db.Execute("""
                INSERT INTO items(id, source, kind, title, author, date, location, open_uri, ext, size, ancestors, version, extra, body, embedded)
                VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, 0)
                ON CONFLICT(id) DO UPDATE SET
                    source=?2, kind=?3, title=?4, author=?5, date=?6, location=?7, open_uri=?8, ext=?9, size=?10,
                    ancestors=?11, version=?12, extra=?13, body=?14,
                    embedded = CASE WHEN ?15 THEN 0 ELSE embedded END
                """,
                it.Id, it.SourceId, (int)it.Kind, it.Title, it.Author, it.Date.ToUnixTimeMilliseconds(), it.Location,
                it.OpenUri, it.Extension, it.Size, IndexedItem.AncestorsToColumn(it.Ancestors), it.Version, it.ExtraJson,
                it.Body, textChanged);
            if (textChanged && old is not null)
            {
                var rowid = _db.Scalar<long>("SELECT rowid FROM items WHERE id = ?", it.Id);
                if (_db.Execute("DELETE FROM chunks WHERE item_rowid = ?", rowid) > 0) Interlocked.Increment(ref _vecVersion);
            }
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var rowid = _db.Scalar<long?>("SELECT rowid FROM items WHERE id = ?", id);
            if (rowid is null) return false;
            if (_db.Execute("DELETE FROM chunks WHERE item_rowid = ?", rowid.Value) > 0) Interlocked.Increment(ref _vecVersion);
            _db.Execute("DELETE FROM items WHERE rowid = ?", rowid.Value);
            return true;
        }
    }

    public int DeleteSource(string sourceId)
    {
        lock (_lock)
        {
            _db.Execute("DELETE FROM chunks WHERE item_rowid IN (SELECT rowid FROM items WHERE source = ?)", sourceId);
            int n = _db.Execute("DELETE FROM items WHERE source = ?", sourceId);
            _db.Execute("DELETE FROM state WHERE source = ?", sourceId);
            _db.Execute("DELETE FROM tree WHERE source = ?", sourceId);
            _db.Execute("DELETE FROM messages WHERE source = ?", sourceId);
            Interlocked.Increment(ref _vecVersion);
            return n;
        }
    }

    /// <summary>Version enregistrée d'un élément (null s'il n'est pas indexé).</summary>
    public string? GetVersion(string id)
    {
        lock (_lock) return _db.Scalar<string>("SELECT version FROM items WHERE id = ?", id);
    }

    /// <summary>Identifiant → version pour toute une source (sert au « marquer et balayer »).</summary>
    public Dictionary<string, string> GetVersions(string sourceId, string? idPrefix = null)
    {
        lock (_lock)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            var st = idPrefix is null
                ? _db.Prepare("SELECT id, version FROM items WHERE source = ?")
                : _db.Prepare("SELECT id, version FROM items WHERE source = ? AND substr(id, 1, length(?2)) = ?2");
            st.Bind(1, sourceId);
            if (idPrefix is not null) st.Bind(2, idPrefix);
            while (st.Step()) d[st.Text(0)] = st.Text(1);
            return d;
        }
    }

    /// <summary>Identifiant → conteneurs, pour élaguer selon les règles de sélection.</summary>
    public List<(string Id, IReadOnlyList<string> Ancestors)> GetAncestors(string sourceId)
    {
        lock (_lock)
        {
            var list = new List<(string, IReadOnlyList<string>)>();
            var st = _db.Prepare("SELECT id, ancestors FROM items WHERE source = ?");
            st.Bind(1, sourceId);
            while (st.Step()) list.Add((st.Text(0), IndexedItem.AncestorsFromColumn(st.Text(1))));
            return list;
        }
    }

    private const string ItemColumns = "i.rowid, i.id, i.source, i.kind, i.title, i.author, i.date, i.location, i.open_uri, i.ext, i.size, i.ancestors, i.version, i.extra";

    private static IndexedItem ReadItem(SqliteStmt st, bool withBody = false) => new()
    {
        Id = st.Text(1),
        SourceId = st.Text(2),
        Kind = (ItemKind)st.Int64(3),
        Title = st.Text(4),
        Author = st.Text(5),
        Date = DateTimeOffset.FromUnixTimeMilliseconds(st.Int64(6)),
        Location = st.Text(7),
        OpenUri = st.Text(8),
        Extension = st.Text(9),
        Size = st.Int64(10),
        Ancestors = IndexedItem.AncestorsFromColumn(st.Text(11)),
        Version = st.Text(12),
        Extra = IndexedItem.ParseExtra(st.Text(13)),
        Body = withBody ? st.Text(14) : "",
    };

    /// <summary>Exécute une lecture sur la connexion de lecture, puis libère son instantané.</summary>
    private T Read<T>(Func<T> f)
    {
        lock (_rlock)
        {
            try { return f(); }
            finally { _rdb.ResetAll(); }
        }
    }

    public IndexedItem? Get(string id, bool withBody = true) => Read(() =>
    {
        var st = _rdb.Prepare($"SELECT {ItemColumns}, i.body FROM items i WHERE i.id = ?");
        st.Bind(1, id);
        return st.Step() ? ReadItem(st, withBody) : null;
    });

    /// <summary>Tous les éléments dont l'identifiant commence par <paramref name="prefix"/> (parcours de la clé, sans le texte par défaut).</summary>
    public List<IndexedItem> GetByIdPrefix(string prefix, bool withBody = false) => Read(() =>
    {
        var st = _rdb.Prepare($"SELECT {ItemColumns}, {(withBody ? "i.body" : "''")} FROM items i WHERE i.id >= ? AND i.id < ?");
        st.Bind(1, prefix);
        st.Bind(2, prefix + "\uffff");
        var list = new List<IndexedItem>();
        while (st.Step()) list.Add(ReadItem(st, withBody));
        return list;
    });

    public int Count(string? sourceId = null) => Read(() => sourceId is null
        ? _rdb.Scalar<int>("SELECT count(*) FROM items")
        : _rdb.Scalar<int>("SELECT count(*) FROM items WHERE source = ?", sourceId));

    public Dictionary<ItemKind, int> CountByKind() => Read(() =>
    {
        var d = new Dictionary<ItemKind, int>();
        var st = _rdb.Prepare("SELECT kind, count(*) FROM items GROUP BY kind");
        while (st.Step()) d[(ItemKind)st.Int64(0)] = (int)st.Int64(1);
        return d;
    });

    // ───────────────────────────── État des connecteurs ─────────────────────────────

    public string? GetState(string source, string key)
    {
        lock (_lock) return _db.Scalar<string>("SELECT value FROM state WHERE source = ? AND key = ?", source, key);
    }

    public void SetState(string source, string key, string? value)
    {
        lock (_lock)
        {
            if (value is null) _db.Execute("DELETE FROM state WHERE source = ? AND key = ?", source, key);
            else _db.Execute("INSERT OR REPLACE INTO state(source, key, value) VALUES (?, ?, ?)", source, key, value);
        }
    }

    public void ClearStatePrefix(string source, string keyPrefix)
    {
        lock (_lock) _db.Execute("DELETE FROM state WHERE source = ? AND substr(key, 1, length(?2)) = ?2", source, keyPrefix);
    }

    // Arborescence des conteneurs distants (dossiers OneDrive/SharePoint, dossiers Zimbra).

    public void SetTreeNode(string source, string id, string? parent, string name)
    {
        lock (_lock) _db.Execute("INSERT OR REPLACE INTO tree(source, id, parent, name) VALUES (?, ?, ?, ?)", source, id, parent, name);
    }

    public void DeleteTreeNode(string source, string id)
    {
        lock (_lock) _db.Execute("DELETE FROM tree WHERE source = ? AND id = ?", source, id);
    }

    public (string? Parent, string Name)? GetTreeNode(string source, string id)
    {
        lock (_lock)
        {
            var st = _db.Prepare("SELECT parent, name FROM tree WHERE source = ? AND id = ?");
            st.Bind(1, source); st.Bind(2, id);
            return st.Step() ? (st.IsNull(0) ? null : st.Text(0), st.Text(1)) : null;
        }
    }

    /// <summary>Remonte l'arborescence : renvoie la chaîne [racine … id] et les noms correspondants.</summary>
    public (List<string> Ids, List<string> Names) GetTreePath(string source, string id, int maxDepth = 64)
    {
        var ids = new List<string>(); var names = new List<string>();
        string? cur = id;
        var seen = new HashSet<string>();
        while (cur is not null && ids.Count < maxDepth && seen.Add(cur))
        {
            var n = GetTreeNode(source, cur);
            ids.Add(cur);
            if (n is null) { names.Add(""); break; }
            names.Add(n.Value.Name);
            cur = n.Value.Parent;
        }
        ids.Reverse(); names.Reverse();
        return (ids, names);
    }

    // Messages (Teams) : stockés un par un, regroupés en documents (fil, journée de conversation).

    public void UpsertMessage(string source, string id, string doc, DateTimeOffset ts, string author, string text, string webUrl)
    {
        lock (_lock) _db.Execute("INSERT OR REPLACE INTO messages(source, id, doc, ts, author, text, web_url) VALUES (?, ?, ?, ?, ?, ?, ?)",
            source, id, doc, ts.ToUnixTimeMilliseconds(), author, text, webUrl);
    }

    public string? DeleteMessage(string source, string id)
    {
        lock (_lock)
        {
            var doc = _db.Scalar<string>("SELECT doc FROM messages WHERE source = ? AND id = ?", source, id);
            _db.Execute("DELETE FROM messages WHERE source = ? AND id = ?", source, id);
            return doc;
        }
    }

    public List<(string Id, DateTimeOffset Ts, string Author, string Text, string WebUrl)> GetMessages(string source, string doc)
    {
        lock (_lock)
        {
            var list = new List<(string, DateTimeOffset, string, string, string)>();
            var st = _db.Prepare("SELECT id, ts, author, text, web_url FROM messages WHERE source = ? AND doc = ? ORDER BY ts");
            st.Bind(1, source); st.Bind(2, doc);
            while (st.Step()) list.Add((st.Text(0), DateTimeOffset.FromUnixTimeMilliseconds(st.Int64(1)), st.Text(2), st.Text(3), st.Text(4)));
            return list;
        }
    }

    public int DeleteMessagesOfDocPrefix(string source, string docPrefix)
    {
        lock (_lock) return _db.Execute("DELETE FROM messages WHERE source = ? AND substr(doc, 1, length(?2)) = ?2", source, docPrefix);
    }

    // ───────────────────────────── Vecteurs ─────────────────────────────

    public sealed record PendingEmbedding(long RowId, string Id, string Title, string Body);

    public List<PendingEmbedding> GetPendingEmbeddings(int limit)
    {
        lock (_lock)
        {
            var list = new List<PendingEmbedding>();
            var st = _db.Prepare("SELECT rowid, id, title, body FROM items WHERE embedded = 0 ORDER BY date DESC LIMIT ?");
            st.Bind(1, limit);
            while (st.Step()) list.Add(new(st.Int64(0), st.Text(1), st.Text(2), st.Text(3)));
            return list;
        }
    }

    public int PendingEmbeddingCount()
    {
        lock (_lock) return _db.Scalar<int>("SELECT count(*) FROM items WHERE embedded = 0");
    }

    public void SaveEmbeddings(long rowId, IReadOnlyList<(int Start, int Length, float[] Vector)> chunks)
    {
        lock (_lock)
        {
            using var _ = Batch();
            _db.Execute("DELETE FROM chunks WHERE item_rowid = ?", rowId);
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                _db.Execute("INSERT INTO chunks(item_rowid, seq, start, length, vec) VALUES (?, ?, ?, ?, ?)",
                    rowId, i, c.Start, c.Length, MemoryMarshal.AsBytes(c.Vector.AsSpan()).ToArray());
            }
            _db.Execute("UPDATE items SET embedded = 1 WHERE rowid = ?", rowId);
            Interlocked.Increment(ref _vecVersion);
        }
    }

    /// <summary>À appeler quand le modèle ou la dimension change : tout sera revectorisé.</summary>
    public void ResetEmbeddings()
    {
        lock (_lock)
        {
            _db.Exec("DELETE FROM chunks; UPDATE items SET embedded = 0;");
            Interlocked.Increment(ref _vecVersion); _vecCache = null;
        }
    }

    public string? GetMeta(string key) { lock (_lock) return _db.Scalar<string>("SELECT value FROM meta WHERE key = ?", key); }
    public void SetMeta(string key, string value) { lock (_lock) _db.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES (?, ?)", key, value); }

    private sealed class VectorCache
    {
        public long Version;
        public int Dim;
        public float[] Data = Array.Empty<float>();
        public long[] RowIds = Array.Empty<long>();
        public int[] Starts = Array.Empty<int>();
        public int[] Lengths = Array.Empty<int>();
        public Dictionary<long, (ItemKind Kind, long Date, string Source)> Meta = new();
    }

    /// <summary>
    /// Vecteurs en mémoire. Premier appel : chargement immédiat. Ensuite, si l'index a changé (vectorisation en cours),
    /// la recherche utilise la version en mémoire et un rechargement part en arrière-plan (au plus toutes les 20 s).
    /// </summary>
    private VectorCache? GetVectors()
    {
        var c = _vecCache;
        long version = Interlocked.Read(ref _vecVersion);
        if (c is null)
        {
            c = BuildVectorCache(version);
            _vecCache = c;
            _vecLastRefresh = DateTime.UtcNow;
            return c;
        }
        if (c.Version != version && DateTime.UtcNow - _vecLastRefresh >= VectorRefreshInterval
            && Interlocked.CompareExchange(ref _vecRefreshing, 1, 0) == 0)
        {
            _vecLastRefresh = DateTime.UtcNow;
            Task.Run(() =>
            {
                try { _vecCache = BuildVectorCache(Interlocked.Read(ref _vecVersion)); }
                catch (Exception) { /* on garde l'ancienne version */ }
                finally { Interlocked.Exchange(ref _vecRefreshing, 0); }
            });
        }
        return c;
    }

    /// <summary>Recharge les vecteurs (connexion dédiée : ne bloque ni la recherche ni la synchronisation).</summary>
    public void RefreshVectorsNow()
    {
        _vecCache = BuildVectorCache(Interlocked.Read(ref _vecVersion));
        _vecLastRefresh = DateTime.UtcNow;
    }

    private VectorCache BuildVectorCache(long version)
    {
        using var db = OpenReader(Path);
        var c = new VectorCache { Version = version };
        int count = db.Scalar<int>("SELECT count(*) FROM chunks");
        var st = db.Prepare("SELECT item_rowid, start, length, vec FROM chunks");
        float[]? data = null;
        var rows = new long[count]; var starts = new int[count]; var lens = new int[count];
        int n = 0;
        while (st.Step() && n < count)
        {
            var v = st.Floats(3);
            if (c.Dim == 0) { c.Dim = v.Length; data = new float[(long)count * c.Dim]; }
            if (v.Length != c.Dim || data is null) continue;
            v.CopyTo(data, (long)n * c.Dim);
            rows[n] = st.Int64(0); starts[n] = (int)st.Int64(1); lens[n] = (int)st.Int64(2);
            n++;
        }
        c.Data = data is null ? Array.Empty<float>() : (n == count ? data : data[..(n * c.Dim)]);
        c.RowIds = rows[..n]; c.Starts = starts[..n]; c.Lengths = lens[..n];
        var ms = db.Prepare("SELECT rowid, kind, date, source FROM items WHERE embedded = 1");
        while (ms.Step()) c.Meta[ms.Int64(0)] = ((ItemKind)ms.Int64(1), ms.Int64(2), ms.Text(3));
        return c;
    }

    // ───────────────────────────── Recherche ─────────────────────────────

    internal sealed record Candidate(long RowId, ItemKind Kind);

    private static long DateFloor(DateFilter f) => f switch
    {
        DateFilter.SeptJours => DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds(),
        DateFilter.TrenteJours => DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds(),
        DateFilter.UnAn => DateTimeOffset.UtcNow.AddYears(-1).ToUnixTimeMilliseconds(),
        _ => long.MinValue,
    };

    /// <summary>Recherche hybride : texte intégral (BM25) + sens (cosinus), fusion par rangs réciproques (RRF).</summary>
    public SearchResponse Search(SearchRequest req, float[]? queryVector, float minSimilarity = 0.35f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var parsed = QueryParser.Parse(req.Text);
        long floor = DateFloor(req.Date);
        VectorCache? vc = req.Semantic && queryVector is { Length: > 0 } && !parsed.HasFieldFilters && !parsed.IsEmpty ? GetVectors() : null;

        return Read(() =>
        {
            if (parsed.IsEmpty)
                return Browse(req, parsed, floor, sw);

            const double K = 60;
            var fused = new Dictionary<long, (double Score, ItemKind Kind, bool Text, bool Vec, int VecStart, int VecLen)>();

            // 1) Texte intégral
            if (parsed.FtsQuery.Length > 0)
            {
                var sql = $"""
                    SELECT i.rowid, i.kind FROM items_fts f JOIN items i ON i.rowid = f.rowid
                    WHERE items_fts MATCH ?1 AND i.date >= ?2 {(req.SourceId is null ? "" : "AND i.source = ?3")} {ExtFilterSql(parsed)}
                    ORDER BY bm25(items_fts, 8.0, 3.0, 1.5, 1.0) LIMIT 2000
                    """;
                var st = _rdb.Prepare(sql);
                st.Bind(1, parsed.FtsQuery); st.Bind(2, floor);
                if (req.SourceId is not null) st.Bind(3, req.SourceId);
                int rank = 0;
                try
                {
                    while (st.Step())
                    {
                        rank++;
                        fused[st.Int64(0)] = (1.0 / (K + rank), (ItemKind)st.Int64(1), true, false, 0, 0);
                    }
                }
                catch (SqliteException) { /* requête FTS invalide : on garde le sens seul */ }
            }
            // 2) Sens (vecteurs), uniquement sur des termes positifs.
            bool semanticUsed = false;
            if (vc is not null && queryVector is not null)
            {
                if (vc.Dim == queryVector.Length && vc.RowIds.Length > 0)
                {
                    semanticUsed = true;
                    var best = new Dictionary<long, (float Sim, int Start, int Len)>();
                    var q = queryVector.AsSpan();
                    for (int i = 0; i < vc.RowIds.Length; i++)
                    {
                        var rid = vc.RowIds[i];
                        if (!vc.Meta.TryGetValue(rid, out var m)) continue;
                        if (m.Date < floor) continue;
                        if (req.SourceId is not null && m.Source != req.SourceId) continue;
                        float sim = Dot(q, vc.Data.AsSpan(i * vc.Dim, vc.Dim));
                        if (sim < minSimilarity) continue;
                        if (!best.TryGetValue(rid, out var b) || sim > b.Sim) best[rid] = (sim, vc.Starts[i], vc.Lengths[i]);
                    }
                    int rank = 0;
                    foreach (var kv in best.OrderByDescending(x => x.Value.Sim).Take(300))
                    {
                        rank++;
                        double s = 1.0 / (K + rank);
                        if (fused.TryGetValue(kv.Key, out var f))
                            fused[kv.Key] = (f.Score + s, f.Kind, f.Text, true, kv.Value.Start, kv.Value.Len);
                        else
                            fused[kv.Key] = (s, vc.Meta[kv.Key].Kind, false, true, kv.Value.Start, kv.Value.Len);
                    }
                }
            }
            var counts = fused.Values.GroupBy(v => v.Kind).ToDictionary(g => g.Key, g => g.Count());
            var selected = fused
                .Where(kv => req.Kind is null || kv.Value.Kind == req.Kind)
                .OrderByDescending(kv => kv.Value.Score)
                .Take(req.Limit)
                .ToList();

            // Chargement groupé : une seule requête pour les éléments et le début de leur texte.
            // Les extraits sont calculés ici : snippet() de FTS5 est très lent avec des recherches par préfixe.
            var rowids = selected.Select(x => x.Key).ToList();
            var items = LoadByRowIds(rowids, out var bodies);

            var hits = new List<SearchHit>(selected.Count);
            foreach (var (rowid, info) in selected)
            {
                if (!items.TryGetValue(rowid, out var item)) continue;
                var body = bodies.GetValueOrDefault(rowid) ?? "";
                string snippet = info.Text ? Snippets.Make(body, parsed.Terms) : "";
                if (!snippet.Contains(SearchHit.HighlightStart))
                    snippet = info.Vec ? Snippets.Window(body, info.VecStart, Math.Min(info.VecLen, 260), parsed.Terms)
                                       : Snippets.Window(body, 0, 220, parsed.Terms);
                hits.Add(new SearchHit { Item = item, Snippet = snippet, Score = info.Score, MatchedText = info.Text, MatchedMeaning = info.Vec });
            }

            return new SearchResponse { Hits = hits, CountsByKind = counts, Elapsed = sw.Elapsed, SemanticUsed = semanticUsed };
        });
    }

    private static string ExtFilterSql(ParsedQuery p)
    {
        var parts = new List<string>();
        if (p.Extensions.Count > 0)
            parts.Add("AND i.ext IN (" + string.Join(",", p.Extensions.Select(e => "'" + e.Replace("'", "''") + "'")) + ")");
        return string.Join(" ", parts);
    }

    private SearchResponse Browse(SearchRequest req, ParsedQuery parsed, long floor, System.Diagnostics.Stopwatch sw)
    {
        string where = $"WHERE i.date >= ?1 {(req.SourceId is null ? "" : "AND i.source = ?2")} {ExtFilterSql(parsed)}";
        var counts = new Dictionary<ItemKind, int>();
        var cs = _rdb.Prepare($"SELECT i.kind, count(*) FROM items i {where} GROUP BY i.kind");
        cs.Bind(1, floor); if (req.SourceId is not null) cs.Bind(2, req.SourceId);
        while (cs.Step()) counts[(ItemKind)cs.Int64(0)] = (int)cs.Int64(1);

        var sql = $"SELECT {ItemColumns}, substr(i.body, 1, 240) FROM items i {where} {(req.Kind is null ? "" : "AND i.kind = ?3")} ORDER BY i.date DESC LIMIT ?4";
        var st = _rdb.Prepare(sql);
        st.Bind(1, floor);
        if (req.SourceId is not null) st.Bind(2, req.SourceId);
        if (req.Kind is not null) st.Bind(3, (int)req.Kind.Value);
        st.Bind(4, req.Limit);
        var hits = new List<SearchHit>();
        while (st.Step())
        {
            var it = ReadItem(st);
            hits.Add(new SearchHit { Item = it, Snippet = Clean(st.Text(14)) });
        }
        return new SearchResponse { Hits = hits, CountsByKind = counts, Elapsed = sw.Elapsed };
    }

    /// <summary>Taille du texte lu pour fabriquer un extrait (le début du document suffit presque toujours).</summary>
    private const int SnippetSource = 60_000;

    private Dictionary<long, IndexedItem> LoadByRowIds(List<long> rowids, out Dictionary<long, string> bodies)
    {
        var d = new Dictionary<long, IndexedItem>(rowids.Count);
        bodies = new Dictionary<long, string>(rowids.Count);
        if (rowids.Count == 0) return d;
        using var st = new SqliteStmt(_rdb,
            $"SELECT {ItemColumns}, substr(i.body, 1, {SnippetSource}) FROM items i WHERE i.rowid IN ({string.Join(',', rowids)})", cached: false);
        while (st.Step())
        {
            long id = st.Int64(0);
            d[id] = ReadItem(st);
            bodies[id] = st.Text(14);
        }
        return d;
    }

    private static string Clean(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; }
            else { sb.Append(ch); space = false; }
        }
        return sb.ToString().Trim();
    }

    internal static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        float sum = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && n >= System.Numerics.Vector<float>.Count)
        {
            var acc = System.Numerics.Vector<float>.Zero;
            int w = System.Numerics.Vector<float>.Count;
            for (; i <= n - w; i += w)
                acc += new System.Numerics.Vector<float>(a.Slice(i, w)) * new System.Numerics.Vector<float>(b.Slice(i, w));
            sum = System.Numerics.Vector.Sum(acc);
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    public void Optimize()
    {
        lock (_lock) _db.Exec("INSERT INTO items_fts(items_fts) VALUES('optimize'); PRAGMA optimize;");
    }

    public void Dispose()
    {
        lock (_rlock) _rdb.Dispose();
        lock (_lock) _db.Dispose();
    }
}
