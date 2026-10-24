import Foundation

/// The Apple apps whose content DigUp can search besides your folders.
///
/// Each source is turned into ordinary files in a folder of its own (`SourcesLayout.root`), which the engine indexes
/// like any chosen folder: a mail becomes a text file (and its attachments files beside it), a photo a JPEG, an event
/// or a reminder a short text. `SourceCatalog` remembers, for each of those files, what it stands for, so a result
/// shows the mail's subject and opens in Mail rather than in a text editor.
public enum SourceKind: String, CaseIterable, Sendable, Codable, Identifiable {
    case mail, notes, calendar, reminders, photos

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .mail: "Mail"
        case .notes: "Notes"
        case .calendar: "Calendar"
        case .reminders: "Reminders"
        case .photos: "Photos"
        }
    }

    /// The folder its files go into, under `SourcesLayout.root`.
    public var folderName: String { title }

    /// An SF Symbol for the settings and the results.
    public var symbol: String {
        switch self {
        case .mail: "envelope"
        case .notes: "note.text"
        case .calendar: "calendar"
        case .reminders: "checklist"
        case .photos: "photo.on.rectangle"
        }
    }

    /// The app a result of this source opens in.
    public var appBundleID: String {
        switch self {
        case .mail: "com.apple.mail"
        case .notes: "com.apple.Notes"
        case .calendar: "com.apple.iCal"
        case .reminders: "com.apple.reminders"
        case .photos: "com.apple.Photos"
        }
    }

    /// What it takes for DigUp to read this source.
    public var accessNeeded: String {
        switch self {
        case .mail, .notes: "Full Disk Access"
        case .calendar: "access to Calendars"
        case .reminders: "access to Reminders"
        case .photos: "access to Photos"
        }
    }
}

/// What to read from the sources. Saved by the app (`AppSettings`), handed to `SourceSyncer`.
public struct SourceOptions: Sendable, Equatable {
    public var enabled: Set<SourceKind> = []
    /// Mail received in the last this many years (0: all of it).
    public var mailYears = 5
    /// Junk and deleted mail are left out unless this is on.
    public var mailJunkAndTrash = false
    /// Attachments (PDFs, documents, pictures, recordings) become files of their own beside the mail.
    public var mailAttachments = true
    /// Events from this many years back to this many years ahead.
    public var calendarPastYears = 5
    public var calendarFutureYears = 1
    /// Holidays, birthdays and other subscribed calendars are left out unless this is on.
    public var calendarSubscribed = false
    /// Completed reminders (of the last year) are kept, not only the open ones.
    public var completedReminders = true
    /// Videos from the Photos library, exported small (640×480), up to `photoVideoMinutes` long.
    public var photoVideos = false
    public var photoVideoMinutes = 15
    /// The longest side of the picture written for each photo, in pixels. The model sees 140–280 soft tokens of it,
    /// so a full-size original would only cost disk and time.
    public var photoSide = 1280

    public init() {}

    public var anyEnabled: Bool { !enabled.isEmpty }
}

/// Where the sources' files and their catalog live.
public enum SourcesLayout {
    public static let supportDirectory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/DigUp", isDirectory: true)

    /// The folder the engine indexes as if you had chosen it. It's inside ~/Library, so a home folder you chose never
    /// walks into it (the crawl skips ~/Library), and Spotlight doesn't index its copies twice (`.noindex`).
    public static let root: URL = supportDirectory.appendingPathComponent("Sources.noindex", isDirectory: true)
        .resolvingSymlinksInPath().standardizedFileURL

    /// What each file under `root` stands for. Outside `root`: the crawl never sees it.
    public static let catalogFile = supportDirectory.appendingPathComponent("sources.sqlite")

    public static func folder(_ kind: SourceKind) -> URL {
        root.appendingPathComponent(kind.folderName, isDirectory: true)
    }

    /// The path of `url` relative to `root` ("Mail/INBOX/2024/…"), or nil when it isn't under it.
    public static func relativePath(of path: String) -> String? {
        let prefix = root.path + "/"
        if path.hasPrefix(prefix) { return String(path.dropFirst(prefix.count)) }
        // The walk can report the real path when `root` is spelled through a symlink.
        let real = (root.path as NSString).resolvingSymlinksInPath + "/"
        if real != prefix, path.hasPrefix(real) { return String(path.dropFirst(real.count)) }
        return nil
    }
}

/// How a source is doing, for the settings.
public enum SourceState: Sendable, Equatable {
    case off
    /// macOS hasn't given DigUp what it needs (`SourceKind.accessNeeded`); the text says what to do.
    case needsAccess(String)
    case syncing(done: Int, total: Int)
    case ready(items: Int, at: Date)
    case failed(String)
}
