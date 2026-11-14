import AppKit
import DigUpKit
import Observation
import SwiftUI

enum SettingsTab: String, CaseIterable {
    case general, folders, sources, code, storage

    var title: String {
        switch self {
        case .general: "Général"
        case .folders: "Dossiers"
        case .sources: "Sources"
        case .code: "Code"
        case .storage: "Stockage"
        }
    }

    var symbol: String {
        switch self {
        case .general: "gearshape"
        case .folders: "folder"
        case .sources: "tray.full"
        case .code: "chevron.left.forwardslash.chevron.right"
        case .storage: "internaldrive"
        }
    }
}

/// Settings, laid out as in System Settings: the pages in a sidebar (the desktop showing through it, as in Blablateur
/// and Homericc), the selected one beside it. Folders is also where the index shows how far along it is, folder by
/// folder.
final class SettingsWindowController: NSObject, NSWindowDelegate {
    let state: SettingsState
    /// Folders → Add Folders…, a sheet on this window.
    let addSheet: AddFoldersController
    /// Code → Add Folders… (code search's folders), a sheet on this window.
    let codeSheet: AddFoldersController
    private unowned let app: AppController
    private var window: NSWindow?

    init(app: AppController) {
        self.app = app
        state = SettingsState(app: app)
        addSheet = AddFoldersController(app: app)
        codeSheet = AddFoldersController(app: app, code: true)
        super.init()
        state.addFolders = { [weak self] in self?.addFolders() }
        state.addCodeFolders = { [weak self] in self?.addCodeFolders() }
        for sheet in [addSheet, codeSheet] {
            sheet.onAdded = { [weak self] in
                guard let self else { return }
                Task { await self.state.refresh() }
            }
        }
    }

    /// Opens Add Folders on the Folders tab.
    func addFolders() {
        addSheet.begin(on: window)
    }

    /// Opens the code folders' sheet on the Code tab.
    func addCodeFolders() {
        codeSheet.begin(on: window)
    }

    var isVisible: Bool { window?.isVisible == true }

    func show(_ tab: SettingsTab = .general) {
        let window = self.window ?? makeWindow()
        state.tab = tab
        Task { await state.refresh() }
        app.present(window)
    }

    var selectedTab: SettingsTab { state.tab }

    private func makeWindow() -> NSWindow {
        let controller = NSHostingController(rootView: SettingsRoot(state: state))
        controller.sizingOptions = []
        // The split view's toolbar and the page's title reach the window.
        controller.sceneBridgingOptions = [.toolbars, .title]
        let window = NSWindow(contentViewController: controller)
        window.styleMask = [.titled, .closable, .resizable, .fullSizeContentView]
        window.toolbarStyle = .unified
        window.setContentSize(NSSize(width: 820, height: 620))
        window.contentMinSize = NSSize(width: 820, height: 420)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        self.window = window
        state.selectTab = { [weak self] tab in self?.state.tab = tab }
        return window
    }

    func windowWillClose(_ notification: Notification) {
        addSheet.end()
        codeSheet.end()
        window?.contentViewController = nil
        window = nil
        app.windowClosed()
    }

    func snapshot(to url: URL) throws {
        try Snapshot.write(window, to: url)
    }
}

@Observable
final class SettingsState {
    var tab = SettingsTab.general
    private(set) var summaries: [Engine.RootSummary]?
    private(set) var codeSummaries: [Engine.RootSummary] = []
    private(set) var indexSize: Int64 = 0
    private(set) var codeIndexSize: Int64 = 0
    var launchAtLogin = false
    var loginProblem: String?
    var checksForUpdates = false
    @ObservationIgnored let app: AppController
    @ObservationIgnored var selectTab: (SettingsTab) -> Void = { _ in }
    @ObservationIgnored var addFolders: () -> Void = {}
    @ObservationIgnored var addCodeFolders: () -> Void = {}

    init(app: AppController) {
        self.app = app
    }

