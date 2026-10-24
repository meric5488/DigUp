import CoreServices
import EventKit
import Foundation
import Photos

/// Keeps the sources' files up to date: a sync when the app starts and when you turn a source on, then whenever the
/// app tells of a change (Photos and Calendar say so; Mail's and Notes' folders are watched), and every 20 minutes in
/// case a change went unnoticed. One source syncs at a time, on a background queue, and a sync only reads what changed
/// since the last one.
///
/// The engine indexes the files like those of any chosen folder (`SourcesLayout.root`): it notices them through its
/// own folder watcher.
public final class SourceSyncer: @unchecked Sendable {
    public typealias StateHandler = @Sendable ([SourceKind: SourceState]) -> Void

    private let queue = DispatchQueue(label: "com.abdurrahim.DigUp.sources", qos: .utility)
    private let catalog: SourceCatalog
    private let providers: [SourceKind: SourceProvider]
    private let onState: StateHandler

    // Under `lock`
    private let lock = NSLock()
    private var options: SourceOptions
    private var states: [SourceKind: SourceState] = [:]
    private var pending = Set<SourceKind>()
    /// Bumped when a source is turned off or its options change: a sync of it that's running stops at its next item.
    private var generation: [SourceKind: Int] = [:]
    private var paused = false

    // `queue` only
    private var running = false
    private var timer: DispatchSourceTimer?
    private var watchers: [SourceKind: ChangeWatcher] = [:]
    private var photoObserver: PhotoObserver?
    private var notificationTokens: [NSObjectProtocol] = []

    /// The order sources sync in: the quick ones first.
    static let order: [SourceKind] = [.reminders, .calendar, .notes, .mail, .photos]

    public init(options: SourceOptions, catalog: SourceCatalog = .shared, onState: @escaping StateHandler) {
        self.options = options
        self.catalog = catalog
        self.onState = onState
        providers = [
            .mail: MailProvider(), .notes: NotesProvider(), .calendar: CalendarProvider(),
            .reminders: RemindersProvider(), .photos: PhotosProvider(),
        ]
        for kind in SourceKind.allCases {
            states[kind] = options.enabled.contains(kind) ? .ready(items: 0, at: .distantPast) : .off
        }
    }

    public var currentStates: [SourceKind: SourceState] { lock.withLock { states } }
    public var currentOptions: SourceOptions { lock.withLock { options } }

    /// Starts syncing the sources that are on, and watching them for changes.
    public func start() {
        queue.async { [self] in
            // Loading the catalog (and its counts) off the main thread: results look files up in it from then on.
            let counts = catalog.counts()
            for kind in SourceKind.allCases {
                if case .ready(_, let at) = currentStates[kind] { setState(kind, .ready(items: counts[kind] ?? 0, at: at)) }
            }
            startTimer()
            updateWatchers()
        }
        schedule(Set(lock.withLock { options.enabled }), after: 2)
        postStates()
    }

    /// Indexing is paused (or outside its hours): no syncs start until it resumes. One that's running finishes its item.
    public func setPaused(_ paused: Bool) {
        let resumed = lock.withLock { () -> Bool in
            defer { self.paused = paused }
            return self.paused && !paused
        }
        if resumed { schedule(Set(lock.withLock { options.enabled }), after: 1) }
    }

    /// New options: a source turned off loses its files, one turned on (or whose options changed) syncs.
    public func setOptions(_ new: SourceOptions) {
        let (old, changed) = lock.withLock { () -> (SourceOptions, Set<SourceKind>) in
            let old = options
            options = new
            var changed = Set<SourceKind>()
            for kind in SourceKind.allCases where Self.matters(kind, old) != Self.matters(kind, new) {
                changed.insert(kind)
                generation[kind, default: 0] += 1
            }
            for kind in SourceKind.allCases where !new.enabled.contains(kind) {
                states[kind] = .off
                pending.remove(kind)
            }
            return (old, changed)
        }
        let turnedOff = old.enabled.subtracting(new.enabled)
        if !turnedOff.isEmpty {
            queue.async { [self] in
                for kind in turnedOff {
                    try? FileManager.default.removeItem(at: SourcesLayout.folder(kind))
                    catalog.forgetAll(kind)
                    SourceLog.write("\(kind.rawValue): off, its files are gone")
                }
            }
        }
        queue.async { [self] in updateWatchers() }
        schedule(changed.intersection(new.enabled), after: 0.5)
        postStates()
    }

