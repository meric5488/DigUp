import AppKit
import SwiftUI

/// The menubar icon: DigUp's logo (four tiles around a D, `Resources/AppIcon/tray.svg`) in one color, as a template
/// image, so the menubar draws it white or black like the system's icons. While the model downloads or files index,
/// the tiles are faint and come back in a sweep from the top, clockwise, as far as it has come; while indexing is
/// paused, two bars take the D's place.
enum StatusIcon {
    /// `update`: a newer version is waiting, a dot at the top right (in the template's color).
    static func image(progress: Double?, paused: Bool = false, update: Bool = false) -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            // Drawn on a 64-unit grid, like the SVGs.
            let transform = NSAffineTransform()
            transform.scale(by: rect.width / 64)
            transform.concat()
            if let progress {
                tiles(alpha: 0.35)
                NSGraphicsContext.saveGraphicsState()
                let center = NSPoint(x: 32, y: 32)
                let reached = NSBezierPath()
                reached.move(to: center)
                reached.appendArc(withCenter: center, radius: 64, startAngle: 90,
                                  endAngle: 90 - 360 * min(1, max(0, progress)), clockwise: true)
                reached.close()
                reached.addClip()
                tiles(alpha: 1)
                NSGraphicsContext.restoreGraphicsState()
            } else {
                tiles(alpha: 1)
            }
            // The mark in the middle, cut out of the tiles with a gap around it.
            let mark = paused ? bars() : letterD()
            NSGraphicsContext.current?.compositingOperation = .clear
            mark.lineWidth = 9
            mark.stroke()
            NSGraphicsContext.current?.compositingOperation = .sourceOver
            NSColor.black.setFill()
            mark.fill()
            if update {
                // Cut a gap around it, so it reads as a dot on the logo and not part of it.
                NSGraphicsContext.current?.compositingOperation = .clear
                NSBezierPath(ovalIn: NSRect(x: 41, y: 41, width: 25, height: 25)).fill()
                NSGraphicsContext.current?.compositingOperation = .sourceOver
                NSBezierPath(ovalIn: NSRect(x: 45.5, y: 45.5, width: 16, height: 16)).fill()
            }
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "DigUp"
        return image
    }

    private static func tiles(alpha: CGFloat) {
        NSColor.black.withAlphaComponent(alpha).setFill()
        for (x, y) in [(4.0, 4.0), (34.0, 4.0), (4.0, 34.0), (34.0, 34.0)] {
            NSBezierPath(roundedRect: NSRect(x: x, y: y, width: 26, height: 26), xRadius: 7, yRadius: 7).fill()
        }
    }

    /// The D (`menubar.svg`'s, a little larger): x 21…43, y 18.5…45.5.
    private static func letterD() -> NSBezierPath {
        let path = NSBezierPath()
        path.move(to: NSPoint(x: 23.5, y: 18.5))
        path.line(to: NSPoint(x: 29.5, y: 18.5))
        path.appendArc(withCenter: NSPoint(x: 29.5, y: 32), radius: 13.5, startAngle: -90, endAngle: 90)
        path.line(to: NSPoint(x: 23.5, y: 45.5))
        path.appendArc(from: NSPoint(x: 21, y: 45.5), to: NSPoint(x: 21, y: 43), radius: 2.5)
        path.line(to: NSPoint(x: 21, y: 21))
        path.appendArc(from: NSPoint(x: 21, y: 18.5), to: NSPoint(x: 23.5, y: 18.5), radius: 2.5)
        path.close()
        return path
    }

    private static func bars() -> NSBezierPath {
        let path = NSBezierPath(roundedRect: NSRect(x: 22, y: 19, width: 7.5, height: 26), xRadius: 2.5, yRadius: 2.5)
        path.append(NSBezierPath(roundedRect: NSRect(x: 34.5, y: 19, width: 7.5, height: 26), xRadius: 2.5, yRadius: 2.5))
        return path
    }
}

/// The menubar icon and its menu: what's going on (with a progress bar), Search…, the window, Pause, Settings, Quit.
final class StatusMenuController: NSObject, NSMenuDelegate {
    private let model: AppModel
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let menu = NSMenu()
    var onSearch: () -> Void = {}
    var onOpenWindow: () -> Void = {}
    var onSettings: () -> Void = {}
    var onChooseFolders: () -> Void = {}
    var onPauseChanged: (Bool) -> Void = { _ in }
    var onCheckForUpdates: () -> Void = {}
    private var shownProgress: Double?
    private var shownPaused = false
    private var shownUpdate = false

    init(model: AppModel) {
        self.model = model
        super.init()
        menu.delegate = self
        statusItem.menu = menu
        refreshButton()
        observeModel()
    }

    private func observeModel() {
        withObservationTracking {
            _ = model.status
            _ = model.progress
            _ = model.updateAvailable
        } onChange: { [weak self] in
            Task { @MainActor in
                self?.refreshButton()
                self?.observeModel()
            }
        }
    }