    @ObservationIgnored private var lastRefresh = Date.distantPast
    @ObservationIgnored private var refreshPending = false

    var model: AppModel { app.model }

    func refresh() async {
        lastRefresh = Date()
        launchAtLogin = app.launchesAtLogin
        checksForUpdates = app.updates.automaticallyChecks
        indexSize = app.engine.indexSize
        codeIndexSize = app.engine.codeIndexSize
        codeSummaries = await app.engine.rootSummaries(code: true)
        summaries = await app.engine.rootSummaries()
    }

    /// The index changed: refresh the folder counts, at most every 2 s while files are being indexed.
    func statusChanged() {
        guard !refreshPending else { return }
        refreshPending = true
        let wait = max(0, 2 - Date().timeIntervalSince(lastRefresh))
        Task {
            try? await Task.sleep(for: .seconds(wait))
            refreshPending = false
            await refresh()
        }
    }

    /// Ready to be drawn (debug snapshots wait for this).
    var isLoaded: Bool { summaries != nil }
}

/// The pages in the sidebar, the selected one beside it.
private struct SettingsRoot: View {
    let state: SettingsState

    var body: some View {
        NavigationSplitView {
            List(selection: Binding(get: { state.tab }, set: { if let tab = $0 { state.tab = tab } })) {
                ForEach(SettingsTab.allCases, id: \.self) { tab in
                    Label(tab.title, systemImage: tab.symbol)
                }
            }
            .scrollContentBackground(.hidden)
            .background { SidebarBackdrop() }
            .navigationSplitViewColumnWidth(190)
            .toolbar(removing: .sidebarToggle)
        } detail: {
            SettingsPane(tab: state.tab, state: state)
                .id(state.tab)
                .navigationTitle(state.tab.title)
        }
    }
}

/// One page's content, at the width every page shares; it scrolls when the window is shorter than the page.
private struct SettingsPane: View {
    let tab: SettingsTab
    let state: SettingsState

    var body: some View {
        ScrollView(.vertical) {
            Group {
                switch tab {
                case .general: GeneralSettings(state: state)
                case .folders: FolderSettings(state: state)
                case .sources: SourcesSettings(state: state)
                case .code: CodeSettings(state: state)
                case .storage: StorageSettings(state: state)
                }
            }
            .frame(width: 580)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity)
        }
        .scrollBounceBehavior(.basedOnSize)
    }
}

private struct GeneralSettings: View {
    @Bindable var state: SettingsState

    var body: some View {
        Form {
            Section {
                LabeledContent {
                    ShortcutRecorder(combo: state.model.hotkey, apply: { state.app.setHotkey($0) },
                                     listening: { state.app.hotkeyRecording($0) }, capSize: 13)
                } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Raccourci de recherche")
                        Text("Ouvre le panneau de recherche depuis n'importe quelle app.")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }
                }
                LabeledContent {
                    ShortcutRecorder(combo: state.model.windowHotkey, apply: { state.app.setWindowHotkey($0) },
                                     listening: { state.app.hotkeyRecording($0) }, capSize: 13)
                } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Raccourci de la fenêtre")
                        Text("Ouvre la fenêtre Fouineur depuis n'importe quelle app.")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }
                }
            }
            Section {
                Toggle(isOn: Binding(get: { state.launchAtLogin }, set: { on in
                    state.loginProblem = state.app.setLaunchAtLogin(on)
                    state.launchAtLogin = state.app.launchesAtLogin
                })) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Ouvrir Fouineur à l'ouverture de session")
                        Text(state.loginProblem ?? "Pour que le raccourci marche toujours.")
                            .font(.system(size: 11))
                            .foregroundStyle(state.loginProblem == nil ? AnyShapeStyle(.secondary) : AnyShapeStyle(.red))
                    }
                }
            }
            Section {
                Toggle(isOn: Binding(get: { state.checksForUpdates }, set: { on in
                    state.app.updates.automaticallyChecks = on
                    state.checksForUpdates = on
                })) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Rechercher les mises à jour automatiquement")
                        Text("Une fois par jour, Fouineur demande à GitHub sa dernière version ; rien sur tes fichiers n'est envoyé. "
                             + "Les mises à jour s'installent en un clic.")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                LabeledContent {
                    Button("Vérifier maintenant") { state.app.updates.checkNow() }
                } label: {
                    Text(state.model.updateAvailable.map { "Fouineur \($0) est disponible" }
                         ?? "Fouineur \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")")
                }
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
    }
}

