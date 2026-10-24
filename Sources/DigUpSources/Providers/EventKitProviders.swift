import EventKit
import Foundation

/// Calendar, through EventKit (every calendar the Mac has: iCloud, Google, Exchange…).
///
/// Each event becomes a short text: what, when, where, with whom, and its notes. A repeating event is one file that
/// lists its dates, not one per occurrence. Holidays and birthdays are left out unless asked for.
final class CalendarProvider: SourceProvider {
    let kind = SourceKind.calendar
    private lazy var store = EKEventStore()

    func accessProblem() -> String? {
        switch EKEventStore.authorizationStatus(for: .event) {
        case .fullAccess: return nil
        case .notDetermined: return "Turn Calendar off and on again to let DigUp read your calendars."
        default: return "Allow DigUp to read your calendars: System Settings → Privacy & Security → Calendars."
        }
    }

    private final class Series {
        var events: [EKEvent] = []
    }

    func sync(_ context: SyncContext) throws {
        let options = context.options
        store.refreshSourcesIfNecessary()
        let calendars = store.calendars(for: .event).filter { calendar in
            options.calendarSubscribed || (calendar.type != .birthday && calendar.type != .subscription)
        }
        guard !calendars.isEmpty else {
            context.setTotal(0)
            context.removeUnseen()
            return
        }
        let now = Date()
        let calendar = Calendar.current
        guard var start = calendar.date(byAdding: .year, value: -max(0, options.calendarPastYears), to: now),
              let end = calendar.date(byAdding: .year, value: max(0, options.calendarFutureYears), to: now)
        else { return }

        // EventKit answers for at most four years at a time: one year per question.
        var series: [String: Series] = [:]
        var order: [String] = []
        while start < end {
            if context.cancelled { return }
            let next = min(end, calendar.date(byAdding: .year, value: 1, to: start) ?? end)
            let predicate = store.predicateForEvents(withStart: start, end: next, calendars: calendars)
            for event in store.events(matching: predicate) {
                guard let id = event.eventIdentifier, !id.isEmpty else { continue }
                if series[id] == nil {
                    series[id] = Series()
                    order.append(id)
                }
                series[id]!.events.append(event)
            }
            start = next
        }

        context.setTotal(order.count)
        let settings = "s\(options.calendarSubscribed ? 1 : 0)"
        for id in order {
            if context.cancelled { return }
            guard let events = series[id]?.events, let first = events.first else { continue }
            let modified = first.lastModifiedDate ?? first.creationDate ?? first.startDate ?? now
            let stamp = "\(Int(modified.timeIntervalSince1970))-\(events.count)-\(Int(first.startDate?.timeIntervalSince1970 ?? 0))-\(settings)"
            guard context.needsWriting(id, stamp: stamp) else { continue }
            autoreleasepool {
                context.writer.put(nativeID: id, stamp: stamp, files: [materialize(id, events: events)])
            }
        }
        context.removeUnseen()
    }

    private func materialize(_ id: String, events: [EKEvent]) -> SourceFile {
        let sorted = events.sorted { ($0.startDate ?? .distantPast) < ($1.startDate ?? .distantPast) }
        let first = sorted[0]
        // A repeating event's file is about its next date (or its last one, when they're all past).
        let now = Date()
        let shown = sorted.first { ($0.startDate ?? .distantPast) >= now } ?? sorted.last ?? first
        let title = (first.title ?? "").isEmpty ? "Untitled event" : first.title!
        let calendarName = first.calendar?.title ?? "Calendar"
        let date = shown.startDate ?? now

        var text = title + "\n"
        text += "When: \(Self.when(shown))\n"
        if sorted.count > 1 || first.hasRecurrenceRules {
            let dates = sorted.prefix(40).compactMap(\.startDate).map { SourceNames.readable($0, time: !first.isAllDay) }
            text += "Repeats: \(sorted.count) times in the calendar, on \(dates.joined(separator: "; "))"
                + (sorted.count > 40 ? "…" : "") + "\n"
        }
        if let location = first.location, !location.isEmpty { text += "Where: \(location)\n" }
        if let place = first.structuredLocation?.title, !place.isEmpty, place != first.location { text += "Place: \(place)\n" }
        text += "Calendar: \(calendarName)\n"
        if let organizer = first.organizer.flatMap(Self.person) { text += "Organizer: \(organizer)\n" }
        let attendees = (first.attendees ?? []).compactMap(Self.person)
        if !attendees.isEmpty { text += "Attendees: \(attendees.joined(separator: ", "))\n" }
        if let url = first.url { text += "Link: \(url.absoluteString)\n" }
        if let notes = first.notes?.trimmingCharacters(in: .whitespacesAndNewlines), !notes.isEmpty {
            text += "\n" + notes + "\n"
        }

        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: "?&#")
        let link = id.addingPercentEncoding(withAllowedCharacters: allowed).map {
            "ical://ekevent/\($0)?method=show&options=more"
        }
        let item = SourceItem(kind: .calendar, nativeID: id, title: title,
                              subtitle: "Calendar · \(calendarName) · \(SourceNames.readable(date, time: !first.isAllDay))",
                              openURL: link, date: date)
        return SourceFile(folders: [SourceNames.folder(calendarName), SourceNames.year(date)],
                          name: SourceNames.file("\(SourceNames.day(date)) \(title)", extension: "txt"),
                          content: .text(text), item: item)
    }

    static func when(_ event: EKEvent) -> String {
        guard let start = event.startDate else { return "" }
        if event.isAllDay {
            let end = event.endDate.flatMap { Calendar.current.date(byAdding: .second, value: -1, to: $0) } ?? start
            if Calendar.current.isDate(start, inSameDayAs: end) { return SourceNames.readable(start, time: false) + " (all day)" }
            return SourceNames.readable(start, time: false) + " – " + SourceNames.readable(end, time: false)
        }
        guard let end = event.endDate else { return SourceNames.readable(start) }
        if Calendar.current.isDate(start, inSameDayAs: end) {
            return SourceNames.readable(start) + " – " + end.formatted(date: .omitted, time: .shortened)
        }
        return SourceNames.readable(start) + " – " + SourceNames.readable(end)
    }

    static func person(_ participant: EKParticipant) -> String? {
        if let name = participant.name, !name.isEmpty { return name }
        let address = participant.url.absoluteString
        return address.hasPrefix("mailto:") ? String(address.dropFirst(7)) : (address.isEmpty ? nil : address)
    }
}

