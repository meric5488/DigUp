import Foundation

/// The Apple apps whose content DigUp can search besides your folders.
///
/// Each source is turned into ordinary files in a folder of its own (`SourcesLayout.root`), which the engine indexes
/// like any chosen folder: a mail becomes a text file (and its attachments files beside it), a photo a JPEG, an event
/// or a reminder a short text. `SourceCatalog` remembers, for each of those files, what it stands for, so a result
/// shows the mail's subject and opens in Mail rather than in a text editor. iCloud Drive is the exception: its files
/// are files already, indexed where they are (`SourcesLayout.iCloudFolders`), with no copies and no catalog.
public enum SourceKind: String, CaseIterable, Sendable, Codable, Identifiable {
    case mail, notes, calendar, reminders, photos, icloud

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .mail: "Mail"
        case .notes: "Notes"
        case .calendar: "Calendar"
        case .reminders: "Reminders"
        case .photos: "Photos"
        case .icloud: "iCloud Drive"
        }
    }

    /// Its items are copies under `SourcesLayout.root` (all but iCloud Drive).
    public var isCopied: Bool { self != .icloud }

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
        case .icloud: "icloud"
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
        case .icloud: "com.apple.finder"
        }
    }

    /// What it takes for DigUp to read this source.
    public var accessNeeded: String {
        switch self {
        case .mail, .notes, .icloud: "Full Disk Access"
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
    /// iCloud Drive: files only in iCloud ("Optimize Mac Storage") are downloaded, so they can be searched.
    public var icloudDownload = false
    /// Everything each source has, whatever the limits above (`effective`).
    public var syncEverything = false
    /// Completed reminders of any age, not only the last year's (`effective`, with `syncEverything`).
    var allCompletedReminders = false

    public init() {}

    /// What a sync follows. With `syncEverything` the limits are lifted: all mail with its attachments, events from 30
    /// years back to 5 ahead, every completed reminder, every video whatever its length; photos are written at 2048 px.
    public var effective: SourceOptions {
        guard syncEverything else { return self }
        var options = self
        options.mailYears = 0
        options.mailAttachments = true
        options.calendarPastYears = 30
        options.calendarFutureYears = 5
        options.completedReminders = true
        options.allCompletedReminders = true
        options.photoVideos = true
        options.photoVideoMinutes = 24 * 60
        options.photoSide = 2048
        return options
    }

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

    /// iCloud Drive's own folder.
    public static let iCloudDrive = mobileDocuments.appendingPathComponent("com~apple~CloudDocs", isDirectory: true)
    static let mobileDocuments = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Mobile Documents", isDirectory: true).resolvingSymlinksInPath()

    /// What the engine indexes for iCloud Drive, where it is: its own folder and the apps' folders the Finder shows in
    /// it (Pages, Numbers, third-party apps' "Documents"). Not the apps' private data beside those.
    public static func iCloudFolders() -> [URL] {
        let fm = FileManager.default
        guard fm.fileExists(atPath: iCloudDrive.path) else { return [] }
        var folders = [iCloudDrive.standardizedFileURL]
        let containers = (try? fm.contentsOfDirectory(atPath: mobileDocuments.path)) ?? []
        for name in containers.sorted() where !name.hasPrefix(".") && name != iCloudDrive.lastPathComponent {
            let documents = mobileDocuments.appendingPathComponent("\(name)/Documents", isDirectory: true)
            var isFolder: ObjCBool = false
            if fm.fileExists(atPath: documents.path, isDirectory: &isFolder), isFolder.boolValue {
                folders.append(documents.standardizedFileURL)
            }
        }
        return folders
    }

    /// Where files in iCloud are: Mobile Documents, and Desktop and Documents when "Desktop & Documents Folders" is on
    /// (iCloud Drive then holds links to them).
    public static func iCloudPaths() -> [String] {
        var paths = [mobileDocuments.path]
        for name in ["Desktop", "Documents"] {
            let link = iCloudDrive.appendingPathComponent(name).path
            if let target = try? FileManager.default.destinationOfSymbolicLink(atPath: link) {
                paths.append((target as NSString).resolvingSymlinksInPath)
            }
        }
        return paths
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