private struct FolderSettings: View {
    @Bindable var state: SettingsState
    @State private var removing: Engine.RootSummary?

    var body: some View {
        Form {
            Section {
                IndexProgress(model: state.model) { state.app.engine.setPaused(!state.model.status.paused) }
            }
            Section {
                if let summaries = state.summaries {
                    if summaries.isEmpty {
                        Text("Aucun dossier pour l'instant. Ajoute ceux que Fouineur doit fouiller.")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(summaries) { summary in
                        FolderStatusRow(summary: summary) { removing = summary }
                    }
                } else {
                    ProgressView().controlSize(.small).frame(maxWidth: .infinity)
                }
            } header: {
                Text("Dossiers à fouiller")
            } footer: {
                HStack {
                    Button(action: state.addFolders) {
                        Label("Ajouter des dossiers…", systemImage: "plus")
                    }
                    Spacer()
                }
            }
            Section {
                ForEach(state.model.settings.excludedFolders, id: \.path) { folder in
                    HStack {
                        Image(nsImage: NSWorkspace.shared.icon(forFile: folder.path))
                            .resizable()
                            .frame(width: 18, height: 18)
                        Text(tildePath(folder.path)).lineLimit(1).truncationMode(.middle)
                        Spacer()
                        Button {
                            state.app.setExcluded(state.model.settings.excludedFolders.filter { $0 != folder })
                        } label: {
                            Image(systemName: "minus.circle.fill").foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help("Refouiller ce dossier")
                    }
                }
                if state.model.settings.excludedFolders.isEmpty {
                    Text("Aucun. Les projets de code, les apps et les caches sont ignorés de toute façon (le code a son propre onglet).")
                        .foregroundStyle(.secondary)
                }
            } header: {
                Text("Ignorer ces dossiers")
            } footer: {
                HStack {
                    Button {
                        state.app.pickFolders(for: NSApp.keyWindow, prompt: "Ignorer") { folders in
                            guard !folders.isEmpty else { return }
                            state.app.setExcluded(state.model.settings.excludedFolders
                                + folders.filter { !state.model.settings.excludedFolders.contains($0) })
                        }
                    } label: {
                        Label("Ignorer un dossier…", systemImage: "plus")
                    }
                    Spacer()
                }
            }
            Section {
                TypeExclusions(types: state.model.settings.excludedTypes) { state.app.setExcludedTypes($0) }
            } header: {
                Text("Ignorer ces types de fichiers")
            } footer: {
                Text("Ici, Fouineur ne lit que les images, PDF, documents (txt, md, typ, rtf, docx, odt, html), l'audio et la vidéo ; "
                     + "le code n'est lu que dans les dossiers de code (onglet Code). Les clés, certificats et fichiers de mots de passe "
                     + "ne sont jamais lus, pas plus que les dossiers cachés comme ~/.ssh.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Section {
                Toggle(isOn: Binding(get: { state.model.settings.indexOnBattery },
                                     set: { state.app.setIndexOnBattery($0) })) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(AppSettings.batteryTitle)
                        Text(AppSettings.batteryDetail)
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }
                }
                Toggle(isOn: Binding(get: { state.model.settings.indexHoursOn }, set: { on in
                    state.app.setIndexHours(on: on, hours: state.model.settings.indexHours)
                })) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Indexer seulement à certaines heures")
                        Text("Les fichiers ne sont lus qu'entre ces heures, et ce qui a changé entre-temps est repris quand "
                             + "elles commencent. La recherche marche toute la journée.")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                if state.model.settings.indexHoursOn {
                    LabeledContent("De") {
                        HStack(spacing: 6) {
                            DatePicker("De", selection: time(\.start), displayedComponents: .hourAndMinute)
                                .labelsHidden()
                            Text("à")
                            DatePicker("À", selection: time(\.end), displayedComponents: .hourAndMinute)
                                .labelsHidden()
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .onChange(of: state.model.status) { state.statusChanged() }
        .confirmationDialog(removalTitle, isPresented: Binding(get: { removing != nil },
                                                               set: { if !$0 { removing = nil } })) {
            Button("Retirer", role: .destructive) {
                guard let removing else { return }
                state.app.removeFolder(URL(fileURLWithPath: removing.path))
                Task { await state.refresh() }
            }
        } message: {
            Text("Ses \(removing?.files.formatted() ?? "") fichiers quittent l'index. Le rajouter plus tard les "
                 + "réindexera.")
        }
    }

    private var removalTitle: String {
        "Arrêter de fouiller « \(removing.map { ($0.path as NSString).lastPathComponent } ?? "") » ?"
    }

    /// One end of the set hours, as a time of day for a date picker.
    private func time(_ end: KeyPath<IndexHours, Int>) -> Binding<Date> {
        Binding(get: {
            let minute = state.model.settings.indexHours[keyPath: end]
            return Calendar.current.date(bySettingHour: minute / 60, minute: minute % 60, second: 0, of: Date()) ?? Date()
        }, set: { date in
            let parts = Calendar.current.dateComponents([.hour, .minute], from: date)
            let minute = (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
            let hours = state.model.settings.indexHours
            state.app.setIndexHours(on: true, hours: end == \IndexHours.start ? IndexHours(start: minute, end: hours.end)
                                                                              : IndexHours(start: hours.start, end: minute))
        })
    }
}

/// Extensions typed as "heic, txt": applied on Return or when the field loses focus.
private struct TypeExclusions: View {
    let types: [String]
    let apply: (String) -> Void
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        TextField("Extensions", text: $text, prompt: Text("p. ex. heic, txt"))
            .labelsHidden()
            .focused($focused)
            .onSubmit { apply(text) }
            .onChange(of: focused) { _, now in if !now { apply(text) } }
            .onAppear { text = types.joined(separator: ", ") }
            .onChange(of: types) { _, now in text = now.joined(separator: ", ") }
    }
}

/// What the index is doing, with Pause/Resume.
private struct IndexProgress: View {
    let model: AppModel
    let togglePause: () -> Void

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 5) {
                Text(model.statusLine).font(.system(size: 13, weight: .semibold))
                if let progress = model.progress {
                    ProgressView(value: progress).controlSize(.small)
                }
                Text(detail)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if model.hasFolders {
                Button(model.status.paused ? "Reprendre" : "Suspendre", action: togglePause)
            }
        }
    }

    /// "357 of 365 files searchable · 8 waiting · 2 long files to finish"
    private var detail: String {
        let status = model.status
        var text = "\(status.searchable.formatted()) fichiers sur \(status.files.formatted()) consultables"
        if status.pending > 0 { text += " · \(status.pending.formatted()) en attente" }
        if status.unfinished > 0 {
            text += " · \(status.unfinished.formatted()) \(status.unfinished == 1 ? "long fichier" : "longs fichiers") à terminer"
        }
        if model.download.isActive { text += " · modèle \(model.download.progressText)" }
        return text
    }
}

private struct FolderStatusRow: View {
    let summary: Engine.RootSummary
    let remove: () -> Void
    @State private var showingReasons = false

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(nsImage: NSWorkspace.shared.icon(forFile: summary.missing ? "/Volumes" : summary.path))
                .resizable()
                .frame(width: 32, height: 32)
                .opacity(summary.missing ? 0.5 : 1)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text((summary.path as NSString).lastPathComponent).font(.system(size: 13, weight: .semibold))
                    if summary.missing || summary.unreadable {
                        Text(summary.missing ? "Non connecté" : "Pas d'accès")
                            .font(.system(size: 10, weight: .semibold))
                            .padding(.horizontal, 6)
                            .padding(.vertical, 1)
                            .background(Capsule().fill(Color.orange.opacity(0.2)))
                    }
                    if summary.unreadable {
                        Button("Autoriser dans Réglages…", action: openFolderPrivacySettings)
                            .buttonStyle(.link)
                            .font(.system(size: 11))
                    }
                }
                Text(tildePath(summary.path))
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if !summary.kinds.isEmpty {
                    KindCounts(counts: summary.kinds).padding(.top, 1)
                }
            }
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 2) {
                Text(summary.missing || summary.unreadable ? "Conservé pour son retour"
                     : "\(summary.indexed.formatted()) sur \(summary.files.formatted()) indexés")
                    .font(.system(size: 12, weight: .medium).monospacedDigit())
                if !states.isEmpty {
                    if reasons.isEmpty {
                        Text(states).font(.system(size: 11)).foregroundStyle(.secondary)
                    } else {
                        Button { showingReasons.toggle() } label: {
                            HStack(spacing: 3) {
                                Text(states)
                                Image(systemName: "info.circle")
                            }
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help("Pourquoi certains fichiers ne sont pas consultables")
                        .popover(isPresented: $showingReasons, arrowEdge: .bottom) {
                            VStack(alignment: .leading, spacing: 4) {
                                ForEach(reasons, id: \.self) { Text($0) }
                            }
                            .font(.system(size: 12))
                            .padding(12)
                            .frame(maxWidth: 360, alignment: .leading)
                        }
                    }
                }
            }
            Button(action: remove) {
                Image(systemName: "minus.circle.fill").foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("Arrêter de fouiller ce dossier")
            .accessibilityLabel("Retirer \((summary.path as NSString).lastPathComponent)")
        }
        .padding(.vertical, 2)
    }

    private var states: String {
        [summary.pending > 0 ? "\(summary.pending.formatted()) en attente" : nil,
         summary.unfinished > 0
            ? "\(summary.unfinished.formatted()) \(summary.unfinished == 1 ? "long fichier" : "longs fichiers") à terminer" : nil,
         summary.skipped > 0 ? "\(summary.skipped.formatted()) ignorés" : nil,
         summary.failed > 0 ? "\(summary.failed.formatted()) illisibles" : nil,
         summary.leftOutCount > 0 ? "\(summary.leftOutCount.formatted()) non lus" : nil].compactMap { $0 }
            .joined(separator: " · ")
    }

    /// One line per reason: what the crawl never queued from this folder, then what was skipped once read.
    private var reasons: [String] {
        let crawl: [String: String] = [
            "other type": "d'un type que Fouineur ne lit pas",
            "app or bundle": "dans des apps ou des paquets",
            "Photos library": "dans une photothèque Photos (ses photos ne sont pas encore lues)",
            "link or special file": "liens ou fichiers spéciaux",
            "in the cloud, not downloaded": "seulement dans le cloud, non téléchargés",
            "inside a code project": "dans des projets de code, où seules les captures d'écran comptent",
            "dataset": "dans des dossiers qui ressemblent à des jeux de données",
            "code or data file": "fichiers de code ou de données",
            "excluded type": "des types de fichiers que tu ignores",
        ]
        let leftOut = summary.leftOut.sorted { $0.value > $1.value }.map { reason, count in
            reason.hasPrefix("folders ") ? "\(count.formatted()) dossiers non ouverts (\(reason.dropFirst(8)))"
                : "\(count.formatted()) \(crawl[reason] ?? reason)"
        }
        let skipped = summary.skippedReasons.sorted { $0.value > $1.value }.map { "\($1.formatted()) ignorés : \($0)" }
        return leftOut + skipped
    }
}

private struct StorageSettings: View {
    let state: SettingsState

