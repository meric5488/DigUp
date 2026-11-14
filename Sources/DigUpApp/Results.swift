import AppKit
import DigUpKit
import DigUpSources

/// One search result as the panel and the window show it: a file, the part of it that matched, and why.
struct ResultRow: Identifiable, Equatable {
    let id: String          // the file's path
    let url: URL
    let name: String
    let folder: String      // "~/Downloads/Screenshots"
    let kind: FileKind
    let segment: SegmentKind
    let segmentID: Int64
    let loc: Double?        // PDF page, seconds into audio or video, or a stretch of code's first line
    let locEnd: Double?
    /// Code: the line to open at (where the name is defined, or the first line with the query's words).
    let focus: Double?
    let excerpt: String?    // matched words are wrapped in « »
    let matchedMeaning: Bool
    let matchedWords: Bool
    let modified: Date
    let size: Int64
    let info: [String: String]
    /// The query's words (`Searcher.terms`): what the preview highlights on a page or in a passage.
    let terms: [String]
    /// A mail, note, event, reminder or photo (a file in the sources' folder stands for it): shown by its subject or
    /// title, and opened in its app.
    let source: SourceItem?

    init(_ hit: SearchHit, terms: [String] = []) {
        id = hit.path
        url = URL(fileURLWithPath: hit.path)
        source = SourceCatalog.shared.item(atPath: hit.path)
        name = source?.title ?? url.lastPathComponent
        if let source {
            // "Mail · INBOX · Jean Dupont" where a file shows its folder.
            folder = Self.frenchSubtitle(source.subtitle)
        } else if hit.kind == .code, let repo = hit.info["repo"], hit.path.hasPrefix(repo + "/") {
            // Code says where it is in its repo: "billing/payments".
            let parent = (repo as NSString).deletingLastPathComponent
            folder = String(url.deletingLastPathComponent().path.dropFirst(parent.count + 1))
        } else {
            folder = tildePath(url.deletingLastPathComponent().path)
        }
        kind = hit.kind
        segment = hit.segment
        segmentID = hit.segmentID
        loc = hit.loc
        locEnd = hit.locEnd
        focus = hit.focus
        excerpt = hit.excerpt.flatMap { $0.isEmpty ? nil : $0 }
        matchedMeaning = (hit.z ?? 0) >= 2
        matchedWords = hit.keywordRank != nil
        modified = Date(timeIntervalSince1970: hit.modified)
        size = hit.size
        info = hit.info
        self.terms = terms
    }

    /// An event or a reminder: the window lists these in a table (`AgendaTable`), not as pictures of their text.
    var isAgenda: Bool { source?.kind == .calendar || source?.kind == .reminders }

    /// The table's columns, from the item's subtitle ("Calendar · Work · 14 July 2024 at 10:30", "Reminders ·
    /// Groceries · due 3 May 2025"): when, which calendar or list, and whether a reminder is done.
    var agenda: (when: String, list: String, status: String) {
        let parts = (source?.subtitle ?? "").components(separatedBy: " · ")
        let list = parts.count > 1 ? parts[1] : ""
        let last = parts.count > 2 ? parts[2...].joined(separator: " · ") : ""
        guard source?.kind == .reminders else { return (last, list, "") }
        if last.hasPrefix("due ") { return (String(last.dropFirst(4)), list, "À faire") }
        return ("", list, last == "done" ? "Terminé" : "À faire")
    }

    /// The sources write their subtitles in English ("Reminders · Groceries · due 3 May 2025"), and `agenda` reads
    /// them that way: translated only where they're shown.
    static func frenchSubtitle(_ subtitle: String) -> String {
        let names = ["Calendar": "Calendrier", "Reminders": "Rappels", "Mail attachment": "Pièce jointe Mail",
                     "Notes attachment": "Pièce jointe Notes"]
        var parts = subtitle.components(separatedBy: " · ")
        guard let first = parts.first else { return subtitle }
        if first == "Reminders", let last = parts.last, parts.count > 2 {
            parts[parts.count - 1] = last == "done" ? "terminé" : last == "open" ? "à faire"
                : last.hasPrefix("due ") ? "échéance " + last.dropFirst(4) : last
        }
        parts[0] = names[first] ?? first
        return parts.joined(separator: " · ")
    }

    /// The name to show: left to right as Finder shows it, also when it starts in Arabic ("ملاحظة.md", not "md.ملاحظة").
    var title: String { "\u{200E}" + name }

    /// The words keyword search found in the excerpt (marked « »), as they're written there.
    var foundWords: [String] {
        guard let excerpt else { return [] }
        var words: [String] = []
        var rest = excerpt[...]
        while let open = rest.firstIndex(of: "«"), let close = rest[open...].firstIndex(of: "»") {
            let word = String(rest[rest.index(after: open)..<close])
            if !word.isEmpty, !words.contains(word) { words.append(word) }
            rest = rest[rest.index(after: close)...]
        }
        return words
    }

