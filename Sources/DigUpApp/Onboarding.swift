import AppKit
import DigUpKit
import Observation
import SwiftUI

/// First launch, in four steps: what DigUp does, which folders to search (with how long the first pass takes), the
/// hotkey (try it), and done; a fifth, code search, when there are folders with code on the Mac (it stays off unless
/// you turn it on there). The model downloads from Get Started on, shown at the bottom of every later step, and
/// the chosen folders start indexing as soon as you continue past them.
final class OnboardingController: NSObject, NSWindowDelegate {
    let state: OnboardingState
    private unowned let app: AppController
    private var window: NSWindow?

    init(app: AppController) {
        self.app = app
        state = OnboardingState(app: app)
    }

    var isVisible: Bool { window?.isVisible == true }

    func show() {
        let window = self.window ?? makeWindow()
        app.present(window)
    }

    func close() {
        window?.close()
    }

    private func makeWindow() -> NSWindow {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: OnboardingView.size.width,
                                                  height: OnboardingView.size.height),
                              styleMask: [.titled, .closable, .fullSizeContentView], backing: .buffered, defer: false)
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = true
        window.title = "Bienvenue dans Fouineur"
        window.isReleasedWhenClosed = false
        window.delegate = self
        state.close = { [weak self] in self?.close() }
        window.contentView = NSHostingView(rootView: OnboardingView(state: state))
        window.center()
        self.window = window
        return window
    }

    func windowWillClose(_ notification: Notification) {
        state.leaving()
        window?.contentView = nil
        window = nil
        app.onboardingClosed()
    }

    func snapshot(to url: URL) throws {
        try Snapshot.write(window, to: url)
    }
}

@Observable
final class OnboardingState {
    enum Step: Int, CaseIterable {
        case welcome, folders, code, shortcut, ready
    }

    private(set) var step = Step.welcome
    /// The folders to search, with their estimates and subfolders; applied on Continue.
    let folders: FolderChoices
    /// Code search's folders, offered when the Mac has folders with code (`CodeFolders`); applied on Continue when
    /// `searchCode` is on.
    let codeFolders: FolderChoices
    var searchCode = false {
        didSet { if searchCode { codeFolders.startEstimates() } }
    }
    /// You pressed the hotkey on the shortcut step.
    private(set) var hotkeyWorked = false
    var launchAtLogin: Bool
    var indexOnBattery: Bool
    /// File types to skip as typed ("heic, txt"): applied on Return, and on Continue (a click there doesn't end the
    /// field's editing).
    var skippedTypesText: String
    @ObservationIgnored let app: AppController
    @ObservationIgnored var close: () -> Void = {}

    init(app: AppController) {
        self.app = app
        let settings = app.model.settings
        let candidates = Self.candidates()
        let chosen = settings.roots
        let selection = FolderSelection(chosen: (chosen.isEmpty ? candidates : chosen).map(\.path),
                                        skipped: settings.excludedFolders.map(\.path))
        // A chosen subfolder of a folder listed shows in that folder's list, not as a folder of its own.
        let others = chosen.filter { root in
            !candidates.contains { $0.path == root.path || $0.path == FolderSelection.parent(root.path) }
        }
        folders = FolderChoices(rows: candidates + others, selection: selection, types: settings.excludedTypes)
        let codeCandidates = CodeFolders.candidates(searched: [])
        codeFolders = FolderChoices(rows: codeCandidates,
                                    selection: FolderSelection(chosen: settings.codeRoots.isEmpty
                                                                   ? codeCandidates.map(\.path)
                                                                   : settings.codeRoots.map(\.path)),
                                    types: [], code: true)
        searchCode = !settings.codeRoots.isEmpty
        launchAtLogin = app.launchesAtLogin || app.suggestsLaunchAtLogin
        indexOnBattery = settings.indexOnBattery
        skippedTypesText = settings.excludedTypes.joined(separator: ", ")
    }

    var model: AppModel { app.model }
    var download: ModelDownload { app.download }