    var body: some View {
        Form {
            Section {
                LabeledContent("EmbeddingGemma 2") {
                    Text("Google · Apache 2.0 · 865 MB")
                }
                LabeledContent("État") {
                    HStack(spacing: 8) {
                        Text(state.model.download.isReady ? "Téléchargé" : state.model.download.progressText)
                        switch state.model.download.phase {
                        case .needed, .failed:
                            Button("Télécharger") { state.model.download.start() }.controlSize(.small)
                            Button("Utiliser des fichiers…") { state.model.download.chooseFiles(for: NSApp.keyWindow) }
                                .controlSize(.small)
                        default:
                            EmptyView()
                        }
                    }
                }
                LabeledContent("Emplacement") {
                    PathButton(url: state.model.settings.modelsDirectory)
                }
            } header: {
                Text("Modèle")
            } footer: {
                if !state.model.download.isReady {
                    HStack(alignment: .firstTextBaseline) {
                        Text("Si ton réseau bloque Hugging Face, télécharge les deux fichiers sur un autre réseau et "
                             + "clique sur Utiliser des fichiers… pour les choisir. Fouineur les vérifie avant de s'en servir.")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                        Spacer()
                        Button("Copier les liens") {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(ModelDownload.links, forType: .string)
                        }
                        .controlSize(.small)
                        .help("Copie les adresses des deux fichiers")
                    }
                }
            }
            Section("Index") {
                LabeledContent("Taille sur le disque", value: bytes(state.indexSize))
                if state.model.hasCode {
                    LabeledContent("Index du code", value: bytes(state.codeIndexSize))
                }
                LabeledContent("Emplacement") {
                    PathButton(url: state.model.settings.indexDirectory)
                }
            }
            Section("Confidentialité") {
                Text("Fouineur lit les dossiers que tu choisis et garde ce qu'il en apprend dans son index, sur ce Mac. "
                     + "Il ne se connecte que pour télécharger son modèle, une fois, et vérifier les mises à jour (Général), "
                     + "sans rien envoyer sur tes fichiers.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Section {
                Text("Fouineur \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "") · "
                     + "EmbeddingGemma 2 de Google (Apache 2.0), exécuté par llama.cpp (MIT).")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
    }
}

/// Code search: its folders and how far along their index is, the editor results open in, and turning it off.
private struct CodeSettings: View {
    @Bindable var state: SettingsState
    @State private var turningOff = false
    @State private var removing: Engine.RootSummary?

    var body: some View {
        Form {
            Section {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Chercher dans ton code").font(.system(size: 13, weight: .semibold))
                    Text("Trouve du code d'après ce qu'il fait, ou un nom qu'il contient : tape code: dans le panneau de recherche, comme "
                         + "« code: retry with backoff ». Le code a son propre index, et n'apparaît jamais dans "
                         + "les autres recherches.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if state.model.hasCode {
                    HStack {
                        VStack(alignment: .leading, spacing: 5) {
                            Text(state.model.codeStatusLine).font(.system(size: 12, weight: .medium))
                            if let indexing = state.model.status.code.indexing, indexing.total > 0 {
                                ProgressView(value: Double(indexing.done) / Double(indexing.total)).controlSize(.small)
                            }
                        }
                        Spacer()
                        Text(bytes(state.codeIndexSize))
                            .font(.system(size: 11).monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
            }
            Section {
                if state.codeSummaries.isEmpty {
                    Text("Aucun dossier de code pour l'instant. Ajoute les dossiers qui contiennent tes projets.")
                        .foregroundStyle(.secondary)
                }
                ForEach(state.codeSummaries) { summary in
                    FolderStatusRow(summary: summary) { removing = summary }
                }
            } header: {
                Text("Dossiers de code")
            } footer: {
                HStack {
                    Button(action: state.addCodeFolders) {
                        Label(state.model.hasCode ? "Ajouter des dossiers…" : "Choisir des dossiers de code…", systemImage: "plus")
                    }
                    Spacer()
                }
            }
            if state.model.hasCode {
                Section {
                    ForEach(state.model.settings.codeExcluded, id: \.path) { folder in
                        HStack {
                            Image(nsImage: NSWorkspace.shared.icon(forFile: folder.path))
                                .resizable()
                                .frame(width: 18, height: 18)
                            Text(tildePath(folder.path)).lineLimit(1).truncationMode(.middle)
                            Spacer()
                            Button {
                                state.app.applyCodeFolders(state.model.settings.codeRoots,
                                                           excluded: state.model.settings.codeExcluded.filter { $0 != folder })
                            } label: {
                                Image(systemName: "minus.circle.fill").foregroundStyle(.secondary)
                            }
                            .buttonStyle(.plain)
                            .help("Refouiller le code de ce dossier")
                        }
                    }
                    if state.model.settings.codeExcluded.isEmpty {
                        Text("Aucun. Ce que git ignore, le code tiers et le code généré sont ignorés de toute façon.")
                            .foregroundStyle(.secondary)
                    }
                } header: {
                    Text("Ignorer ces dossiers")
                } footer: {
                    HStack {
                        Button {
                            state.app.pickFolders(for: NSApp.keyWindow, prompt: "Ignorer") { folders in
                                guard !folders.isEmpty else { return }
                                let excluded = state.model.settings.codeExcluded
                                state.app.applyCodeFolders(state.model.settings.codeRoots,
                                                           excluded: excluded + folders.filter { !excluded.contains($0) })
                            }
                        } label: {
                            Label("Ignorer un dossier…", systemImage: "plus")
                        }
                        Spacer()
                    }
                }
            }
            Section {
                Picker("Ouvrir le code dans", selection: Binding(get: { CodeEditor.chosen(state.model.settings.codeEditor).id },
                                                          set: { state.app.setCodeEditor($0) })) {
                    ForEach(CodeEditor.installed) { editor in Text(editor.name).tag(editor.id) }
                    Text(CodeEditor.defaultApp.name).tag(CodeEditor.defaultApp.id)
                }
            } footer: {
                Text("Un résultat s'y ouvre à la ligne trouvée. Espace affiche tout le fichier en Coup d'œil.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Section {
                Text("L'index du code garde le texte de ton code sur ce Mac, comme l'index principal pour tes documents. "
                     + "Il ignore ce que git ignore, le code tiers ou généré, les données et tout ce qui ressemble "
                     + "à une clé ou un mot de passe.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if state.model.hasCode {
                    HStack {
                        Spacer()
                        Button("Désactiver la recherche de code…", role: .destructive) { turningOff = true }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .onChange(of: state.model.status) { state.statusChanged() }
        .confirmationDialog("Désactiver la recherche de code ?", isPresented: $turningOff) {
            Button("Désactiver", role: .destructive) {
                state.app.applyCodeFolders([], excluded: [])
                Task { await state.refresh() }
            }
        } message: {
            Text("L'index du code (\(bytes(state.codeIndexSize))) sera supprimé. Le réactiver relira le code.")
        }
        .confirmationDialog("Arrêter de fouiller le code de « \(removing.map { ($0.path as NSString).lastPathComponent } ?? "") » ?",
                            isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } })) {
            Button("Retirer", role: .destructive) {
                guard let removing else { return }
                state.app.removeCodeFolder(URL(fileURLWithPath: removing.path))
                Task { await state.refresh() }
            }
        } message: {
            Text("Ses \(removing?.files.formatted() ?? "") fichiers de code quittent l'index du code.")
        }
    }
}

/// "~/Library/…/Models" that shows the folder in Finder.
private struct PathButton: View {
    let url: URL

    var body: some View {
        Button {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } label: {
            HStack(spacing: 4) {
                Text(tildePath(url.path)).lineLimit(1).truncationMode(.middle)
                Image(systemName: "arrow.right.circle.fill").foregroundStyle(.secondary)
            }
        }
        .buttonStyle(.plain)
        .help("Afficher dans le Finder")
    }
}
