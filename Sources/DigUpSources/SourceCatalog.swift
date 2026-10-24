import DigUpKit
import Foundation

/// One file under `SourcesLayout.root` and what it stands for.
public struct SourceItem: Sendable, Equatable {
    public let kind: SourceKind
    /// The item's identity in its app: a mail's file in ~/Library/Mail, a note's identifier, an event's identifier, a
    /// reminder's identifier, a photo's local identifier. A mail and its attachments share it.
    public let nativeID: String
    /// What a result shows instead of the file name: a mail's subject, a note's title, an event's title.
    public let title: String
    /// What a result shows instead of the folder: "Mail · INBOX · Jean Dupont", "Calendar · Work · 14 Jul 2024".
    public let subtitle: String
    /// Opens the item in its app (`message://…`, `ical://…`, `x-apple-reminderkit://…`), or nil.
    public let openURL: String?
    /// An AppleScript that shows the item in its app, for apps without a link that does (Photos, Notes). Tried first.
    public let openScript: String?
    /// When it happened: received, modified, starts, due, taken.
    public let date: Date?

    public init(kind: SourceKind, nativeID: String, title: String, subtitle: String, openURL: String?,
                openScript: String? = nil, date: Date?) {
        self.kind = kind
        self.nativeID = nativeID
        self.title = title
        self.subtitle = subtitle
        self.openURL = openURL
        self.openScript = openScript
        self.date = date
    }
}

/// What every file under `SourcesLayout.root` stands for, and which version of each item was last written.
///
/// Two tables in `SourcesLayout.catalogFile`:
/// - `stamps`: every item a sync has handled (written, or left out by the options), with the stamp it had then (a
///   modification date, a size), so the next sync only reads what changed;
/// - `files`: each file written for an item (a mail and its attachments are several files of one item).
///
/// Results look files up by path many times a second (`item(atPath:)`), so `files` is also kept in memory. Every
/// access, from any thread, goes through `lock`.
public final class SourceCatalog: @unchecked Sendable {
    public static let shared = SourceCatalog(file: SourcesLayout.catalogFile)

    private let file: URL
    private let lock = NSLock()
    private var db: Database?
    private var loaded = false
    /// Relative path → item.
    private var byPath: [String: SourceItem] = [:]

    init(file: URL) {
        self.file = file
    }

    // MARK: Lookups (any thread)

    /// The item a file under `SourcesLayout.root` stands for, or nil for any other file.
    public func item(atPath path: String) -> SourceItem? {
        guard let relative = SourcesLayout.relativePath(of: path) else { return nil }
        return lock.withLock {
            loadIfNeeded()
            return byPath[relative]
        }
    }

    /// How many items each source has files for.
    public func counts() -> [SourceKind: Int] {
        lock.withLock {
            loadIfNeeded()
            var ids: [SourceKind: Set<String>] = [:]
            for item in byPath.values { ids[item.kind, default: []].insert(item.nativeID) }
            return ids.mapValues(\.count)
        }
    }

    // MARK: Syncing (the sync queue)

    /// The stamp each item of `kind` had when it was last handled.
    func stamps(of kind: SourceKind) -> [String: String] {
        lock.withLock {
            var stamps: [String: String] = [:]
            guard let db = openDatabase() else { return stamps }
            try? db.query("SELECT native_id, stamp FROM stamps WHERE kind = ?", [.text(kind.rawValue)]) { row in
                if let id = row.text(0) { stamps[id] = row.text(1) ?? "" }
            }
            return stamps
        }
    }

    /// The relative paths of the files written for these items.
    func paths(of kind: SourceKind, ids: [String]) -> [String: [String]] {
        guard !ids.isEmpty else { return [:] }
        return lock.withLock {
            loadIfNeeded()
            let wanted = Set(ids)
            var paths: [String: [String]] = [:]
            for (path, item) in byPath where item.kind == kind && wanted.contains(item.nativeID) {
                paths[item.nativeID, default: []].append(path)
            }
            return paths
        }
    }

