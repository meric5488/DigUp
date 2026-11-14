import AppKit
import DigUpKit
import SwiftUI

/// Settings → Folders → Add Folders…: a sheet like onboarding's folder step. It lists the usual folders that aren't
/// searched yet and any other folder you choose, each with what's in it, how long its first pass takes and its
/// subfolders; nothing changes until Add. With nothing left to suggest, it starts from the open panel.
///
/// With `code`, Settings → Code's: folders for code search, each opening to its repos, with how long reading them
/// takes and how much room their index needs.
final class AddFoldersController {
    private unowned let app: AppController
    private(set) var choices: FolderChoices?
    private var sheet: NSWindow?
    let code: Bool
    /// The folders changed (Settings refreshes its list).
    var onAdded: () -> Void = {}

    init(app: AppController, code: Bool = false) {
        self.app = app
        self.code = code
    }

    var isOpen: Bool { choices != nil }

    func begin(on window: NSWindow?) {
        guard choices == nil else { return }
        let settings = app.model.settings
        let selection = code
            ? FolderSelection(skipped: settings.codeExcluded.map(\.path), kept: settings.codeRoots.map(\.path))
            : FolderSelection(skipped: settings.excludedFolders.map(\.path), kept: settings.roots.map(\.path))
        let suggestions = (code ? CodeFolders.candidates(searched: settings.roots) : OnboardingState.candidates())
            .filter { !selection.isSearched($0.path) }
        let choices = FolderChoices(rows: suggestions, selection: selection, types: code ? [] : settings.excludedTypes,
                                    code: code)
        self.choices = choices
        // Snapshot runs never put an open panel on screen (-debugChoices picks folders instead).
        guard suggestions.isEmpty, !AppController.offscreen else { return present(choices, on: window) }
        app.pickFolders(for: window) { [weak self] picked in
            guard let self else { return }
            guard !picked.isEmpty else {
                self.choices = nil
                return
            }
            choices.add(picked)
            present(choices, on: window)
        }
    }

    private func present(_ choices: FolderChoices, on window: NSWindow?) {
        let view = AddFoldersView(choices: choices,
                                  pick: { [weak self] in self?.pick() },
                                  cancel: { [weak self] in self?.end() },
                                  add: { [weak self] in self?.add() })
        let sheet = NSWindow(contentViewController: NSHostingController(rootView: view))
        sheet.styleMask = [.titled]
        sheet.title = code ? "Dossiers de code" : "Ajouter des dossiers"
        self.sheet = sheet
        choices.startEstimates()
        log("add folders: \(choices.rows.count) to choose from")
        guard !AppController.offscreen else { return }   // snapshot runs draw it without showing it
        if let window, window.isVisible { window.beginSheet(sheet) } else { app.present(sheet) }
    }

    private func pick() {
        app.pickFolders(for: sheet) { [weak self] picked in
            guard !picked.isEmpty else { return }
            self?.choices?.add(picked)
        }
    }

    func add() {
        guard let choices else { return }
        log("add \(code ? "code " : "")folders: \(choices.roots.map { tildePath($0.path) })"
            + (choices.skipped.isEmpty ? "" : " minus \(choices.skipped.map { tildePath($0.path) })"))
        if code {
            app.applyCodeFolders(choices.roots, excluded: choices.skipped)
        } else {
            app.applyFolders(choices.roots, excluded: choices.skipped)
        }
        end()
        onAdded()
    }

    func end() {
        if let sheet {
            if let parent = sheet.sheetParent { parent.endSheet(sheet) } else { sheet.close() }
        }
        sheet = nil
        choices = nil
    }

    func snapshot(to url: URL) throws {
        try Snapshot.write(sheet, to: url)
    }
}

private struct AddFoldersView: View {
    let choices: FolderChoices
    let pick: () -> Void
    let cancel: () -> Void
    let add: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 16) {
                if choices.code {
                    StepTitle(title: "Chercher dans ton code",
                              subtitle: "Choisis les dossiers qui contiennent tes projets ; ouvre-en un pour choisir parmi ses dépôts. "
                                  + "Fouineur lit chacun une fois, puis seulement ce qui change. Le code se cherche à part : tape "
                                  + "code: dans le panneau de recherche.")
                } else {
                    StepTitle(title: "Ajouter des dossiers à fouiller",
                              subtitle: "Fouineur lit un nouveau dossier une fois, puis suit ce qui y change. "
                                  + "Ouvre un dossier pour choisir certains de ses sous-dossiers.")
                }
                ScrollView {
                    FolderList(choices: choices, addTitle: "Choisir un autre dossier…", add: pick)
                    if choices.code { CodeFolderNote().padding(.top, 8) }
                }
                .scrollBounceBehavior(.basedOnSize)
            }
            .padding(.horizontal, 28)
            .padding(.top, 24)
            .padding(.bottom, 12)
            Divider()
            HStack(spacing: 12) {
                Text(summary)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer(minLength: 8)
                Button("Annuler", action: cancel)
                    .keyboardShortcut(.cancelAction)
                Button("Ajouter", action: add)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!choices.changesSomething)
            }
            .controlSize(.large)
            .padding(.horizontal, 20)
            .frame(height: 60)
        }
        .frame(width: 600, height: 540)
    }

    private var summary: String {
        guard choices.changesSomething else { return "Coche les dossiers à ajouter" }
        guard choices.estimatesDone else { return "Examen des dossiers…" }
        let time = choices.totalSeconds < 1 ? "moins d'une seconde" : "environ \(roughDuration(choices.totalSeconds))"
        if choices.code {
            return "Ajoute \(time) d'indexation et \(roughBytes(choices.totalBytes))"
        }
        let later = choices.totalLaterSeconds
        return "Ajoute \(time) d'indexation"
            + (later >= 30 ? ", plus \(roughDuration(later)) pour les longs fichiers" : "")
    }
}

/// What code search leaves out, under a list of code folders.
struct CodeFolderNote: View {
    var body: some View {
        Label {
            Text("La recherche de code lit les fichiers source, les notebooks et la documentation des dépôts. Elle ignore "
                 + "ce que git ignore, le code tiers ou généré, les données et tout ce qui ressemble à une clé ou un mot de passe.")
                .fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: "info.circle")
        }
        .font(.system(size: 11))
        .foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 4)
    }
}