    /// The steps this Mac gets: code search's only when there's code to offer.
    var steps: [Step] {
        Step.allCases.filter { $0 != .code || !codeFolders.rows.isEmpty }
    }

    /// The folders it offers: the usual home folders that exist, or `-onboardingLocations a:b:c` (tests point it at
    /// the testbed, never at real folders).
    static func candidates() -> [URL] {
        if let list = UserDefaults.standard.string(forKey: "onboardingLocations") {
            return list.split(separator: ":").map { AppSettings.folderURL(String($0)) }
        }
        // A dev or test run (its own defaults suite) never offers, and so never estimates, the real home folders.
        if UserDefaults.standard.string(forKey: "defaultsSuite") != nil {
            log("onboarding: no -onboardingLocations in a dev run, so no folders are offered")
            return []
        }
        let home = FileManager.default.homeDirectoryForCurrentUser
        return ["Desktop", "Documents", "Downloads", "Pictures", "Movies", "Music"]
            .map { AppSettings.folderURL(home.appendingPathComponent($0).path) }
            .filter(AppSettings.isFolder)
    }

    // MARK: Steps

    func next() {
        switch step {
        case .welcome:
            download.start()   // the go-ahead for the one download
            go(to: .folders)
        case .folders:
            // Indexing starts now (file names first; meaning once the model is in), while you set up the rest.
            folders.setTypes(skippedTypesText)
            app.applyFolders(folders.roots, excluded: folders.skipped, types: folders.types)
            go(to: steps.contains(.code) ? .code : .shortcut)
        case .code:
            if searchCode, !codeFolders.roots.isEmpty {
                app.applyCodeFolders(codeFolders.roots, excluded: codeFolders.skipped)
            } else if !searchCode, model.hasCode {
                app.applyCodeFolders([], excluded: [])
            }
            go(to: .shortcut)
        case .shortcut:
            go(to: .ready)
        case .ready:
            finish()
        }
    }

    func back() {
        guard let index = steps.firstIndex(of: step), index > 0 else { return }
        go(to: steps[index - 1])
    }

    func go(to step: Step) {
        self.step = step
        app.hotkeyTester = step == .shortcut ? { [weak self] in self?.hotkeyWorked = true } : nil
        if step == .folders { folders.startEstimates() }
    }

    func finish() {
        if indexOnBattery != model.settings.indexOnBattery { app.setIndexOnBattery(indexOnBattery) }
        if launchAtLogin != app.launchesAtLogin { _ = app.setLaunchAtLogin(launchAtLogin) }
        log("onboarding: done (\(model.settings.roots.count) folders, login item \(launchAtLogin ? "on" : "off"))")
        close()
    }

    /// The window is closing (Done or its close button).
    func leaving() {
        app.hotkeyTester = nil
    }

    // MARK: Folders (applied on Continue, from the first pass on; Settings has the same for later)

    func addFolders(from window: NSWindow?) {
        app.pickFolders(for: window) { [weak self] picked in
            guard !picked.isEmpty else { return }
            self?.folders.add(picked)
        }
    }

    func skipFolders(from window: NSWindow?) {
        app.pickFolders(for: window, prompt: "Ignorer") { [weak self] picked in
            guard !picked.isEmpty else { return }
            self?.folders.skip(picked)
        }
    }

    /// The step has what it needs to be drawn (debug snapshots wait for this).
    var isLoaded: Bool {
        switch step {
        case .folders: folders.estimatesDone
        case .code: !searchCode || codeFolders.estimatesDone
        default: true
        }
    }

    func addCodeFolders(from window: NSWindow?) {
        app.pickFolders(for: window) { [weak self] picked in
            guard !picked.isEmpty else { return }
            self?.codeFolders.add(picked)
        }
    }
}

// MARK: Views

struct OnboardingView: View {
    static let size = CGSize(width: 680, height: 600)
    @Bindable var state: OnboardingState