    /// The page or moment that matched, as a badge: "p. 3", "0:06", "♪ 0:25" (a video's sound), "L12–40" (code).
    var place: String? {
        guard let loc else { return nil }
        switch segment {
        case .page, .pageImage: return "p. \(Int(loc))"
        case .frame: return Self.clock(loc)
        case .audio: return kind == .video ? "♪ \(Self.clock(loc))" : Self.clock(loc)
        case .lines: return Int(loc) == Int(locEnd ?? loc) ? "L\(Int(loc))" : "L\(Int(loc))–\(Int(locEnd ?? loc))"
        case .cells: return span
        default: return nil
        }
    }

    /// The lines (or a notebook's cells) of code that matched: "lines 12–40", "cell 3".
    var span: String? {
        guard let loc else { return nil }
        let (first, last) = (Int(loc), Int(locEnd ?? loc))
        switch segment {
        case .lines: return first == last ? "ligne \(first)" : "lignes \(first)–\(last)"
        case .cells: return first == last ? "cellule \(first)" : "cellules \(first)–\(last)"
        default: return nil
        }
    }

    /// What the preview shows: the moment or the page that matched, else the file.
    var previewKey: String { "\(id)|\(loc.map { "\($0)" } ?? "")" }

    var duration: Double? { info["duration"].flatMap(Double.init) }

    /// Why it's here, in a few words: "Matches the picture", "Matches the frame at 0:06".
    var reason: String {
        guard matchedMeaning else { return matchedWords ? "Contient tes mots" : "Correspond à son nom" }
        let place = loc.map(Self.clock) ?? ""
        let page = loc.map { "la page \(Int($0))" } ?? "une page"
        switch segment {
        case .image: return kind == .screenshot ? "Correspond à ce qui est à l'écran" : "Correspond à l'image"
        case .ocr: return "Correspond au texte qu'elle contient"
        case .frame: return "Correspond à l'image à \(place)"
        case .audio: return kind == .video ? "Correspond au son à \(place)" : "Correspond à ce qu'on entend à \(place)"
        case .page: return "Correspond au texte de \(page)"
        case .pageImage: return "Correspond à \(page)"
        case .chunk: return "Correspond à son texte"
        case .name: return "Correspond à son nom"
        case .lines, .cells: return "Correspond " + (span.map { $0.contains("–") ? "aux \($0)" : "à la \($0)" } ?? "au code")
        }
    }

    /// "Screenshot · Oct 1, 2026 · 1440 × 900 · 2.1 MB", "PDF · … · 463 pages, 120 read so far · 23 MB",
    /// "Python · 63 lines · Oct 1, 2026 · 2 KB"
    var details: String {
        if kind == .code {
            var parts = [CodeExtractor.language(of: url.path)]
            if let cells = info["cells"].flatMap(Int.init) {
                parts.append(cells == 1 ? "1 cellule" : "\(cells) cellules")
            } else if let lines = info["lines"].flatMap(Int.init) {
                parts.append(lines == 1 ? "1 ligne" : "\(lines.formatted()) lignes")
            }
            parts.append(modified.formatted(date: .abbreviated, time: .omitted))
            if size > 0 { parts.append(bytes(size)) }
            return parts.joined(separator: " · ")
        }
        var parts = [kind.title, modified.formatted(date: .abbreviated, time: .omitted)]
        if let width = info["width"], let height = info["height"] { parts.append("\(width) × \(height)") }
        // A long file still being read (`Indexer.finishLongFiles`): what isn't read yet can't match.
        let readTo = info["read_to"].flatMap(Int.init)
        if let pages = info["pages"].flatMap(Int.init) {
            parts.append((pages == 1 ? "1 page" : "\(pages) pages") + (readTo.map { ", \($0) lues pour l'instant" } ?? ""))
        } else if readTo != nil {
            parts.append("lu en partie pour l'instant")
        }
        if let duration { parts.append(Self.clock(duration)) }
        if size > 0 { parts.append(bytes(size)) }
        return parts.joined(separator: " · ")
    }

    static func clock(_ seconds: Double) -> String {
        let total = Int(seconds)
        return total >= 3600 ? String(format: "%d:%02d:%02d", total / 3600, total / 60 % 60, total % 60)
                             : String(format: "%d:%02d", total / 60, total % 60)
    }
}

/// How results are grouped in the panel, and the kind filters in the window.
enum ResultGroupKind: CaseIterable {
    case screenshots, images, documents, video, audio
    /// Code search's results (the code index; a search of it never shows anything else).
    case code
    /// The sources' items (by where their files are, `folders`): mail, notes, events, reminders, the Photos library's
    /// pictures, and iCloud Drive's files.
    case mail, notes, calendar, reminders, photos, icloud