    /// Syncs now (from Settings), all the sources that are on or only these.
    public func syncNow(_ kinds: Set<SourceKind>? = nil) {
        let enabled = lock.withLock { options.enabled }
        schedule(kinds.map { $0.intersection(enabled) } ?? enabled, after: 0)
    }

    /// The options that change what a source writes: when they change, it syncs again.
    private static func matters(_ kind: SourceKind, _ options: SourceOptions) -> String {
        guard options.enabled.contains(kind) else { return "off" }
        switch kind {
        case .mail: return "\(options.mailYears) \(options.mailJunkAndTrash) \(options.mailAttachments)"
        case .notes: return "on"
        case .calendar: return "\(options.calendarPastYears) \(options.calendarFutureYears) \(options.calendarSubscribed)"
        case .reminders: return "\(options.completedReminders)"
        case .photos: return "\(options.photoVideos) \(options.photoVideoMinutes) \(options.photoSide)"
        }
    }

    // MARK: Scheduling

    private func schedule(_ kinds: Set<SourceKind>, after seconds: Double) {
        guard !kinds.isEmpty else { return }
        lock.withLock { pending.formUnion(kinds) }
        queue.asyncAfter(deadline: .now() + seconds) { [self] in runPending() }
    }

    /// Runs what's waiting, one source at a time, on `queue`.
    private func runPending() {
        guard !running else { return }
        running = true
        defer { running = false }
        while true {
            let next = lock.withLock { () -> (SourceKind, SourceOptions, Int)? in
                guard !paused else { return nil }
                guard let kind = Self.order.first(where: { pending.contains($0) }) else { return nil }
                pending.remove(kind)
                guard options.enabled.contains(kind) else { return nil }
                return (kind, options, generation[kind, default: 0])
            }
            guard let next else {
                // A source that was turned off in the meantime: go on with the others.
                let more = lock.withLock { !paused && !pending.intersection(options.enabled).isEmpty }
                if more { continue }
                return
            }
            run(next.0, options: next.1, generation: next.2)
        }
    }

    private func run(_ kind: SourceKind, options: SourceOptions, generation started: Int) {
        guard let provider = providers[kind] else { return }
        if let problem = provider.accessProblem() {
            setState(kind, .needsAccess(problem))
            return
        }
        let clock = Date()
        setState(kind, .syncing(done: 0, total: 0))
        let writer = SourceWriter(kind: kind, catalog: catalog)
        let context = SyncContext(options: options, writer: writer, known: catalog.stamps(of: kind),
                                  report: { [weak self] done, total in
                                      self?.setState(kind, .syncing(done: done, total: total))
                                  },
                                  isCancelled: { [weak self] in
                                      guard let self else { return true }
                                      return self.lock.withLock {
                                          self.generation[kind, default: 0] != started || self.paused
                                              || !self.options.enabled.contains(kind)
                                      }
                                  })
        do {
            try provider.sync(context)
            let stillOn = lock.withLock { self.options.enabled.contains(kind) }
            guard stillOn else { return }
            if context.cancelled {
                // Stopped for new options or a pause: it starts again when they allow.
                lock.withLock { _ = pending.insert(kind) }
                setState(kind, .ready(items: catalog.counts()[kind] ?? 0, at: Date()))
                return
            }
            let count = catalog.counts()[kind] ?? 0
            SourceLog.write("\(kind.rawValue): synced, \(count) items (\(Int(Date().timeIntervalSince(clock))) s)")
            setState(kind, .ready(items: count, at: Date()))
        } catch {
            SourceLog.write("\(kind.rawValue): \(error)")
            setState(kind, .failed("\(error)"))
        }
    }