    var body: some View {
        VStack(spacing: 0) {
            Group {
                switch state.step {
                case .welcome: WelcomeStep()
                case .folders: FoldersStep(state: state)
                case .code: CodeStep(state: state)
                case .shortcut: ShortcutStep(state: state)
                case .ready: ReadyStep(state: state)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: state.step == .welcome ? .center : .top)
            .padding(.horizontal, 48)
            .padding(.top, 46)
            Divider()
            footer
        }
        .frame(width: Self.size.width, height: Self.size.height)
        .background(alignment: .top) {
            // A soft glow behind the top of each step, in the accent color.
            Brand.wash
                .frame(height: 260)
                .mask(LinearGradient(colors: [.black.opacity(0.10), .clear], startPoint: .top, endPoint: .bottom))
                .ignoresSafeArea()
        }
    }

    private var footer: some View {
        HStack(spacing: 14) {
            StepDots(current: state.steps.firstIndex(of: state.step) ?? 0, count: state.steps.count)
            if state.step != .welcome {
                DownloadStatus(download: state.download)
            }
            Spacer(minLength: 8)
            if state.step == .folders || state.step == .code && state.searchCode {
                Text(state.step == .code ? codeSummary : foldersSummary)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            if state.step != .welcome, state.step != .ready {
                Button("Retour") { state.back() }
            }
            Button(continueTitle) { state.next() }
                .keyboardShortcut(.defaultAction)
                .disabled(state.step == .folders && state.folders.roots.isEmpty)
        }
        .controlSize(.large)
        .padding(.horizontal, 20)
        .frame(height: 64)
    }

    private var continueTitle: String {
        switch state.step {
        case .welcome: "Commencer"
        case .folders: state.folders.roots.isEmpty ? "Continuer" : "Lancer l'indexation"
        case .code: "Continuer"
        case .shortcut: "Continuer"
        case .ready: "Terminé"
        }
    }

    private var codeSummary: String {
        guard !state.codeFolders.roots.isEmpty else { return "Coche les dossiers qui contiennent ton code" }
        guard state.codeFolders.estimatesDone else { return "Examen de ton code…" }
        let seconds = state.codeFolders.totalSeconds
        return (seconds < 1 ? "Moins d'une seconde" : "Environ \(roughDuration(seconds))")
            + ", \(roughBytes(state.codeFolders.totalBytes))"
    }

    private var foldersSummary: String {
        guard !state.folders.roots.isEmpty else { return "Choisis au moins un dossier" }
        guard state.folders.estimatesDone else { return "Examen de tes dossiers…" }
        return "Premier passage : environ \(roughDuration(state.folders.totalSeconds))"
    }
}

private struct StepDots: View {
    let current: Int
    let count: Int

    var body: some View {
        HStack(spacing: 6) {
            ForEach(0..<count, id: \.self) { index in
                Circle()
                    .fill(index == current ? Color.primary.opacity(0.55) : Color.primary.opacity(0.15))
                    .frame(width: 6, height: 6)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Étape \(current + 1) sur \(count)")
    }
}

/// The model download, small: "Downloading the model · 42% · about 1 min left", then "Model ready".
struct DownloadStatus: View {
    let download: ModelDownload

    var body: some View {
        HStack(spacing: 7) {
            switch download.phase {
            case .ready:
                Image(systemName: "checkmark.circle.fill").foregroundStyle(Brand.leaf)
                Text("Modèle prêt")
            case .failed(let message):
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                Text(message).lineLimit(1)
                Button("Réessayer") { download.start() }.controlSize(.small)
                Button("Utiliser des fichiers…") { download.chooseFiles(for: NSApp.keyWindow) }
                    .controlSize(.small)
                    .help("Tu as téléchargé les deux fichiers du modèle ailleurs ? Choisis-les ici.")
            case .needed:
                Image(systemName: "arrow.down.circle").foregroundStyle(.secondary)
                Text("Modèle non téléchargé")
                Button("Télécharger") { download.start() }.controlSize(.small)
            default:
                ProgressView(value: download.fraction)
                    .progressViewStyle(.circular)
                    .controlSize(.small)
                Text("Téléchargement du modèle · \(download.progressText)")
                    .lineLimit(1)
            }
        }
        .font(.system(size: 12))
        .foregroundStyle(.secondary)
    }
}

private struct WelcomeStep: View {
    var body: some View {
        VStack(spacing: 0) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .frame(width: 96, height: 96)
                .shadow(color: Brand.moss.opacity(0.35), radius: 14, y: 6)
            Text("Fouineur")
                .font(.system(size: 30, weight: .bold))
                .padding(.top, 10)
            Text("Décris-le. Fouineur le déterre.")
                .font(.system(size: 16))
                .foregroundStyle(.secondary)
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 16) {
                Feature(symbol: "camera.viewfinder", title: "Captures d'écran et photos",
                        example: "« cette erreur de paiement », « le zèbre », « le tableau blanc de mardi »")
                Feature(symbol: "doc.text", title: "PDF et documents",
                        example: "« la clause sur les animaux dans le bail », « reçu du vol »")
                Feature(symbol: "waveform", title: "Des moments dans l'audio et la vidéo",
                        example: "« là où ils parlent du sommeil », « le passage avec la batterie »")
            }
            .padding(.top, 30)
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "lock.shield")
                    .font(.system(size: 18))
                    .foregroundStyle(.secondary)
                    .frame(width: 30)
                Text("Tout est indexé et fouillé sur ce Mac. Fouineur télécharge son modèle d'IA (865 Mo) une fois ; "
                     + "ensuite il marche hors ligne, et rien de ce que tu indexes ne quitte ton Mac. Ensuite, Fouineur parcourt "
                     + "tes dossiers habituels pour estimer leur durée ; rien n'est indexé tant que tu n'as pas choisi.")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.top, 26)
            .frame(maxWidth: 470, alignment: .leading)
        }
        .frame(maxWidth: .infinity)
        .padding(.bottom, 40)
    }
}