    /// Records that an item was handled with this stamp, and the files written for it (none when the options left it
    /// out). Files recorded for it before are forgotten: the caller has removed them.
    func record(kind: SourceKind, nativeID: String, stamp: String, files: [(path: String, item: SourceItem)]) {
        lock.withLock {
            loadIfNeeded()
            guard let db = openDatabase() else { return }
            do {
                try db.transaction {
                    try db.run("DELETE FROM files WHERE kind = ? AND native_id = ?", [.text(kind.rawValue), .text(nativeID)])
                    try db.run("INSERT OR REPLACE INTO stamps(kind, native_id, stamp) VALUES(?, ?, ?)",
                               [.text(kind.rawValue), .text(nativeID), .text(stamp)])
                    for file in files {
                        try db.run("""
                            INSERT OR REPLACE INTO files(path, kind, native_id, title, subtitle, open_url, open_script, date)
                            VALUES(?, ?, ?, ?, ?, ?, ?, ?)
                            """, [.text(file.path), .text(kind.rawValue), .text(nativeID), .text(file.item.title),
                                  .text(file.item.subtitle), Self.value(file.item.openURL),
                                  Self.value(file.item.openScript),
                                  file.item.date.map { .double($0.timeIntervalSince1970) } ?? .null])
                    }
                }
                for (path, item) in byPath where item.kind == kind && item.nativeID == nativeID {
                    byPath[path] = nil
                }
                for file in files { byPath[file.path] = file.item }
            } catch {
                SourceLog.write("catalog: \(error)")
            }
        }
    }

    /// Forgets items that are gone from their app (the caller has removed their files).
    func forget(kind: SourceKind, ids: [String]) {
        guard !ids.isEmpty else { return }
        lock.withLock {
            loadIfNeeded()
            guard let db = openDatabase() else { return }
            do {
                try db.transaction {
                    for id in ids {
                        try db.run("DELETE FROM files WHERE kind = ? AND native_id = ?", [.text(kind.rawValue), .text(id)])
                        try db.run("DELETE FROM stamps WHERE kind = ? AND native_id = ?", [.text(kind.rawValue), .text(id)])
                    }
                }
                let gone = Set(ids)
                for (path, item) in byPath where item.kind == kind && gone.contains(item.nativeID) {
                    byPath[path] = nil
                }
            } catch {
                SourceLog.write("catalog: \(error)")
            }
        }
    }

    /// Forgets a whole source (it was turned off; the caller has removed its folder).
    func forgetAll(_ kind: SourceKind) {
        lock.withLock {
            loadIfNeeded()
            guard let db = openDatabase() else { return }
            try? db.transaction {
                try db.run("DELETE FROM files WHERE kind = ?", [.text(kind.rawValue)])
                try db.run("DELETE FROM stamps WHERE kind = ?", [.text(kind.rawValue)])
            }
            for (path, item) in byPath where item.kind == kind { byPath[path] = nil }
        }
    }

    // MARK: Storage (under `lock`)

    private static func value(_ text: String?) -> SQLValue { text.map { .text($0) } ?? .null }

    private func openDatabase() -> Database? {
        if let db { return db }
        do {
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            let db = try Database(path: file.path)
            try db.execute("""
                CREATE TABLE IF NOT EXISTS stamps(
                    kind TEXT NOT NULL,
                    native_id TEXT NOT NULL,
                    stamp TEXT NOT NULL,
                    PRIMARY KEY(kind, native_id)
                );
                CREATE TABLE IF NOT EXISTS files(
                    path TEXT PRIMARY KEY,      -- relative to SourcesLayout.root
                    kind TEXT NOT NULL,
                    native_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    subtitle TEXT NOT NULL,
                    open_url TEXT,
                    open_script TEXT,
                    date REAL
                );
                CREATE INDEX IF NOT EXISTS files_item ON files(kind, native_id);
                """)
            self.db = db
            return db
        } catch {
            SourceLog.write("catalog: can't open \(file.path): \(error)")
            return nil
        }
    }

    private func loadIfNeeded() {
        guard !loaded else { return }
        loaded = true
        guard let db = openDatabase() else { return }
        try? db.query("SELECT path, kind, native_id, title, subtitle, open_url, open_script, date FROM files") { row in
            guard let path = row.text(0), let kind = row.text(1).flatMap(SourceKind.init(rawValue:)) else { return }
            byPath[path] = SourceItem(kind: kind, nativeID: row.text(2) ?? "", title: row.text(3) ?? "",
                                      subtitle: row.text(4) ?? "", openURL: row.text(5), openScript: row.text(6),
                                      date: row.isNull(7) ? nil : Date(timeIntervalSince1970: row.double(7)))
        }
    }
}

/// The sources' log lines go to the app's log (the app sets `SourceLog.sink`), or to stderr.
public enum SourceLog {
    nonisolated(unsafe) public static var sink: (@Sendable (String) -> Void)?

    static func write(_ line: String) {
        if let sink { sink("sources: " + line) } else { FileHandle.standardError.write(Data(("sources: " + line + "\n").utf8)) }
    }
}
