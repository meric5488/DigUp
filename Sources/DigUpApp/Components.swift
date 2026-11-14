import AppKit
import DigUpKit
import SwiftUI

/// The app's own colors. Icons and brand moments (onboarding's glow and tiles, the example chips) follow the system
/// accent color, as selection and controls do; amber, the icon's D (`Resources/AppIcon`), stays for the glow around
/// the icon and the focused line of a preview. Computed, so a new accent color shows the next time a view draws.
enum Brand {
    static let amber = Color(red: 0.949, green: 0.635, blue: 0.165)    // #F2A22A, the D
    static let ember = Color(red: 0.851, green: 0.482, blue: 0.102)    // #D97B1A, a deeper amber that holds up on white
    /// The accent color, darker (tiles' lower edge, shadows).
    static var moss: Color { accent(mixedWith: .black, by: 0.35) }
    static var leaf: Color { Color(nsColor: .controlAccentColor) }
    /// The accent color, lighter (the onboarding glow).
    static var sprout: Color { accent(mixedWith: .white, by: 0.45) }
    /// Accent icons.
    static var gradient: LinearGradient { LinearGradient(colors: [leaf, moss], startPoint: .top, endPoint: .bottom) }
    /// Tiles with a white symbol on them.
    static var tile: LinearGradient { LinearGradient(colors: [leaf, moss], startPoint: .top, endPoint: .bottom) }
    /// The soft glow behind onboarding's steps.
    static var wash: LinearGradient {
        LinearGradient(colors: [sprout, leaf], startPoint: .topLeading, endPoint: .bottomTrailing)
    }

    private static func accent(mixedWith other: NSColor, by fraction: CGFloat) -> Color {
        let accent = NSColor.controlAccentColor.usingColorSpace(.sRGB) ?? .controlAccentColor
        return Color(nsColor: accent.blended(withFraction: fraction, of: other) ?? accent)
    }
}

// MARK: Liquid Glass

/// Liquid Glass (macOS 26 and later) is for the few controls that float above the content: the window's search
/// field, its buttons and its filters, as one bar. The content (results, previews, examples, badges) keeps system
/// fills, and the panel, a glass surface itself, gets no glass inside it.
extension View {
    /// Liquid Glass in this shape on macOS 26 and later (tinted, and reacting to the pointer when `interactive`);
    /// before, `fallback` fills it.
    @ViewBuilder
    func glass<S: Shape>(in shape: S, tint: Color? = nil, interactive: Bool = false,
                         fallback: some ShapeStyle = Color.primary.opacity(0.06)) -> some View {
        if #available(macOS 26.0, *) {
            self.glassEffect(Glass.regular.tint(tint).interactive(interactive), in: shape)
        } else {
            self.background(shape.fill(fallback))
        }
    }

    /// Glass shapes side by side render together, and blend when closer than `spacing`, on macOS 26 and later.
    @ViewBuilder
    func glassGroup(spacing: CGFloat) -> some View {
        if #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: spacing) { self }
        } else {
            self
        }
    }
}

/// ⇧ ⌘ Space as keycaps.
struct Keycaps: View {
    let caps: [String]
    var size: CGFloat = 12

