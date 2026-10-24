import AppKit
import DigUpSources
import SwiftUI

/// Settings → Sources: Mail, Notes, Calendar, Reminders and Photos, each on or off, how each is doing, and how much of
/// each to search.
struct SourcesSettings: View {
    @Bindable var state: SettingsState

    private var options: SourceOptions { state.model.settings.sources }

    /// A setting of the sources that's saved, and followed, as soon as it changes.
    private func binding<T>(_ keyPath: WritableKeyPath<SourceOptions, T>) -> Binding<T> {
        Binding(get: { state.model.settings.sources[keyPath: keyPath] },
                set: { value in
                    var options = state.model.settings.sources
                    options[keyPath: keyPath] = value
                    state.app.applySources(options)
                })
    }

    var body: some View {
        Form {
            Section {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Search your Apple apps").font(.system(size: 13, weight: .semibold))
                    Text("Mail, notes, events, reminders and photos are searched with your folders, by what's in "
                         + "them. A result shows the mail's subject or the note's title, and opens in its app. "
                         + "DigUp keeps a copy of each as a small file on this Mac to search it; nothing leaves the Mac.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Section {
                ForEach(SourceKind.allCases) { kind in
                    SourceRow(kind: kind, state: state)
                }
            } header: {
                Text("Sources")
            }
            if options.enabled.contains(.mail) {
                Section("Mail") {
                    Picker("Mail received in the last", selection: binding(\.mailYears)) {
                        Text("year").tag(1)
                        Text("2 years").tag(2)
                        Text("5 years").tag(5)
                        Text("10 years").tag(10)
                        Text("All time").tag(0)
                    }
                    Toggle("Search attachments (PDFs, documents, pictures)", isOn: binding(\.mailAttachments))
                    Toggle("Include junk and deleted mail", isOn: binding(\.mailJunkAndTrash))
                }
            }
            if options.enabled.contains(.calendar) {
                Section("Calendar") {
                    Stepper("From \(options.calendarPastYears) \(options.calendarPastYears == 1 ? "year" : "years") ago",
                            value: binding(\.calendarPastYears), in: 0...20)
                    Stepper("To \(options.calendarFutureYears) \(options.calendarFutureYears == 1 ? "year" : "years") ahead",
                            value: binding(\.calendarFutureYears), in: 0...5)
                    Toggle("Include holidays, birthdays and subscribed calendars", isOn: binding(\.calendarSubscribed))
                }
            }
            if options.enabled.contains(.reminders) {
                Section("Reminders") {
                    Toggle("Include completed reminders (of the last year)", isOn: binding(\.completedReminders))
                }
            }
            if options.enabled.contains(.photos) {
                Section {
                    Picker("Picture size", selection: binding(\.photoSide)) {
                        Text("Small (1024 px)").tag(1024)
                        Text("Medium (1280 px)").tag(1280)
                        Text("Large (2048 px)").tag(2048)
                    }
                    Toggle("Search videos too", isOn: binding(\.photoVideos))
                    if options.photoVideos {
                        Stepper("Videos up to \(options.photoVideoMinutes) min", value: binding(\.photoVideoMinutes),
                                in: 1...120, step: 5)
                    }
                } header: {
                    Text("Photos")
                } footer: {
                    Text("Photos only in iCloud are downloaded at this size, not in full. A video is copied small "
                         + "(640×480): it takes longer, and disk space.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            if options.anyEnabled {
                Section {
                    HStack {
                        Button("Check for Changes Now") { state.app.sources.syncNow() }
                        Spacer()
                        Button {
                            NSWorkspace.shared.activateFileViewerSelecting([SourcesLayout.root])
                        } label: {
                            Label("Show the Copies", systemImage: "folder")
                        }
                        .buttonStyle(.link)
                        .help(tildePath(SourcesLayout.root.path))
                    }
                } footer: {
                    Text("Changes are picked up as they happen, and checked every 20 minutes. Turning a source off "
                         + "deletes its copies.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
    }
}

/// One source: its switch, and what it's doing (or what it needs).
private struct SourceRow: View {
    let kind: SourceKind
    @Bindable var state: SettingsState

    private var isOn: Bool { state.model.settings.sources.enabled.contains(kind) }
    private var sourceState: SourceState { isOn ? state.model.sourceStates[kind] ?? .ready(items: 0, at: .distantPast) : .off }

    var body: some View {
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: kind.symbol)
                .font(.system(size: 15))
                .frame(width: 22)
                .foregroundStyle(isOn ? Color.accentColor : .secondary)
            VStack(alignment: .leading, spacing: 3) {
                Text(kind.title).font(.system(size: 13, weight: .medium))
                status
            }
            Spacer()
            if case .needsAccess = sourceState {
                Button("Open Settings…") { SourceAccess.openPrivacySettings(for: kind) }
                    .controlSize(.small)
            }
            Toggle("", isOn: Binding(get: { isOn }, set: { state.app.setSource(kind, on: $0) }))
                .toggleStyle(.switch)
                .labelsHidden()
                .controlSize(.small)
        }
        .padding(.vertical, 2)
    }

    @ViewBuilder private var status: some View {
        switch sourceState {
        case .off:
            Text("Needs \(kind.accessNeeded)").font(.system(size: 11)).foregroundStyle(.secondary)
        case .needsAccess(let problem):
            Text(problem).font(.system(size: 11)).foregroundStyle(.orange)
                .fixedSize(horizontal: false, vertical: true)
        case .syncing(let done, let total):
            VStack(alignment: .leading, spacing: 3) {
                Text(total > 0 ? "Reading \(done.formatted()) of \(total.formatted())…" : "Reading…")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                if total > 0 {
                    ProgressView(value: Double(done), total: Double(max(total, 1))).controlSize(.small).frame(width: 220)
                }
            }
        case .ready(let items, let at):
            let count = "\(items.formatted()) \(items == 1 ? "item" : "items")"
            Text(at == .distantPast ? count : "\(count) · checked \(at.formatted(.relative(presentation: .named)))")
                .font(.system(size: 11)).foregroundStyle(.secondary)
        case .failed(let problem):
            Text("⚠︎ \(problem)").font(.system(size: 11)).foregroundStyle(.red)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}
