import Foundation

/// Reads one app's content and keeps its files up to date. Runs on the sync queue only.
protocol SourceProvider: AnyObject {
    var kind: SourceKind { get }

    /// Whether macOS lets DigUp read the source: nil when it does, else what to tell you (`SourceState.needsAccess`).
    /// Never asks: asking is the app's job, when you turn a source on (`SourceAccess`).
    func accessProblem() -> String?

    /// Brings the source's files up to date: writes what's new or changed, removes what's gone. `report` says how far
    /// along it is; `cancelled` is checked between items.
    func sync(_ context: SyncContext) throws
}

/// What a provider syncs with: the options, the stamps of what's already written, the writer, progress.
final class SyncContext {
    let options: SourceOptions
    let writer: SourceWriter
    /// Native ID → the stamp it was handled with.
    let known: [String: String]
    private(set) var seen = Set<String>()
    private let report: (Int, Int) -> Void
    private let isCancelled: () -> Bool
    private var done = 0, total = 0
    private var lastReport = Date.distantPast

    init(options: SourceOptions, writer: SourceWriter, known: [String: String],
         report: @escaping (Int, Int) -> Void, isCancelled: @escaping () -> Bool) {
        self.options = options
        self.writer = writer
        self.known = known
        self.report = report
        self.isCancelled = isCancelled
    }

    var cancelled: Bool { isCancelled() }

    /// The number of items the source has, as soon as the provider knows it.
    func setTotal(_ count: Int) {
        total = count
        report(done, total)
    }

    /// Marks an item as still in the app. Returns whether it needs writing: new, or changed since (its stamp differs).
    func needsWriting(_ id: String, stamp: String) -> Bool {
        seen.insert(id)
        step()
        return known[id] != stamp
    }

    private func step() {
        done += 1
        let now = Date()
        if now.timeIntervalSince(lastReport) >= 0.5 || done == total {
            lastReport = now
            report(done, max(total, done))
        }
    }

    /// Removes the files of items that weren't seen: gone from the app. Only after a complete pass (a cancelled or
    /// failed one hasn't seen everything).
    func removeUnseen() {
        let gone = known.keys.filter { !seen.contains($0) }
        if !gone.isEmpty { SourceLog.write("\(writer.kind.rawValue): \(gone.count) gone") }
        writer.remove(Array(gone))
    }
}

/// Errors a provider reports as the source's state.
struct SourceError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}