    var body: some View {
        HStack(spacing: size * 0.3) {
            ForEach(Array(caps.enumerated()), id: \.offset) { _, cap in
                Text(cap)
                    .font(.system(size: size, weight: .medium, design: .rounded))
                    .frame(minWidth: size * 1.6)
                    .padding(.horizontal, size * 0.45)
                    .padding(.vertical, size * 0.28)
                    .background(RoundedRectangle(cornerRadius: size * 0.42, style: .continuous)
                        .fill(Color.primary.opacity(0.07)))
                    .overlay(RoundedRectangle(cornerRadius: size * 0.42, style: .continuous)
                        .strokeBorder(Color.primary.opacity(0.14), lineWidth: 0.5))
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(caps.joined(separator: " "))
    }
}

/// "p. 3" or "0:06" on a result: the page or moment that matched.
struct PlaceBadge: View {
    let text: String
    var onImage = false

    var body: some View {
        Text(text)
            .font(.system(size: 10, weight: .semibold).monospacedDigit())
            .foregroundStyle(onImage ? AnyShapeStyle(.white) : AnyShapeStyle(.primary))
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(Capsule().fill(onImage ? AnyShapeStyle(.black.opacity(0.6)) : AnyShapeStyle(.quaternary)))
    }
}

/// Where a moment sits in a recording: a track with the matched stretch lit.
struct MomentBar: View {
    let start: Double
    let end: Double
    let duration: Double
    /// Drawn over a picture: white on a dark track.
    var onImage = false
    /// Where playback is, while it plays.
    var playhead: Double?

    var body: some View {
        GeometryReader { geometry in
            let width = geometry.size.width
            let from = max(0, min(1, start / duration)), to = max(from, min(1, end / duration))
            ZStack(alignment: .leading) {
                Capsule().fill(onImage ? Color.white.opacity(0.3) : Color.primary.opacity(0.12))
                Capsule()
                    .fill(onImage ? Color.white : Color.accentColor)
                    .frame(width: max(4, width * (to - from)))
                    .offset(x: width * from)
                if let playhead {
                    Circle()
                        .fill(Color.primary)
                        .frame(width: 9, height: 9)
                        .offset(x: width * max(0, min(1, playhead / duration)) - 4.5)
                }
            }
        }
        .frame(height: 4)
        .accessibilityLabel("À \(ResultRow.clock(start)) sur \(ResultRow.clock(duration))")
    }
}

/// What a folder holds, compactly: 📷 136  🖼 104  📄 25 … (the full wording in the tooltip).
struct KindCounts: View {
    let counts: [FileKind: Int]

    var body: some View {
        HStack(spacing: 10) {
            ForEach(FileKind.allCases.filter { (counts[$0] ?? 0) > 0 }, id: \.self) { kind in
                Label(counts[kind]!.formatted(), systemImage: kind.symbol)
                    .labelStyle(CompactLabel())
                    .lineLimit(1)
                    .fixedSize()
            }
        }
        .font(.system(size: 11).monospacedDigit())
        .foregroundStyle(.secondary)
        .help(description)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(description)
    }

    /// "136 screenshots, 104 images, 25 PDFs"
    var description: String {
        FileKind.allCases.compactMap { kind in
            counts[kind].flatMap { $0 > 0 ? kind.counted($0) : nil }
        }.joined(separator: ", ")
    }
}

private struct CompactLabel: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 3) {
            configuration.icon.font(.system(size: 10))
            configuration.title
        }
    }
}

extension FileKind {
    /// "1 screenshot", "25 PDFs", "6 audio files"
    func counted(_ count: Int) -> String {
        let one = count == 1
        let noun = switch self {
        case .screenshot: one ? "capture d'écran" : "captures d'écran"
        case .image: one ? "image" : "images"
        case .pdf: one ? "PDF" : "PDFs"
        case .doc: one ? "document" : "documents"
        case .audio: one ? "fichier audio" : "fichiers audio"
        case .video: one ? "vidéo" : "vidéos"
        case .code: one ? "fichier de code" : "fichiers de code"
        }
        return "\(count.formatted()) \(noun)"
    }
}

/// System Settings → Privacy & Security → Files and Folders, where a folder denied in macOS's prompt can be allowed.
func openFolderPrivacySettings() {
    if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders") {
        NSWorkspace.shared.open(url)
    }
}

/// Matched words, which FTS marks with « », in bold.
func highlighted(_ text: String, size: CGFloat) -> AttributedString {
    var result = AttributedString()
    var bold = false
    var piece = ""
    func flush() {
        guard !piece.isEmpty else { return }
        var run = AttributedString(piece)
        if bold {
            run.font = .system(size: size, weight: .bold)
            run.foregroundColor = .primary
        }
        result += run
        piece = ""
    }
    for character in text {
        switch character {
        case "«": flush(); bold = true
        case "»": flush(); bold = false
        default: piece.append(character)
        }
    }
    flush()
    return result
}

/// Click, then press the new shortcut; Esc cancels. While it listens, the global hotkey steps aside (`listening`) so
/// that pressing the current one lands here instead of opening the panel.
struct ShortcutRecorder: View {
    let combo: KeyCombo?
    /// Tries a combination: nil when it's set, else why it can't be used.
    let apply: (KeyCombo) -> String?
    let listening: (Bool) -> Void
    var capSize: CGFloat = 15
    @State private var recording = false
    @State private var problem: String?