    /// The kinds of files the main index holds: the window's kind filters, ⌘2 to ⌘6.
    static let files: [ResultGroupKind] = [.screenshots, .images, .documents, .video, .audio]
    /// The sources' filters, after the kinds' (those of the sources that are on).
    static let sources: [ResultGroupKind] = [.mail, .notes, .calendar, .reminders, .photos, .icloud]

    init(_ source: SourceKind) {
        switch source {
        case .mail: self = .mail
        case .notes: self = .notes
        case .calendar: self = .calendar
        case .reminders: self = .reminders
        case .photos: self = .photos
        case .icloud: self = .icloud
        }
    }

    /// A row's group: its source's when it stands for a source's item, else its kind's.
    init(_ row: ResultRow) {
        self = row.source.map { Self($0.kind) } ?? Self(row.kind)
    }

    var source: SourceKind? {
        switch self {
        case .mail: .mail
        case .notes: .notes
        case .calendar: .calendar
        case .reminders: .reminders
        case .photos: .photos
        case .icloud: .icloud
        default: nil
        }
    }

    /// A source's files are those under these folders (nil: a kind, any folder).
    var folders: [String]? {
        guard let source else { return nil }
        return source == .icloud ? SourcesLayout.iCloudPaths() : [SourcesLayout.folder(source).path]
    }

    init(_ kind: FileKind) {
        switch kind {
        case .screenshot: self = .screenshots
        case .image: self = .images
        case .pdf, .doc: self = .documents
        case .video: self = .video
        case .audio: self = .audio
        case .code: self = .code
        }
    }

    var title: String {
        switch self {
        case .screenshots: "Captures d'écran"
        case .images: "Images"
        case .documents: "Documents"
        case .video: "Vidéo"
        case .audio: "Audio"
        case .code: "Code"
        case .mail, .notes, .calendar, .reminders, .photos, .icloud: source!.title
        }
    }

    var symbol: String {
        switch self {
        case .screenshots: FileKind.screenshot.symbol
        case .images: FileKind.image.symbol
        case .documents: FileKind.doc.symbol
        case .video: FileKind.video.symbol
        case .audio: FileKind.audio.symbol
        case .code: FileKind.code.symbol
        case .mail, .notes, .calendar, .reminders, .photos, .icloud: source!.symbol
        }
    }

    /// The kinds of files it is (nil: a source, any kind).
    var kinds: Set<FileKind>? {
        switch self {
        case .screenshots: [.screenshot]
        case .images: [.image]
        case .documents: [.pdf, .doc]
        case .video: [.video]
        case .audio: [.audio]
        case .code: [.code]
        case .mail, .notes, .calendar, .reminders, .photos, .icloud: nil
        }
    }
}

struct ResultGroup: Identifiable, Equatable {
    let kind: ResultGroupKind
    let rows: [ResultRow]
    /// Strong results of this kind, shown or not (the panel shows a few).
    var total: Int
    var id: ResultGroupKind { kind }
}

/// Search results the way they're shown: the strong ones grouped by kind (the best hit's group first, as many rows per
/// group as there's room for), and the weaker ones (below half the top score) folded underneath.
struct ResultSet: Equatable {
    var groups: [ResultGroup] = []
    /// The strong ones in score order (the window's grid; the panel shows `groups`).
    var ranked: [ResultRow] = []
    var weaker: [ResultRow] = []

    static let empty = ResultSet()

    init() {}

    init(_ hits: [SearchHit], query: String = "", rowsPerGroup: Int = .max) {
        let strong = Searcher.strongCount(hits)
        let terms = Searcher.terms(query)
        var order: [ResultGroupKind] = []
        var byGroup: [ResultGroupKind: [ResultRow]] = [:]
        ranked = hits.prefix(strong).map { ResultRow($0, terms: terms) }
        for row in ranked {
            let group = ResultGroupKind(row)
            if byGroup[group] == nil { order.append(group) }
            byGroup[group, default: []].append(row)
        }
        groups = order.map {
            ResultGroup(kind: $0, rows: Array(byGroup[$0]!.prefix(rowsPerGroup)), total: byGroup[$0]!.count)
        }
        weaker = hits.dropFirst(strong).map { ResultRow($0, terms: terms) }
    }

    var strongRows: [ResultRow] { groups.flatMap(\.rows) }
    var isEmpty: Bool { groups.isEmpty && weaker.isEmpty }
    var count: Int { strongRows.count + weaker.count }
}

extension FileKind {
    var title: String {
        switch self {
        case .screenshot: "Capture d'écran"
        case .image: "Image"
        case .pdf: "PDF"
        case .doc: "Document"
        case .audio: "Audio"
        case .video: "Vidéo"
        case .code: "Code"
        }
    }

    var symbol: String {
        switch self {
        case .screenshot: "camera.viewfinder"
        case .image: "photo"
        case .pdf: "doc.richtext"
        case .doc: "doc.text"
        case .audio: "waveform"
        case .video: "film"
        case .code: "chevron.left.forwardslash.chevron.right"
        }
    }
}
