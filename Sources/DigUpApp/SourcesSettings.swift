import AppKit
import DigUpSources
import SwiftUI

/// Settings → Sources: Mail, Notes, Calendar, Reminders, Photos and iCloud Drive, each on or off, how each is doing, and
/// how much of each to search (or all of it).
struct SourcesSettings: View {
    @Bindable var state: SettingsState

    /// What the sources follow: with "Sync everything", its lifted limits show where the limits were (`effective`).
    private var options: SourceOptions { state.model.settings.sources.effective }

    /// A setting of the sources that's saved, and followed, as soon as it changes.
    private func binding<T>(_ keyPath: WritableKeyPath<SourceOptions, T>) -> Binding<T> {
        Binding(get: { state.model.settings.sources.effective[keyPath: keyPath] },
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
                    Text("Fouiller tes apps Apple").font(.system(size: 13, weight: .semibold))
                    Text("Mails, notes, événements, rappels, photos et iCloud Drive sont fouillés avec tes dossiers, d'après "
                         + "leur contenu. Un résultat affiche l'objet du mail ou le titre de la note, et s'ouvre dans son "
                         + "app. Fouineur en garde une copie sous forme de petit fichier sur ce Mac pour la fouiller (les fichiers "
                         + "d'iCloud Drive sont fouillés sur place) ; rien ne quitte le Mac.")
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
            Section {
                Toggle("Tout synchroniser", isOn: binding(\.syncEverything))
            } footer: {
                Text("Tous les mails et leurs pièces jointes, les événements d'il y a 30 ans à dans 5 ans, tous les rappels terminés, "
                     + "toutes les photos et vidéos (images en 2048 px). C'est plus long, et prend plus de place.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if options.enabled.contains(.mail) {
                Section("Mail") {
                    Picker("Mails reçus depuis", selection: binding(\.mailYears)) {
                        Text("year").tag(1)
                        Text("2 ans").tag(2)
                        Text("5 ans").tag(5)
                        Text("10 ans").tag(10)
                        Text("Toujours").tag(0)
                    }
                    .disabled(options.syncEverything)
                    Toggle("Fouiller les pièces jointes (PDF, documents, images)", isOn: binding(\.mailAttachments))
                        .disabled(options.syncEverything)
                    Toggle("Inclure les indésirables et les mails supprimés", isOn: binding(\.mailJunkAndTrash))
                }
            }
            if options.enabled.contains(.calendar) {
                Section("Calendrier") {
                    Stepper("Depuis \(options.calendarPastYears) \(options.calendarPastYears == 1 ? "an" : "ans")",
                            value: binding(\.calendarPastYears), in: 0...20)
                    .disabled(options.syncEverything)
                    Stepper("Jusqu'à dans \(options.calendarFutureYears) \(options.calendarFutureYears == 1 ? "an" : "ans")",
                            value: binding(\.calendarFutureYears), in: 0...5)
                    .disabled(options.syncEverything)
                    Toggle("Inclure les jours fériés, anniversaires et calendriers abonnés", isOn: binding(\.calendarSubscribed))
                }
            }
            if options.enabled.contains(.reminders) {
                Section("Rappels") {
                    Toggle("Inclure les rappels terminés (de l'année écoulée)", isOn: binding(\.completedReminders))
                        .disabled(options.syncEverything)
                }
            }
            if options.enabled.contains(.photos) {
                Section {
                    Picker("Taille des images", selection: binding(\.photoSide)) {
                        Text("Petite (1024 px)").tag(1024)
                        Text("Moyenne (1280 px)").tag(1280)
                        Text("Grande (2048 px)").tag(2048)
                    }
                    .disabled(options.syncEverything)
                    Toggle("Fouiller aussi les vidéos", isOn: binding(\.photoVideos))
                        .disabled(options.syncEverything)
                    if options.photoVideos && !options.syncEverything {
                        Stepper("Vidéos jusqu'à \(options.photoVideoMinutes) min", value: binding(\.photoVideoMinutes),
                                in: 1...120, step: 5)
                    }
                } header: {
                    Text("Photos")
                } footer: {
                    Text("Les photos présentes seulement dans iCloud sont téléchargées à cette taille, pas en entier. Une vidéo est copiée en petit "
                         + "(640×480) : c'est plus long, et prend de la place.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            if options.enabled.contains(.icloud) {
                Section {
                    Toggle("Télécharger les fichiers présents seulement dans iCloud", isOn: binding(\.icloudDownload))
                } header: {
                    Text("iCloud Drive")
                } footer: {
                    Text("Les fichiers d'iCloud Drive sont fouillés sur place. Ceux qui ne sont que dans iCloud (Optimiser le stockage "
                         + "du Mac) ne peuvent être lus qu'une fois téléchargés : cette option les télécharge, et ils prennent de la "
                         + "place.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            if options.anyEnabled {
                Section {
                    HStack {
                        Button("Vérifier les changements maintenant") { state.app.sources.syncNow() }
                        Spacer()
                        Button {
                            NSWorkspace.shared.activateFileViewerSelecting([SourcesLayout.root])
                        } label: {
                            Label("Afficher les copies", systemImage: "folder")
                        }
                        .buttonStyle(.link)
                        .help(tildePath(SourcesLayout.root.path))
                    }
                } footer: {
                    Text("Les changements sont repris au fil de l'eau, et vérifiés toutes les 20 minutes. Désactiver une source "
                         + "supprime ses copies.")
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
                Button("Ouvrir Réglages…") { SourceAccess.openPrivacySettings(for: kind) }
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
            Text("Nécessite \(kind.accessNeeded)").font(.system(size: 11)).foregroundStyle(.secondary)
        case .needsAccess(let problem):
            Text(problem).font(.system(size: 11)).foregroundStyle(.orange)
                .fixedSize(horizontal: false, vertical: true)
        case .syncing(let done, let total):
            VStack(alignment: .leading, spacing: 3) {
                Text(total > 0 ? "Lecture : \(done.formatted()) sur \(total.formatted())…" : "Lecture…")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                if total > 0 {
                    ProgressView(value: Double(done), total: Double(max(total, 1))).controlSize(.small).frame(width: 220)
                }
            }
        case .ready(let items, let at):
            let count = "\(items.formatted()) \(items == 1 ? "élément" : "éléments")"
            Text(at == .distantPast ? count : "\(count) · vérifié \(at.formatted(.relative(presentation: .named)))")
                .font(.system(size: 11)).foregroundStyle(.secondary)
        case .failed(let problem):
            Text("⚠︎ \(problem)").font(.system(size: 11)).foregroundStyle(.red)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}