    private func setState(_ kind: SourceKind, _ state: SourceState) {
        let changed = lock.withLock { () -> Bool in
            guard options.enabled.contains(kind) || state == .off else { return false }
            guard states[kind] != state else { return false }
            states[kind] = state
            return true
        }
        if changed { postStates() }
    }

    private func postStates() {
        onState(currentStates)
    }

    // MARK: Watching for changes (`queue`)

    private func startTimer() {
        guard timer == nil else { return }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 20 * 60, repeating: 20 * 60, leeway: .seconds(60))
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            self.schedule(Set(self.lock.withLock { self.options.enabled }), after: 0)
        }
        timer.resume()
        self.timer = timer
    }

    private func updateWatchers() {
        let enabled = lock.withLock { options.enabled }
        // Mail and Notes: their stores' folders. A burst of changes (a mailbox syncing) is one sync, once it settles.
        let folders: [SourceKind: (URL, Double)] = [
            .mail: (MailProvider.mailDirectory, 30),
            .notes: (NotesProvider.container, 10),
        ]
        for (kind, (folder, latency)) in folders {
            if enabled.contains(kind) {
                if watchers[kind] == nil {
                    watchers[kind] = ChangeWatcher(paths: [folder.path], latency: latency, queue: queue) { [weak self] in
                        self?.schedule([kind], after: 0)
                    }
                }
            } else {
                watchers[kind] = nil
            }
        }
        // Photos tells of changes to the library once DigUp may read it.
        if enabled.contains(.photos), PhotosProvider().accessProblem() == nil {
            if photoObserver == nil {
                let observer = PhotoObserver { [weak self] in self?.schedule([.photos], after: 15) }
                PHPhotoLibrary.shared().register(observer)
                photoObserver = observer
            }
        } else if let observer = photoObserver {
            PHPhotoLibrary.shared().unregisterChangeObserver(observer)
            photoObserver = nil
        }
        // Calendar and Reminders: EventKit's change notification, for both.
        if notificationTokens.isEmpty, enabled.contains(.calendar) || enabled.contains(.reminders) {
            let token = NotificationCenter.default.addObserver(forName: .EKEventStoreChanged, object: nil,
                                                               queue: nil) { [weak self] _ in
                self?.schedule([.calendar, .reminders], after: 10)
            }
            notificationTokens.append(token)
        } else if !notificationTokens.isEmpty, !enabled.contains(.calendar), !enabled.contains(.reminders) {
            for token in notificationTokens { NotificationCenter.default.removeObserver(token) }
            notificationTokens = []
        }
    }
}

/// Photos' change notifications, as a callback.
private final class PhotoObserver: NSObject, PHPhotoLibraryChangeObserver {
    private let changed: () -> Void

    init(changed: @escaping () -> Void) {
        self.changed = changed
    }

    func photoLibraryDidChange(_ changeInstance: PHChange) {
        changed()
    }
}

/// FSEvents for a few folders, as a callback (no paths: a change anywhere under them is a reason to sync).
final class ChangeWatcher {
    private var stream: FSEventStreamRef?
    private let callback: () -> Void

    init?(paths: [String], latency: Double, queue: DispatchQueue, callback: @escaping () -> Void) {
        self.callback = callback
        var context = FSEventStreamContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(),
                                           retain: nil, release: nil, copyDescription: nil)
        let handler: FSEventStreamCallback = { _, info, _, _, _, _ in
            guard let info else { return }
            Unmanaged<ChangeWatcher>.fromOpaque(info).takeUnretainedValue().callback()
        }
        guard let stream = FSEventStreamCreate(nil, handler, &context, paths as CFArray,
                                               FSEventStreamEventId(kFSEventStreamEventIdSinceNow), latency,
                                               FSEventStreamCreateFlags(kFSEventStreamCreateFlagNone)) else {
            return nil
        }
        self.stream = stream
        FSEventStreamSetDispatchQueue(stream, queue)
        guard FSEventStreamStart(stream) else {
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
            self.stream = nil
            return nil
        }
    }

    deinit {
        guard let stream else { return }
        FSEventStreamStop(stream)
        FSEventStreamInvalidate(stream)
        FSEventStreamRelease(stream)
    }
}
