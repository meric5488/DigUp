import AppKit
import DigUpKit

/// Fouineur was DigUp (com.abdurrahim.DigUp, ~/Library/Application Support/DigUp) until it got a name and an identity
/// of its own. Its first launch takes DigUp's settings and folder along (the index, the model, the sources' copies), so
/// nothing is read, indexed or downloaded again. DigUp's own are left as they were.
enum Migration {
    static let oldBundleID = "com.abdurrahim.DigUp"
    static let oldSupport = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/DigUp", isDirectory: true)

    /// Runs once, before the settings load. Returns what it did, for the log (which opens later).
    static func fromDigUp() -> [String] {
        let defaults = UserDefaults.standard
        // Dev runs have their own defaults and folders.
        guard defaults.string(forKey: "defaultsSuite") == nil, !defaults.bool(forKey: "migratedFromDigUp") else {
            return []
        }
        var done: [String] = []
        let new = AppPaths.applicationSupport

        if let old = defaults.persistentDomain(forName: oldBundleID) {
            var copied = 0
            for (key, value) in old where !isDigUpsOwn(key) && defaults.object(forKey: key) == nil {
                defaults.set(value, forKey: key)
                copied += 1
            }
            done.append("migration: \(copied) settings from \(oldBundleID)")
        }
        // A custom index or models folder inside DigUp's folder moves with it.
        for key in ["indexDir", "modelsDir"] {
            if let path = defaults.string(forKey: key), let moved = moved(path, to: new) { defaults.set(moved, forKey: key) }
        }

        let fm = FileManager.default
        if fm.fileExists(atPath: oldSupport.path), !fm.fileExists(atPath: new.path) {
            // DigUp (or this fork before it was renamed) writes there: it quits first, so the copy is whole.
            quitDigUp()
            do {
                try fm.copyItem(at: oldSupport, to: new)   // a clone on APFS: no extra space
                done.append("migration: copied \(tildePath(oldSupport.path)) to \(tildePath(new.path))")
                let index = defaults.string(forKey: "indexDir").map { URL(fileURLWithPath: $0) }
                    ?? new.appendingPathComponent("index.noindex")
                for file in [IndexStore.mainFile, IndexStore.codeFile] {
                    let url = index.appendingPathComponent(file)
                    guard fm.fileExists(atPath: url.path) else { continue }
                    do {
                        let count = try rewritePaths(in: url, to: new)
                        done.append("migration: \(count) paths moved in \(file)")
                    } catch {
                        done.append("migration: can't move the paths in \(file): \(error)")
                    }
                }
            } catch {
                done.append("migration: can't copy \(tildePath(oldSupport.path)): \(error)")
            }
        }
        defaults.set(true, forKey: "migratedFromDigUp")
        return done
    }

    /// Settings that belong to DigUp's copy and not to Fouineur: Sparkle's (its updates), the menu bar item's place.
    private static func isDigUpsOwn(_ key: String) -> Bool {
        key.hasPrefix("SU") || key.hasPrefix("NSStatusItem") || key == "migratedFromDigUp"
    }

    private static func moved(_ path: String, to new: URL) -> String? {
        let old = oldSupport.path
        guard path == old || path.hasPrefix(old + "/") else { return nil }
        return new.path + path.dropFirst(old.count)
    }

    /// The index's files under DigUp's folder (the sources' copies) are the same files under Fouineur's.
    private static func rewritePaths(in file: URL, to new: URL) throws -> Int {
        let db = try Database(path: file.path)
        let from = oldSupport.path + "/"
        var count = 0
        try db.query("SELECT count(*) FROM files WHERE path > ? AND path < ?",
                     [.text(from), .text(oldSupport.path + "0")]) { count = Int($0.int(0)) }
        try db.run("UPDATE files SET path = ? || substr(path, ?) WHERE path > ? AND path < ?",
                   [.text(new.path + "/"), .int(Int64(from.unicodeScalars.count + 1)), .text(from),
                    .text(oldSupport.path + "0")])
        return count
    }

    private static func quitDigUp() {
        let running = NSRunningApplication.runningApplications(withBundleIdentifier: oldBundleID)
        guard !running.isEmpty else { return }
        running.forEach { $0.terminate() }
        let deadline = Date().addingTimeInterval(15)
        while running.contains(where: { !$0.isTerminated }), Date() < deadline {
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
    }
}