private struct Feature: View {
    let symbol: String
    let title: String
    let example: String

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: symbol)
                .font(.system(size: 16, weight: .medium))
                .foregroundStyle(.white)
                .frame(width: 34, height: 34)
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Brand.tile))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 14, weight: .semibold))
                Text(example).font(.system(size: 12)).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: 470, alignment: .leading)
    }
}

struct StepTitle: View {
    let title: String
    let subtitle: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 24, weight: .bold))
            Text(subtitle)
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct FoldersStep: View {
    @Bindable var state: OnboardingState

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            StepTitle(title: "Que doit fouiller Fouineur ?",
                      subtitle: "Choisis des dossiers, ou ouvres-en un pour choisir certains de ses sous-dossiers. Tu pourras changer ça "
                          + "à tout moment dans les Réglages.")
            ScrollView {
                VStack(spacing: 6) {
                    FolderList(choices: state.folders, addTitle: "Ajouter un dossier…") {
                        state.addFolders(from: NSApp.keyWindow)
                    }
                    SkipChoices(state: state)
                }
            }
            .scrollBounceBehavior(.basedOnSize)
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "info.circle").foregroundStyle(.secondary)
                Text("macOS demandera ton accord avant que Fouineur lise des dossiers comme Bureau, Documents et Téléchargements. "
                     + "Fouineur ne fait que lire les fichiers : il ne les modifie, ne les déplace et ne les envoie jamais.")
                    .fixedSize(horizontal: false, vertical: true)
            }
            .font(.system(size: 11))
            .foregroundStyle(.secondary)
            .padding(.bottom, 14)
        }
    }
}

