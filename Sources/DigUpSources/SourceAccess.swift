import AppKit
import EventKit
import Foundation
import Photos

/// Asking macOS for what each source needs, and opening its privacy settings. Call from the main thread.
public enum SourceAccess {
    /// Asks for access when macOS can be asked (Calendars, Reminders, Photos: it shows its own prompt the first time).
    /// Mail, Notes and iCloud Drive need Full Disk Access, which only you can give in System Settings: that pane opens instead.
    /// `completion` gets whether the source can be read now, on the main thread.
    public static func request(_ kind: SourceKind, completion: @escaping @Sendable (Bool) -> Void) {
        func finish(_ granted: Bool) { DispatchQueue.main.async { completion(granted) } }
        switch kind {
        case .calendar:
            let store = EKEventStore()
            store.requestFullAccessToEvents { granted, _ in
                withExtendedLifetime(store) { finish(granted) }
            }
        case .reminders:
            let store = EKEventStore()
            store.requestFullAccessToReminders { granted, _ in
                withExtendedLifetime(store) { finish(granted) }
            }
        case .photos:
            PHPhotoLibrary.requestAuthorization(for: .readWrite) { status in
                finish(status == .authorized || status == .limited)
            }
        case .mail:
            let readable = MailProvider().accessProblem() == nil
            if !readable { openPrivacySettings(for: kind) }
            finish(readable)
        case .notes:
            let readable = NotesProvider().accessProblem() == nil
            if !readable { openPrivacySettings(for: kind) }
            finish(readable)
        case .icloud:
            let readable = ICloudDriveProvider().accessProblem() == nil
            if !readable { openPrivacySettings(for: kind) }
            finish(readable)
        }
    }

    /// What's in the way of reading the source right now, or nil.
    public static func problem(_ kind: SourceKind) -> String? {
        switch kind {
        case .mail: MailProvider().accessProblem()
        case .notes: NotesProvider().accessProblem()
        case .calendar: CalendarProvider().accessProblem()
        case .reminders: RemindersProvider().accessProblem()
        case .photos: PhotosProvider().accessProblem()
        case .icloud: ICloudDriveProvider().accessProblem()
        }
    }

    /// System Settings → Privacy & Security, at the list that concerns this source.
    public static func openPrivacySettings(for kind: SourceKind) {
        let anchor = switch kind {
        case .mail, .notes, .icloud: "Privacy_AllFiles"
        case .calendar: "Privacy_Calendars"
        case .reminders: "Privacy_Reminders"
        case .photos: "Privacy_Photos"
        }
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)") {
            NSWorkspace.shared.open(url)
        }
    }
}

/// Opens a source's result in its app: the mail in Mail, the note in Notes, the event in Calendar, the reminder in
/// Reminders, the photo in Photos.
public enum SourceOpener {
    /// Call from the main thread. The AppleScript (Notes, Photos) runs in the background, so a slow app doesn't hold the
    /// panel up; when it fails, the item's link (or its app) opens instead.
    public static func open(_ item: SourceItem) {
        if let script = item.openScript {
            DispatchQueue.global(qos: .userInitiated).async {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
                process.arguments = ["-e", script]
                process.standardOutput = FileHandle.nullDevice
                process.standardError = FileHandle.nullDevice
                var worked = false
                do {
                    try process.run()
                    process.waitUntilExit()
                    worked = process.terminationStatus == 0
                } catch {}
                if !worked {
                    SourceLog.write("open: \(item.kind.rawValue) script failed, opening its link")
                    DispatchQueue.main.async { openLink(item) }
                }
            }
            return
        }
        openLink(item)
    }

    private static func openLink(_ item: SourceItem) {
        if let link = item.openURL.flatMap(URL.init(string:)), NSWorkspace.shared.open(link) { return }
        if let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: item.kind.appBundleID) {
            NSWorkspace.shared.openApplication(at: app, configuration: NSWorkspace.OpenConfiguration())
        }
    }
}