    var body: some View {
        VStack(spacing: 6) {
            Button { setRecording(!recording) } label: {
                Group {
                    if recording {
                        Text("Appuie sur le nouveau raccourci…").foregroundStyle(.secondary)
                    } else if let combo {
                        Keycaps(caps: combo.caps, size: capSize)
                    } else {
                        Text("Clique pour définir un raccourci").foregroundStyle(.secondary)
                    }
                }
                .font(.system(size: 13))
                .frame(minWidth: 190, minHeight: capSize * 2.4)
                .padding(.horizontal, 12)
                .contentShape(Rectangle())
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .fill(Color.primary.opacity(recording ? 0.03 : 0.05)))
                .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .strokeBorder(recording ? Color.accentColor : Color.primary.opacity(0.15),
                                  lineWidth: recording ? 2 : 1))
            }
            .buttonStyle(.plain)
            .background(KeyCatcher(active: recording, onKey: handle, onResign: { setRecording(false) }))
            .accessibilityLabel(recording ? "Enregistrement d'un nouveau raccourci" : "Raccourci \(combo?.display ?? "aucun"), clique pour le changer")
            Text(problem ?? (recording ? "Échap pour annuler" : "Clique pour changer"))
                .font(.system(size: 11))
                .foregroundStyle(problem == nil ? AnyShapeStyle(.tertiary) : AnyShapeStyle(Color.red))
                .multilineTextAlignment(.center)
        }
        .onDisappear { if recording { listening(false) } }
    }

    private func setRecording(_ on: Bool) {
        guard on != recording else { return }
        recording = on
        if !on { problem = nil }
        listening(on)
    }

    private func handle(_ event: NSEvent) {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask).subtracting([.capsLock, .function,
                                                                                               .numericPad])
        if event.keyCode == 53, flags.isEmpty { return setRecording(false) }   // Esc
        guard let combo = KeyCombo(keyCode: event.keyCode, flags: flags) else {
            problem = "Utilise ⌘, ⌥ ou ⌃ avec une touche"
            return
        }
        if let why = apply(combo) {
            problem = why
        } else {
            setRecording(false)
        }
    }
}

/// Takes key presses (⌘-combinations too, before any menu sees them) while `active`. `onResign`: the window or the
/// view lost the keyboard, so listening must stop (the global hotkey is off meanwhile).
private struct KeyCatcher: NSViewRepresentable {
    let active: Bool
    let onKey: (NSEvent) -> Void
    let onResign: () -> Void

    func makeNSView(context: Context) -> KeyCatcherView { KeyCatcherView() }

    func updateNSView(_ view: KeyCatcherView, context: Context) {
        view.onKey = onKey
        view.onResign = onResign
        guard view.active != active else { return }
        view.active = active
        DispatchQueue.main.async {
            if active {
                view.window?.makeFirstResponder(view)
            } else if view.window?.firstResponder === view {
                view.window?.makeFirstResponder(nil)
            }
        }
    }
}

private final class KeyCatcherView: NSView {
    var active = false
    var onKey: (NSEvent) -> Void = { _ in }
    var onResign: () -> Void = {}
    private var resignObserver: NSObjectProtocol?

    override var acceptsFirstResponder: Bool { active }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver) }
        resignObserver = nil
        guard let window else { return }
        resignObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification,
                                                                object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.active else { return }
                self.onResign()
            }
        }
    }

    override func resignFirstResponder() -> Bool {
        if active { DispatchQueue.main.async { [weak self] in self?.onResign() } }
        return super.resignFirstResponder()
    }

    override func keyDown(with event: NSEvent) {
        if active { onKey(event) } else { super.keyDown(with: event) }
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard active, window?.firstResponder === self else { return super.performKeyEquivalent(with: event) }
        onKey(event)
        return true
    }
}

/// A sidebar's backdrop, matched to Blablateur and Homericc: the desktop blurred through the window (the system
/// sidebar material, which turns opaque by itself with Reduce Transparency) and a faint glow in the accent colour.
struct SidebarBackdrop: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let strength = colorScheme == .dark ? 1.0 : 0.72
        ZStack {
            BehindWindowMaterial()
            RadialGradient(colors: [.accentColor.opacity(0.22 * strength), .clear],
                           center: UnitPoint(x: 0.85, y: -0.1), startRadius: 0, endRadius: 340)
            RadialGradient(colors: [.accentColor.opacity(0.12 * strength), .clear],
                           center: UnitPoint(x: 0.15, y: 1.1), startRadius: 0, endRadius: 300)
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

private struct BehindWindowMaterial: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .sidebar
        view.blendingMode = .behindWindow
        view.state = .followsWindowActiveState
        return view
    }

    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}