    private func refreshButton() {
        guard let button = statusItem.button else { return }
        let progress = model.progress.map { ($0 * 50).rounded() / 50 }   // redraw in 2% steps, not every file
        let paused = model.status.paused, update = model.updateAvailable != nil
        if progress != shownProgress || paused != shownPaused || update != shownUpdate || button.image == nil {
            button.image = StatusIcon.image(progress: progress, paused: paused, update: update)
            shownProgress = progress
            shownPaused = paused
            shownUpdate = update
        }
        button.toolTip = "DigUp — \(model.statusLine)"
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let header = NSMenuItem()
        let view = NSHostingView(rootView: MenuStatusView(model: model))
        view.frame.size = view.fittingSize
        header.view = view
        menu.addItem(header)
        menu.addItem(.separator())
        if let version = model.updateAvailable {
            menu.addItem(ActionMenuItem("Update to DigUp \(version)…") { [weak self] in self?.onCheckForUpdates() })
            menu.addItem(.separator())
        }

        if !model.hasFolders {
            menu.addItem(ActionMenuItem("Choose Folders to Search…") { [weak self] in self?.onChooseFolders() })
        }
        let search = ActionMenuItem("Search…") { [weak self] in self?.onSearch() }
        if let hotkey = model.hotkey, let (key, modifiers) = hotkey.menuEquivalent {
            search.keyEquivalent = key
            search.keyEquivalentModifierMask = modifiers
        }
        menu.addItem(search)
        let open = ActionMenuItem("Open DigUp") { [weak self] in self?.onOpenWindow() }
        if let hotkey = model.windowHotkey, let (key, modifiers) = hotkey.menuEquivalent {
            open.keyEquivalent = key
            open.keyEquivalentModifierMask = modifiers
        }
        menu.addItem(open)
        if model.hasFolders {
            menu.addItem(.separator())
            let paused = model.status.paused
            menu.addItem(ActionMenuItem(paused ? "Resume Indexing" : "Pause Indexing") { [weak self] in
                self?.onPauseChanged(!paused)
            })
        }
        switch model.download.phase {
        case .failed, .needed:
            menu.addItem(ActionMenuItem("Download the Model") { [weak self] in self?.model.download.start() })
        default:
            break
        }
        menu.addItem(.separator())
        menu.addItem(ActionMenuItem("Settings…", key: ",") { [weak self] in self?.onSettings() })
        if model.updateAvailable == nil {
            menu.addItem(ActionMenuItem("Check for Updates…") { [weak self] in self?.onCheckForUpdates() })
        }
        menu.addItem(ActionMenuItem("Quit DigUp", key: "q") { NSApp.terminate(nil) })
    }

    /// The menu as text, for the -debugMenu launch argument (menus can't be clicked from a script).
    func describeMenu() -> String {
        let menu = NSMenu()
        menuNeedsUpdate(menu)
        return menu.items.map { item in
            if item.isSeparatorItem { return "—" }
            if item.view != nil { return "[\(model.statusLine)]" }
            return item.title + (item.isEnabled ? "" : " (disabled)")
        }.joined(separator: " | ")
    }

    /// Runs a menu item by title, for -debugMenu.
    func performItem(titled title: String) {
        let menu = NSMenu()
        menuNeedsUpdate(menu)
        guard let item = menu.items.first(where: { $0.title == title }), let action = item.action else { return }
        NSApp.sendAction(action, to: item.target, from: item)
    }
}

/// The top of the menu: the status line, a detail (where, or how long), and progress while there is some.
struct MenuStatusView: View {
    let model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(model.statusLine)
                .font(.system(size: 13, weight: .semibold))
                .lineLimit(1)
            if let detail {
                Text(detail)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
            if let progress = model.progress {
                ProgressView(value: progress)
                    .controlSize(.small)
                    .padding(.top, 2)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 5)
        .frame(width: 280, alignment: .leading)
    }

    private var detail: String? {
        if model.download.isActive { return model.download.progressText }
        if case .failed(let message) = model.download.phase { return message }
        guard model.hasFolders else { return "Pick the folders it should search." }
        let missing = Set(model.status.missingRoots), unreadable = Set(model.status.unreadableRoots)
        let names = model.settings.roots.map { root in
            root.lastPathComponent + (missing.contains(root.path) ? " (not connected)"
                                      : unreadable.contains(root.path) ? " (no access)" : "")
        }
        return "In " + names.joined(separator: ", ")
    }
}

/// Makes an NSMenuItem that runs a closure.
private final class MenuAction: NSObject {
    let handler: () -> Void

    init(_ handler: @escaping () -> Void) {
        self.handler = handler
    }

    @objc func run() { handler() }
}

func ActionMenuItem(_ title: String, key: String = "", handler: @escaping () -> Void) -> NSMenuItem {
    let action = MenuAction(handler)
    let item = NSMenuItem(title: title, action: #selector(MenuAction.run), keyEquivalent: key)
    item.target = action
    item.representedObject = action  // `target` is weak; this keeps the action alive
    return item
}