/// Folders and file types to leave out, before the first pass reads anything. Folded away unless something is set.
private struct SkipChoices: View {
    @Bindable var state: OnboardingState
    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 10) {
                ForEach(state.folders.selection.effectiveSkips, id: \.self) { folder in
                    HStack(spacing: 8) {
                        Image(nsImage: NSWorkspace.shared.icon(forFile: folder))
                            .resizable()
                            .frame(width: 16, height: 16)
                        Text(tildePath(folder)).lineLimit(1).truncationMode(.middle)
                        Spacer()
                        Button {
                            state.folders.unskip(folder)
                        } label: {
                            Image(systemName: "minus.circle.fill").foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help("Refouiller ce dossier")
                    }
                    .font(.system(size: 12))
                }
                Button {
                    state.skipFolders(from: NSApp.keyWindow)
                } label: {
                    Label("Ignorer un dossier…", systemImage: "plus")
                }
                .controlSize(.small)
                HStack(spacing: 8) {
                    Text("Types de fichiers")
                    TextField("Extensions", text: $state.skippedTypesText, prompt: Text("p. ex. heic, txt"))
                        .labelsHidden()
                        .controlSize(.small)
                        .onSubmit { state.folders.setTypes(state.skippedTypesText) }
                }
                .font(.system(size: 12))
                Text("Le code, les clés, les certificats, les dossiers cachés, les apps et les caches sont toujours ignorés.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.top, 8)
            .padding(.leading, 4)
        } label: {
            Text("Ignorer certains dossiers ou types de fichiers")
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 4)
        .padding(.top, 4)
        .onAppear {
            expanded = !state.folders.selection.effectiveSkips.isEmpty || !state.folders.types.isEmpty
        }
    }
}

/// Code search, if you want it: a switch (off until you turn it on), then the folders with code, each opening to its
/// repos with how long reading them takes and the room their index needs, and the editor results open in.
private struct CodeStep: View {
    @Bindable var state: OnboardingState

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            StepTitle(title: "Fouiller aussi ton code ?",
                      subtitle: "Fouineur peut trouver du code d'après ce qu'il fait : tape code: dans le panneau de recherche, comme "
                          + "« code: retry with backoff ». Il a son propre index et n'apparaît jamais dans les autres "
                          + "recherches. Tu pourras l'activer plus tard dans Réglages → Code.")
            SwitchRow(title: "Fouiller mon code", detail: "Désactivé sauf si tu l'actives.", isOn: $state.searchCode)
                .padding(14)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.04)))
            if state.searchCode {
                ScrollView {
                    VStack(spacing: 6) {
                        FolderList(choices: state.codeFolders, addTitle: "Ajouter un dossier…") {
                            state.addCodeFolders(from: NSApp.keyWindow)
                        }
                        CodeFolderNote().padding(.top, 4)
                    }
                }
                .scrollBounceBehavior(.basedOnSize)
                HStack {
                    Text("Ouvrir le code dans")
                    Picker("Ouvrir le code dans", selection: Binding(get: { CodeEditor.chosen(state.model.settings.codeEditor).id },
                                                              set: { state.app.setCodeEditor($0) })) {
                        ForEach(CodeEditor.installed) { editor in Text(editor.name).tag(editor.id) }
                        Text(CodeEditor.defaultApp.name).tag(CodeEditor.defaultApp.id)
                    }
                    .labelsHidden()
                    .fixedSize()
                    Spacer()
                }
                .font(.system(size: 12))
                .padding(.bottom, 12)
            }
            Spacer(minLength: 0)
        }
    }
}

private struct ShortcutStep: View {
    let state: OnboardingState