/// Reminders, through EventKit.
///
/// Each reminder becomes a short text in a folder named after its list: what, when it's due, its notes. Completed
/// reminders of the last year are kept unless turned off; older completed ones are left out.
final class RemindersProvider: SourceProvider {
    let kind = SourceKind.reminders
    private lazy var store = EKEventStore()

    func accessProblem() -> String? {
        switch EKEventStore.authorizationStatus(for: .reminder) {
        case .fullAccess: return nil
        case .notDetermined: return "Turn Reminders off and on again to let DigUp read your reminders."
        default: return "Allow DigUp to read your reminders: System Settings → Privacy & Security → Reminders."
        }
    }

    private final class Box {
        var reminders: [EKReminder] = []
    }

    func sync(_ context: SyncContext) throws {
        let options = context.options
        store.refreshSourcesIfNecessary()
        let box = Box()
        let done = DispatchSemaphore(value: 0)
        _ = store.fetchReminders(matching: store.predicateForReminders(in: nil)) { reminders in
            box.reminders = reminders ?? []
            done.signal()
        }
        guard done.wait(timeout: .now() + 120) == .success else { throw SourceError("Reminders didn't answer") }

        let yearAgo = Calendar.current.date(byAdding: .year, value: -1, to: Date()) ?? .distantPast
        let reminders = box.reminders.filter { reminder in
            guard reminder.isCompleted else { return true }
            return options.completedReminders
                && (options.allCompletedReminders || (reminder.completionDate ?? .distantPast) >= yearAgo)
        }
        context.setTotal(reminders.count)
        for reminder in reminders {
            if context.cancelled { return }
            let id = reminder.calendarItemIdentifier
            let modified = reminder.lastModifiedDate ?? reminder.creationDate ?? .distantPast
            let stamp = "\(Int(modified.timeIntervalSince1970))-\(reminder.isCompleted ? 1 : 0)"
            guard context.needsWriting(id, stamp: stamp) else { continue }
            autoreleasepool {
                context.writer.put(nativeID: id, stamp: stamp, files: [materialize(reminder)])
            }
        }
        context.removeUnseen()
    }

    private func materialize(_ reminder: EKReminder) -> SourceFile {
        let title = (reminder.title ?? "").isEmpty ? "Untitled reminder" : reminder.title!
        let list = reminder.calendar?.title ?? "Reminders"
        let due = reminder.dueDateComponents.flatMap { Calendar.current.date(from: $0) }
        let dueHasTime = reminder.dueDateComponents?.hour != nil
        let date = due ?? reminder.lastModifiedDate ?? reminder.creationDate

        var text = title + "\n"
        text += "List: \(list)\n"
        if let due { text += "Due: \(SourceNames.readable(due, time: dueHasTime))\n" }
        if reminder.priority > 0 {
            let priority = reminder.priority <= 4 ? "high" : reminder.priority == 5 ? "medium" : "low"
            text += "Priority: \(priority)\n"
        }
        if reminder.isCompleted {
            text += "Completed" + (reminder.completionDate.map { ": \(SourceNames.readable($0))" } ?? "") + "\n"
        }
        if let location = reminder.location, !location.isEmpty { text += "Where: \(location)\n" }
        if let url = reminder.url { text += "Link: \(url.absoluteString)\n" }
        if let notes = reminder.notes?.trimmingCharacters(in: .whitespacesAndNewlines), !notes.isEmpty {
            text += "\n" + notes + "\n"
        }
        let state = reminder.isCompleted ? "done" : (due.map { "due " + SourceNames.readable($0, time: dueHasTime) } ?? "open")
        let item = SourceItem(kind: .reminders, nativeID: reminder.calendarItemIdentifier, title: title,
                              subtitle: "Reminders · \(list) · \(state)",
                              openURL: "x-apple-reminderkit://REMCDReminder/\(reminder.calendarItemIdentifier)",
                              date: date)
        return SourceFile(folders: [SourceNames.folder(list)], name: SourceNames.file(title, extension: "txt"),
                          content: .text(text), item: item)
    }
}
