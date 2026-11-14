import AppKit
import DigUpSources
import Observation

/// State the menubar, the panel and the windows share. Engine updates arrive on the main thread.
@Observable
final class AppModel {
    var status = EngineStatus()
    var settings: AppSettings
    var hotkey: KeyCombo?
    /// Opens the window from any app (`AppSettings.windowHotkey`).
    var windowHotkey: KeyCombo?
    let download: ModelDownload
    /// A newer DigUp the daily check found ("0.6.1"), until you open it (`Updates`).
    var updateAvailable: String?
    /// How each source (Mail, Notes, Calendar, Reminders, Photos) is doing (`SourceSyncer`).
    var sourceStates: [SourceKind: SourceState] = [:]

    init(settings: AppSettings, download: ModelDownload) {
        self.settings = settings
        self.download = download
    }

    /// Something to index: chosen folders, or a source.
    var hasFolders: Bool { !settings.roots.isEmpty || settings.sources.anyEnabled }
    /// Code search is on (there are code folders).
    var hasCode: Bool { !settings.codeRoots.isEmpty }

    /// One line for the menu and the windows: what the index is doing.
    var statusLine: String {
        let status = self.status
        guard hasFolders else { return "Aucun dossier choisi pour l'instant" }
        switch status.activity {
        case .starting: return "Démarrage…"
        case .failed(let problem): return "⚠︎ \(problem)"
        case .syncing: return "Vérification des dossiers…"
        case .indexing(let done, let total):
            let percent = total > 0 ? Int((Double(done) / Double(total) * 100).rounded(.down)) : 0
            return "Indexation \(percent) % · \(done.formatted()) sur \(total.formatted())"
        case .finishing(let done, let total, let files):
            let percent = total > 0 ? Int((Double(done) / Double(total) * 100).rounded(.down)) : 0
            let which = files > 0 ? "\(files.formatted()) \(files == 1 ? "long fichier" : "longs fichiers")" : "longs fichiers"
            return "Lecture de la suite des \(which) · \(percent) %"
        case .idle:
            if let line = downloadLine { return line }
            if let code = status.code.indexing, code.total > 0 {
                let percent = Int((Double(code.done) / Double(code.total) * 100).rounded(.down))
                return "Indexation du code \(percent) % · \(code.done.formatted()) sur \(code.total.formatted())"
            }
            let waiting = "\(status.pending.formatted()) \(status.pending == 1 ? "fichier en attente" : "fichiers en attente")"
            if status.pending > 0, status.paused { return "En pause · \(waiting)" }
            if let until = status.heldUntil, !status.paused {
                // Outside the set hours: what's known to wait, and when indexing starts again.
                let when = "indexation à partir de \(until.formatted(date: .omitted, time: .shortened))"
                if status.pending > 0 { return "\(waiting) · \(when)" }
                return "\(status.searchable.formatted()) \(status.searchable == 1 ? "fichier indexé" : "fichiers indexés") · \(when)"
            }
            if status.pending > 0, let reason = status.waitingFor { return "\(waiting) · \(reason)" }
            if status.pending > 0 { return "\(waiting) d'indexation" }
            if status.unfinished > 0 {
                let rest = "\(status.unfinished.formatted()) \(status.unfinished == 1 ? "long fichier" : "longs fichiers") à terminer"
                if status.paused { return "En pause · \(rest)" }
                if let reason = status.waitingFor { return "\(rest) · \(reason)" }
            }
            return "\(status.searchable.formatted()) \(status.searchable == 1 ? "fichier indexé" : "fichiers indexés")"
                + (status.paused ? " · en pause" : "")
        }
    }

    /// While the model isn't there, how its download goes.
    private var downloadLine: String? {
        switch download.phase {
        case .ready: nil
        case .downloading: "Téléchargement du modèle · \(Int(download.fraction * 100)) %"
        case .verifying: "Vérification du modèle téléchargé…"
        case .retrying: "Téléchargement du modèle : en attente du réseau"
        case .failed: "⚠︎ Le téléchargement du modèle s'est arrêté"
        case .needed: status.pending > 0 ? "Le modèle n'est pas encore téléchargé" : nil
        }
    }

    /// 0…1 for the menubar ring and progress bars: the model download, then an indexing pass or the rest of long files.
    var progress: Double? {
        if download.isActive { return download.fraction }
        switch status.activity {
        case .indexing(let done, let total) where total > 0: return Double(done) / Double(total)
        case .finishing(let done, let total, _) where total > 0: return Double(done) / Double(total)
        case .idle:
            guard let code = status.code.indexing, code.total > 0 else { return nil }
            return Double(code.done) / Double(code.total)
        default: return nil
        }
    }

    /// One line about the code index, for Settings → Code: "812 code files indexed", "Indexing code 42%…".
    var codeStatusLine: String {
        let code = status.code
        guard hasCode else { return "La recherche de code est désactivée" }
        if !download.isReady { return "Le code sera lu une fois le modèle téléchargé" }
        if let indexing = code.indexing, indexing.total > 0 {
            let percent = Int((Double(indexing.done) / Double(indexing.total) * 100).rounded(.down))
            return "Indexation du code \(percent) % · \(indexing.done.formatted()) sur \(indexing.total.formatted())"
        }
        let waiting = code.pending > 0 ? " · \(code.pending.formatted()) en attente" : ""
        if code.pending > 0, status.paused { return "En pause · \(code.pending.formatted()) fichiers de code en attente" }
        if code.pending > 0, let reason = status.waitingFor { return "\(code.pending.formatted()) fichiers de code en attente · \(reason)" }
        if code.pending > 0, let until = status.heldUntil {
            return "\(code.pending.formatted()) fichiers de code en attente · indexation à partir de "
                + until.formatted(date: .omitted, time: .shortened)
        }
        // Code is read once the other folders have nothing waiting (the helper's order).
        if code.pending > 0, status.pending > 0 {
            let files = "\(code.pending.formatted()) \(code.pending == 1 ? "fichier" : "fichiers") de code"
            return files + " en attente · lus après tes autres fichiers"
        }
        return "\(code.searchable.formatted()) \(code.searchable == 1 ? "fichier de code indexé" : "fichiers de code indexés")" + waiting
    }

    /// A few words for the search bar while results may be incomplete: "Indexing 42%", "Downloading model · 42%".
    var activityBadge: String? {
        if download.isActive { return "Téléchargement du modèle · \(Int(download.fraction * 100)) %" }
        if case .indexing(let done, let total) = status.activity, total > 0 {
            return "Indexation \(Int(Double(done) / Double(total) * 100)) %"
        }
        if status.paused, status.pending > 0 { return "Indexation en pause" }
        return nil
    }

    /// Why meaning search can't run yet (words still match), or nil.
    var meaningUnavailable: String? {
        guard !download.isReady else { return nil }
        if download.isActive {
            return "Recherche par mots seulement, jusqu'au téléchargement du modèle (\(Int(download.fraction * 100)) %)"
        }
        return "Recherche par mots seulement : le modèle n'est pas téléchargé"
    }

    /// Why results may be incomplete right now, or nil.
    var resultsCaveat: String? {
        if let meaningUnavailable { return meaningUnavailable }
        if case .indexing(let done, let total) = status.activity, total > 0 {
            return "Indexation en cours (\(Int(Double(done) / Double(total) * 100)) %) : certains fichiers manquent encore"
        }
        if status.pending > 0 {
            return "\(status.pending.formatted()) \(status.pending == 1 ? "fichier n'est" : "fichiers ne sont") pas encore \(status.pending == 1 ? "indexé" : "indexés")"
        }
        return nil
    }
}
