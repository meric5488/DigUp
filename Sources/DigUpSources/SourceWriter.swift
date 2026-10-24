import Foundation

/// A file a source wants written for one of its items.
struct SourceFile {
    enum Content {
        case text(String)
        case data(Data)
        /// A file on disk to copy (a mail attachment, a note's picture).
        case copy(URL)
        /// A file already written into place by the provider (a photo's JPEG, an exported video).
        case written
    }

    /// The folders under the source's folder, already cleaned (`SourceNames.folder`).
    var folders: [String]
    /// The file name, already cleaned (`SourceNames.file`), with its extension.
    var name: String
    var content: Content
    var item: SourceItem
}

/// Names that are safe and readable on disk, and say what the file is (the engine titles each passage with the file
/// name, and searches it by keyword).
enum SourceNames {
    /// "Re: Devis / plan" → "Re - Devis - plan": no slashes or colons, no leading dot, at most `limit` characters.
    static func file(_ stem: String, extension ext: String, limit: Int = 120) -> String {
        var cleaned = clean(stem)
        if cleaned.count > limit { cleaned = String(cleaned.prefix(limit)).trimmingCharacters(in: .whitespaces) + "…" }
        if cleaned.isEmpty { cleaned = "Untitled" }
        return ext.isEmpty ? cleaned : cleaned + "." + ext
    }

    /// A folder name: as `file`, and no dots at all, so no folder ever looks like a bundle ("Projet.app") to the crawl.
    static func folder(_ name: String) -> String {
        var cleaned = clean(name).replacingOccurrences(of: ".", with: "\u{2024}")   // one-dot leader
        if cleaned.count > 80 { cleaned = String(cleaned.prefix(80)).trimmingCharacters(in: .whitespaces) }
        return cleaned.isEmpty ? "Other" : cleaned
    }

    private static func clean(_ text: String) -> String {
        var result = ""
        result.reserveCapacity(text.count)
        var lastWasSpace = false
        for character in text {
            var piece: String
            switch character {
            case "/", ":", "\\": piece = " - "
            case "\n", "\r", "\t": piece = " "
            case "\u{0}"..."\u{1F}", "\u{7F}", "\u{FFFC}", "\u{200B}": piece = ""
            case "\"": piece = "'"
            case "<", ">", "|", "?", "*": piece = ""
            default: piece = String(character)
            }
            if piece == " " || piece == " - " {
                if lastWasSpace { piece = piece == " - " ? "- " : "" }
                lastWasSpace = true
            } else if !piece.isEmpty {
                lastWasSpace = false
            }
            result += piece
        }
        var trimmed = result.trimmingCharacters(in: .whitespaces)
        while trimmed.hasPrefix(".") || trimmed.hasPrefix("-") {
            trimmed = String(trimmed.dropFirst()).trimmingCharacters(in: .whitespaces)
        }
        while trimmed.hasSuffix(".") { trimmed = String(trimmed.dropLast()).trimmingCharacters(in: .whitespaces) }
        return trimmed
    }

    /// "2024-07-14", "2024-07-14 10.32" for names: sortable, and the crawl's dataset rule never takes a name that starts
    /// with a date for a machine-made one.
    static func day(_ date: Date) -> String { dayFormatter.string(from: date) }
    static func minute(_ date: Date) -> String { minuteFormatter.string(from: date) }
    static func year(_ date: Date) -> String { String(Calendar.current.component(.year, from: date)) }
    static func month(_ date: Date) -> String {
        String(format: "%02d", Calendar.current.component(.month, from: date))
    }

    /// "14 July 2024 at 10:32", in the Mac's language, for the text of a file.
    static func readable(_ date: Date, time: Bool = true) -> String {
        date.formatted(date: .long, time: time ? .shortened : .omitted)
    }

    private static let dayFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter
    }()

    private static let minuteFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd HH.mm.ss"
        return formatter
    }()
}