    var body: some View {
        VStack(spacing: 0) {
            StepTitle(title: "Ouvre Fouineur de n'importe où",
                      subtitle: "Appuie sur le raccourci dans n'importe quelle app, décris ce que tu cherches, puis appuie sur Retour.")
            ShortcutRecorder(combo: state.model.hotkey, apply: { state.app.setHotkey($0) },
                             listening: { state.app.hotkeyRecording($0) }, capSize: 20)
                .padding(.top, 34)
            Group {
                if state.hotkeyWorked {
                    Label("C'est tout. Ça marche depuis n'importe quelle app.", systemImage: "checkmark.circle.fill")
                        .foregroundStyle(Brand.leaf)
                } else if let hotkey = state.model.hotkey {
                    Text("Essaie maintenant : appuie sur \(hotkey.display).")
                        .foregroundStyle(.secondary)
                }
            }
            .font(.system(size: 13, weight: .medium))
            .padding(.top, 16)
            .animation(.default, value: state.hotkeyWorked)
            VStack(alignment: .leading, spacing: 9) {
                Text("DANS LE PANNEAU DE RECHERCHE")
                    .font(.system(size: 10, weight: .semibold))
                    .tracking(0.6)
                    .foregroundStyle(.secondary)
                KeyTip(caps: ["↩"], text: "Ouvrir le fichier")
                KeyTip(caps: ["⌘", "↩"], text: "L'afficher dans le Finder")
                KeyTip(caps: ["Espace"], text: "Coup d'œil, après avoir choisi un résultat avec les flèches")
                KeyTip(caps: ["⌘", "C"], text: "Copier le fichier, pour le coller n'importe où")
                KeyTip(caps: ["esc"], text: "Fermer")
            }
            .padding(18)
            .frame(maxWidth: 340, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.04)))
            .padding(.top, 34)
        }
    }
}

private struct KeyTip: View {
    let caps: [String]
    let text: String

    var body: some View {
        HStack(spacing: 10) {
            Keycaps(caps: caps, size: 11)
                .frame(width: 66, alignment: .leading)
            Text(text).font(.system(size: 12))
        }
    }
}

private struct ReadyStep: View {
    @Bindable var state: OnboardingState

    var body: some View {
        VStack(spacing: 0) {
            Image(systemName: "checkmark.seal.fill")
                .font(.system(size: 60))
                .foregroundStyle(Brand.gradient)
                .padding(.top, 8)
            Text("Tout est prêt")
                .font(.system(size: 26, weight: .bold))
                .padding(.top, 14)
            Text(progressNote)
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 470)
                .padding(.top, 8)
            VStack(alignment: .leading, spacing: 16) {
                SwitchRow(title: "Ouvrir Fouineur à l'ouverture de session", detail: "Pour que le raccourci marche toujours.",
                          isOn: $state.launchAtLogin)
                Divider()
                SwitchRow(title: AppSettings.batteryTitle, detail: AppSettings.batteryDetail,
                          isOn: $state.indexOnBattery)
            }
            .padding(20)
            .frame(maxWidth: 470, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.04)))
            .padding(.top, 28)
            HStack(spacing: 8) {
                Image(systemName: "sparkle.magnifyingglass")
                Text("Fouineur vit dans la barre des menus, qui montre où en est l'indexation.")
            }
            .font(.system(size: 12))
            .foregroundStyle(.secondary)
            .padding(.top, 22)
        }
        .frame(maxWidth: .infinity)
    }

    private var progressNote: String {
        let seconds = state.folders.totalSeconds, later = state.folders.totalLaterSeconds
        let time = seconds > 0 ? " (environ \(roughDuration(seconds)) sur ce Mac)" : ""
        let rest = later >= 30 ? " Les longs PDF et documents voient leurs premières pages lues dans ce temps, et le reste "
            + "ensuite (environ \(roughDuration(later)) de plus)." : ""
        if state.download.isReady {
            return "Fouineur lit tes dossiers\(time), les fichiers récents d'abord. Tu peux chercher tout de suite ; "
                + "les résultats arrivent au fur et à mesure." + rest
        }
        return "Fouineur commencera à lire tes dossiers\(time) une fois le modèle téléchargé "
            + "(\(state.download.progressText)). D'ici là, la recherche trouve les fichiers par leur nom." + rest
    }
}

/// A setting with a title, a line about it, and a switch on the right.
struct SwitchRow: View {
    let title: String
    let detail: String
    @Binding var isOn: Bool

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .medium))
                Text(detail)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            Toggle(title, isOn: $isOn).labelsHidden().toggleStyle(.switch)
        }
    }
}