/// Writes, replaces and removes a source's files, and keeps the catalog in step with them. Used on the sync queue.
final class SourceWriter {
    let kind: SourceKind
    let folder: URL
    private let catalog: SourceCatalog
    private let fm = FileManager.default
    /// Relative paths taken in this sync, so two items with the same name get "(2)", "(3)".
    private var taken = Set<String>()

    init(kind: SourceKind, catalog: SourceCatalog) {
        self.kind = kind
        self.catalog = catalog
        folder = SourcesLayout.folder(kind)
        try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    /// Where a provider writes a file itself (`SourceFile.Content.written`): a free path for the name, reserved.
    func reservePath(folders: [String], name: String) -> (relative: String, url: URL) {
        let directory = folders.reduce(folder) { $0.appendingPathComponent($1, isDirectory: true) }
        try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
        let stem = (name as NSString).deletingPathExtension, ext = (name as NSString).pathExtension
        var candidate = name
        var number = 2
        while true {
            let url = directory.appendingPathComponent(candidate)
            let relative = SourcesLayout.relativePath(of: url.path) ?? url.path
            if !taken.contains(relative), !fm.fileExists(atPath: url.path) {
                taken.insert(relative)
                return (relative, url)
            }
            candidate = ext.isEmpty ? "\(stem) (\(number))" : "\(stem) (\(number)).\(ext)"
            number += 1
        }
    }

    /// Replaces an item's files with these (none: the options leave it out) and records its stamp.
    func put(nativeID: String, stamp: String, files: [SourceFile], reserved: [String: URL] = [:]) {
        removeFiles(of: nativeID)
        var recorded: [(path: String, item: SourceItem)] = []
        for file in files {
            let target: (relative: String, url: URL)
            if case .written = file.content, let url = reserved[file.name] {
                guard let relative = SourcesLayout.relativePath(of: url.path) else { continue }
                target = (relative, url)
            } else {
                target = reservePath(folders: file.folders, name: file.name)
            }
            do {
                switch file.content {
                case .text(let text): try Data(text.utf8).write(to: target.url, options: .atomic)
                case .data(let data): try data.write(to: target.url, options: .atomic)
                case .copy(let source):
                    if fm.fileExists(atPath: target.url.path) { try fm.removeItem(at: target.url) }
                    try fm.copyItem(at: source, to: target.url)
                case .written: break
                }
                if let date = file.item.date {
                    // The file's date is the item's: the engine shows it, and sorting by date makes sense.
                    try? fm.setAttributes([.modificationDate: date], ofItemAtPath: target.url.path)
                }
                recorded.append((target.relative, file.item))
            } catch {
                SourceLog.write("\(kind.rawValue): can't write \(target.relative): \(error.localizedDescription)")
            }
        }
        catalog.record(kind: kind, nativeID: nativeID, stamp: stamp, files: recorded)
    }

    /// Removes an item's current files before a provider writes new ones itself (`reservePath`), so the new ones can
    /// take the same names.
    func clear(_ nativeID: String) {
        removeFiles(of: nativeID)
    }

    /// Removes the files of items that are gone from their app, and forgets them.
    func remove(_ ids: [String]) {
        guard !ids.isEmpty else { return }
        for id in ids { removeFiles(of: id) }
        catalog.forget(kind: kind, ids: ids)
    }

    private func removeFiles(of nativeID: String) {
        for relative in catalog.paths(of: kind, ids: [nativeID])[nativeID] ?? [] {
            let url = SourcesLayout.root.appendingPathComponent(relative)
            try? fm.removeItem(at: url)
            taken.remove(relative)
            removeEmptyFolders(from: url.deletingLastPathComponent())
        }
    }

    /// Folders left empty by removals go too, up to the source's folder.
    private func removeEmptyFolders(from start: URL) {
        var directory = start.standardizedFileURL
        while directory.path.hasPrefix(folder.path + "/"),
              let names = try? fm.contentsOfDirectory(atPath: directory.path),
              names.allSatisfy({ $0 == ".DS_Store" }) {
            try? fm.removeItem(at: directory)
            directory = directory.deletingLastPathComponent()
        }
    }
}
