import AppKit

// Shared directly with the UI v1 design draft stylesheet (CSS px map to AppKit pt).
enum Design {
    static func font(ofSize size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        let suffix = weight >= .semibold ? "Semibold" : weight >= .medium ? "Medium" : "Regular"
        let cascade = NSFontDescriptor(fontAttributes: [.name: "PingFangSC-" + suffix])
        let descriptor = NSFont.systemFont(ofSize: size, weight: weight).fontDescriptor.addingAttributes([.cascadeList: [cascade]])
        return NSFont(descriptor: descriptor, size: size) ?? .systemFont(ofSize: size, weight: weight)
    }
    static func color(_ light: UInt32, _ dark: UInt32) -> NSColor {
        NSColor(name: nil) { appearance in
            let value = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat((value >> 16) & 255) / 255, green: CGFloat((value >> 8) & 255) / 255, blue: CGFloat(value & 255) / 255, alpha: 1)
        }
    }
    static let surface = color(0xffffff, 0x24272d)
    static let soft = color(0xf4f5f7, 0x2c3037)
    static let ink = color(0x20242b, 0xf0f2f6)
    static let secondary = color(0x606875, 0xb4bcc8)
    static let line = color(0xe1e4e9, 0x3b404a)
    static let strongLine = color(0xc6ccd5, 0x5f6875)
    static let accent = color(0x1263d6, 0x86b5ff)
    static let accentSoft = color(0xeaf2ff, 0x283a55)
    static let accentInk = color(0xffffff, 0x12213a)
    static let green = color(0x26734c, 0x95d4ad)
    static let amber = color(0x8d580d, 0xe8bc74)
    static let amberSoft = color(0xfff5e4, 0x443723)
    static let amberLine = color(0xe8c88f, 0x6e5630)
    static let greenSoft = color(0xe6f2ea, 0x24392c)
}

final class SurfaceView: NSView {
    var fill: NSColor = Design.surface
    var stroke: NSColor? = nil
    var radius: CGFloat = 0
    override var isOpaque: Bool { radius == 0 }
    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: radius, yRadius: radius)
        fill.setFill(); if radius == 0 { bounds.fill() } else { path.fill() }
        if let stroke { stroke.setStroke(); path.lineWidth = 1; path.stroke() }
    }
    override func viewDidChangeEffectiveAppearance() { super.viewDidChangeEffectiveAppearance(); needsDisplay = true }
}

final class ActionButton: NSButton {
    enum Style { case standard, primary, subtle, navigation, choice, tab, tinted }
    var style: Style = .standard { didSet { needsDisplay = true } }
    var symbol: String? { didSet { invalidateIntrinsicContentSize(); needsDisplay = true } }
    var buttonHeight: CGFloat = 32 { didSet { invalidateIntrinsicContentSize() } }
    var radius: CGFloat = 6 { didSet { needsDisplay = true } }
    private let handler: () -> Void
    init(_ title: String, primary: Bool = false, action: @escaping () -> Void) {
        handler = action
        super.init(frame: .zero)
        self.title = title; bezelStyle = .regularSquare; isBordered = false
        font = Design.font(ofSize: 12, weight: .medium)
        style = primary ? .primary : .standard
        target = self; self.action = #selector(invoke(_:))
        setContentHuggingPriority(.defaultHigh, for: .horizontal)
        setContentCompressionResistancePriority(.required, for: .horizontal)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }
    // HUD buttons are clicked while the remote client stays frontmost.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var isOpaque: Bool { false }
    override var intrinsicContentSize: NSSize {
        let width = (title as NSString).size(withAttributes: [.font: font ?? Design.font(ofSize: 12)]).width
        return NSSize(width: ceil(width) + 24 + (symbol == nil ? 0 : 23), height: buttonHeight)
    }
    override func draw(_ dirtyRect: NSRect) {
        let selected = state == .on
        var fill = Design.surface, ink = Design.ink, border: NSColor? = Design.strongLine
        switch style {
        case .primary: fill = Design.accent; ink = Design.accentInk; border = nil
        case .subtle: fill = .clear; ink = Design.secondary; border = nil
        case .navigation: fill = selected ? Design.accentSoft : .clear; ink = selected ? Design.accent : Design.secondary; border = nil
        case .choice: fill = selected ? Design.accentSoft : Design.surface; ink = selected ? Design.accent : Design.ink; border = selected ? Design.accent : Design.strongLine
        case .tab: fill = selected ? Design.surface : .clear; ink = selected ? Design.ink : Design.secondary; border = selected ? Design.line : nil
        case .tinted: fill = Design.accentSoft; ink = Design.accent; border = nil
        case .standard: break
        }
        let alpha: CGFloat = isEnabled ? 1 : 0.55
        let corner = min(radius, bounds.height / 2)
        let rect = bounds.insetBy(dx: 0.5, dy: 0.5), path = NSBezierPath(roundedRect: rect, xRadius: corner, yRadius: corner)
        fill.withAlphaComponent(fill.alphaComponent * alpha).setFill(); path.fill()
        if isHighlighted { Design.ink.withAlphaComponent(0.08).setFill(); path.fill() }
        if let border { border.withAlphaComponent(alpha).setStroke(); path.lineWidth = 1; path.stroke() }
        if window?.firstResponder === self {
            Design.accent.withAlphaComponent(0.65).setStroke()
            let ring = NSBezierPath(roundedRect: bounds.insetBy(dx: 2, dy: 2), xRadius: max(0, corner - 1), yRadius: max(0, corner - 1)); ring.lineWidth = 2; ring.stroke()
        }
        let attributes: [NSAttributedString.Key: Any] = [.font: font ?? Design.font(ofSize: 12), .foregroundColor: ink.withAlphaComponent(alpha)]
        let size = (title as NSString).size(withAttributes: attributes)
        let total = size.width + (symbol == nil ? 0 : 23)
        var x = style == .navigation ? 10 : (bounds.width - total) / 2
        if let symbol, let image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)?.withSymbolConfiguration(.init(pointSize: 14, weight: .regular)) {
            let tinted = NSImage(size: NSSize(width: 15, height: 15), flipped: false) { rect in
                image.draw(in: rect); ink.withAlphaComponent(alpha).setFill(); rect.fill(using: .sourceIn); return true
            }
            tinted.draw(in: NSRect(x: x, y: (bounds.height - 15) / 2, width: 15, height: 15)); x += 23
        }
        (title as NSString).draw(at: NSPoint(x: x, y: (bounds.height - size.height) / 2), withAttributes: attributes)
    }
    override func viewDidChangeEffectiveAppearance() { super.viewDidChangeEffectiveAppearance(); needsDisplay = true }
    @objc private func invoke(_ sender: Any?) { handler() }
}

final class DesignCheck: NSButton {
    init(_ title: String, target: AnyObject?, action: Selector?) {
        super.init(frame: .zero); self.title = title; self.target = target; self.action = action
        setButtonType(.switch); isBordered = false; font = Design.font(ofSize: 11)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }
    override var isOpaque: Bool { false }
    override var intrinsicContentSize: NSSize { NSSize(width: (title as NSString).size(withAttributes: [.font: font!]).width + 25, height: 22) }
    override func draw(_ dirtyRect: NSRect) {
        let rect = NSRect(x: 2, y: (bounds.height - 13) / 2, width: 13, height: 13)
        let box = NSBezierPath(roundedRect: rect, xRadius: 2, yRadius: 2)
        (state == .on ? Design.accent : Design.surface).setFill(); box.fill()
        (state == .on ? Design.accent : Design.strongLine).setStroke(); box.stroke()
        if state == .on { ("✓" as NSString).draw(at: NSPoint(x: 3, y: rect.minY - 1), withAttributes: [.font: Design.font(ofSize: 11, weight: .medium), .foregroundColor: Design.accentInk]) }
        let attributes: [NSAttributedString.Key: Any] = [.font: font!, .foregroundColor: Design.secondary]
        let size = (title as NSString).size(withAttributes: attributes)
        (title as NSString).draw(at: NSPoint(x: 23, y: (bounds.height - size.height) / 2), withAttributes: attributes)
        if window?.firstResponder === self { Design.accent.setStroke(); let ring = NSBezierPath(roundedRect: rect.insetBy(dx: -2, dy: -2), xRadius: 3, yRadius: 3); ring.stroke() }
    }
}

final class DesignPopUp: NSPopUpButton {
    override var isOpaque: Bool { false }
    override var intrinsicContentSize: NSSize { NSSize(width: 140, height: 28) }
    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 5, yRadius: 5)
        Design.surface.setFill(); path.fill(); Design.strongLine.setStroke(); path.stroke()
        let attributes: [NSAttributedString.Key: Any] = [.font: font ?? Design.font(ofSize: 11), .foregroundColor: Design.ink]
        let text = (titleOfSelectedItem ?? "") as NSString, size = text.size(withAttributes: attributes)
        text.draw(at: NSPoint(x: 9, y: (bounds.height - size.height) / 2), withAttributes: attributes)
        let arrow = NSBezierPath(); arrow.move(to: NSPoint(x: bounds.width - 16, y: bounds.midY + (isFlipped ? -2 : 2))); arrow.line(to: NSPoint(x: bounds.width - 12, y: bounds.midY + (isFlipped ? 2 : -2))); arrow.line(to: NSPoint(x: bounds.width - 8, y: bounds.midY + (isFlipped ? -2 : 2))); Design.secondary.setStroke(); arrow.lineWidth = 1.2; arrow.stroke()
        if window?.firstResponder === self { Design.accent.setStroke(); path.lineWidth = 2; path.stroke() }
    }
}

enum NativeUI {
    static func label(_ text: String, size: CGFloat = 12, weight: NSFont.Weight = .regular, secondary: Bool = false) -> NSTextField {
        let label = NSTextField(wrappingLabelWithString: text)
        label.font = Design.font(ofSize: size, weight: weight)
        label.textColor = secondary ? Design.secondary : Design.ink
        label.maximumNumberOfLines = 0
        let paragraph = NSMutableParagraphStyle()
        paragraph.minimumLineHeight = ceil(size * (size >= 19 ? 1.4 : 1.6))
        paragraph.maximumLineHeight = paragraph.minimumLineHeight
        label.attributedStringValue = NSAttributedString(string: text, attributes: [.font: label.font!, .foregroundColor: label.textColor!, .paragraphStyle: paragraph])
        label.setContentCompressionResistancePriority(.required, for: .vertical)
        return label
    }
    static func column(_ views: [NSView] = [], spacing: CGFloat = 12) -> NSStackView {
        let stack = NSStackView(views: views)
        stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = spacing
        stack.translatesAutoresizingMaskIntoConstraints = false
        return stack
    }
    static func fillWidth(_ stack: NSStackView) {
        for child in stack.arrangedSubviews {
            if let button = child as? ActionButton, button.style != .navigation { continue }
            child.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        }
    }
    static func row(_ title: String, detail: String, control: NSView) -> NSView {
        let labels = column([label(title, weight: .medium), label(detail, size: 10, secondary: true)], spacing: 5)
        labels.setContentHuggingPriority(.defaultLow, for: .horizontal)
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let spacer = NSView(); spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        spacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let row = NSStackView(views: [labels, spacer, control]); row.spacing = 16; row.alignment = .centerY; row.distribution = .fill
        control.setContentHuggingPriority(.required, for: .horizontal)
        control.setContentCompressionResistancePriority(.required, for: .horizontal)
        return row
    }
    static func separator() -> NSView {
        let line = SurfaceView(); line.fill = Design.line
        line.heightAnchor.constraint(equalToConstant: 1).isActive = true
        return line
    }
    static func group(_ rows: [NSView]) -> NSView {
        let box = SurfaceView(); box.radius = 9; box.stroke = Design.line
        let stack = column(spacing: 0)
        for (index, row) in rows.enumerated() {
            if index > 0 { stack.addArrangedSubview(separator()) }
            let padded = NSView(); place(row, in: padded, inset: 14, bottom: true)
            padded.heightAnchor.constraint(greaterThanOrEqualToConstant: 65).isActive = true
            stack.addArrangedSubview(padded)
        }
        fillWidth(stack); place(stack, in: box, inset: 1, bottom: true)
        return box
    }
    // Real macOS traffic lights remain in the standard title bar; the content
    // chrome below matches the 45 pt title strip in the approved draft.
    static func windowBody(_ window: NSWindow, title: String, width: CGFloat, height: CGFloat) -> NSView {
        window.styleMask.insert(.fullSizeContentView)
        window.titleVisibility = .hidden; window.titlebarAppearsTransparent = true
        window.setContentSize(NSSize(width: width, height: height))
        window.contentMinSize = NSSize(width: width, height: height)
        window.backgroundColor = Design.surface
        let root = SurfaceView(); window.contentView = root
        let chrome = SurfaceView(); chrome.fill = Design.soft
        chrome.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(chrome)
        let caption = label(title, size: 12, weight: .semibold)
        caption.translatesAutoresizingMaskIntoConstraints = false; chrome.addSubview(caption)
        let line = separator(); line.translatesAutoresizingMaskIntoConstraints = false; chrome.addSubview(line)
        let body = SurfaceView(); body.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(body)
        NSLayoutConstraint.activate([
            chrome.topAnchor.constraint(equalTo: root.topAnchor), chrome.leadingAnchor.constraint(equalTo: root.leadingAnchor), chrome.trailingAnchor.constraint(equalTo: root.trailingAnchor), chrome.heightAnchor.constraint(equalToConstant: 45),
            caption.leadingAnchor.constraint(equalTo: chrome.leadingAnchor, constant: 80), caption.centerYAnchor.constraint(equalTo: chrome.centerYAnchor),
            line.bottomAnchor.constraint(equalTo: chrome.bottomAnchor), line.leadingAnchor.constraint(equalTo: chrome.leadingAnchor), line.trailingAnchor.constraint(equalTo: chrome.trailingAnchor),
            body.topAnchor.constraint(equalTo: chrome.bottomAnchor), body.leadingAnchor.constraint(equalTo: root.leadingAnchor), body.trailingAnchor.constraint(equalTo: root.trailingAnchor), body.bottomAnchor.constraint(equalTo: root.bottomAnchor)
        ])
        return body
    }
    static func clientPicker(profile: RemoteClientProfile, cards: Bool = false, changed: @escaping (RemoteClientProfile) -> Void) -> NSView {
        let outer = NSView()
        let surface = SurfaceView(); surface.fill = cards ? Design.surface : Design.soft
        surface.radius = 7; surface.stroke = cards ? nil : Design.line
        surface.translatesAutoresizingMaskIntoConstraints = false; outer.addSubview(surface)
        let row = NSStackView(); row.spacing = cards ? 12 : 3
        for (name, symbol, client) in [("Windows App", "square.grid.2x2", RemoteClientProfile.windowsApp), ("UU 远程", "display", .uu)] {
            let button = ActionButton(name) { changed(client) }
            button.symbol = symbol; button.style = cards ? .choice : .tab
            button.font = Design.font(ofSize: cards ? 12 : 11); button.buttonHeight = cards ? 44 : 30
            button.state = client == profile ? .on : .off
            button.setAccessibilityValue(client == profile ? "已选择" : "未选择")
            row.addArrangedSubview(button)
        }
        place(row, in: surface, inset: cards ? 0 : 3, bottom: true)
        NSLayoutConstraint.activate([surface.leadingAnchor.constraint(equalTo: outer.leadingAnchor), surface.topAnchor.constraint(equalTo: outer.topAnchor), surface.bottomAnchor.constraint(equalTo: outer.bottomAnchor), surface.trailingAnchor.constraint(lessThanOrEqualTo: outer.trailingAnchor)])
        return outer
    }
    static func place(_ content: NSView, in parent: NSView, inset: CGFloat = 24, bottom: Bool = false) {
        content.translatesAutoresizingMaskIntoConstraints = false; parent.addSubview(content)
        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: parent.leadingAnchor, constant: inset),
            content.trailingAnchor.constraint(equalTo: parent.trailingAnchor, constant: -inset),
            content.topAnchor.constraint(equalTo: parent.topAnchor, constant: inset)
        ])
        if bottom { content.bottomAnchor.constraint(equalTo: parent.bottomAnchor, constant: -inset).isActive = true }
    }
    static func present(_ window: NSWindow) {
        if !window.isVisible { window.center() }
        window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
    }
}

final class PassivePanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

// Passive status surface at the bottom of the screen. It never becomes key,
// so its buttons can be clicked while the remote client stays frontmost.
// Separate from Composer so a delayed dismissal can never erase a new IME draft.
final class StatusHUD {
    enum Tone { case info, success, warning, test }
    struct Action {
        let title: String
        var primary = false
        let run: () -> Void
    }
    struct Option {
        let title: String
        let detail: String
        let action: Action
    }
    static let width: CGFloat = 440
    let panel = PassivePanel(contentRect: NSRect(x: 0, y: 0, width: StatusHUD.width, height: 56), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
    private let surface = SurfaceView()
    private var dismiss: DispatchWorkItem?
    private(set) var title = ""
    private(set) var isSticky = false

    init() {
        panel.level = .floating; panel.hidesOnDeactivate = false; panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.backgroundColor = .clear; panel.isOpaque = false; panel.hasShadow = true
        panel.setAccessibilityLabel("远程语音输入状态")
        panel.contentView = surface
    }
    var isVisible: Bool { panel.isVisible }
    // duration nil keeps the HUD until an action, a new HUD or hide().
    func show(_ title: String, detail: String? = nil, tone: Tone = .info, actions: [Action] = [], options: [Option] = [], duration: TimeInterval? = 3) {
        hide(); configure(title, detail: detail, tone: tone, actions: actions, options: options)
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        if let frame = screen?.visibleFrame { panel.setFrameOrigin(NSPoint(x: frame.midX - Self.width / 2, y: frame.minY + 72)) }
        panel.orderFront(nil); panel.invalidateShadow()
        isSticky = duration == nil
        guard let duration else { return }
        let work = DispatchWorkItem { [weak self] in self?.hide() }
        dismiss = work; DispatchQueue.main.asyncAfter(deadline: .now() + duration, execute: work)
    }
    func hide() { dismiss?.cancel(); dismiss = nil; isSticky = false; panel.orderOut(nil) }
    func configure(_ title: String, detail: String? = nil, tone: Tone = .info, actions: [Action] = [], options: [Option] = []) {
        self.title = title
        surface.subviews.forEach { $0.removeFromSuperview() }
        let pill = detail == nil && options.isEmpty
        surface.radius = pill ? 28 : 16
        surface.fill = tone == .warning ? Design.amberSoft : Design.surface
        surface.stroke = tone == .warning ? Design.amberLine : tone == .test ? Design.accent : Design.strongLine
        surface.needsDisplay = true
        var leading: NSView
        switch tone {
        case .test:
            let chip = NativeUI.label("测试", size: 11, weight: .semibold); chip.textColor = Design.accentInk
            let box = SurfaceView(); box.fill = Design.accent; box.radius = 9
            NativeUI.place(chip, in: box, inset: 0, bottom: true)
            chip.alignment = .center
            box.widthAnchor.constraint(equalToConstant: 38).isActive = true; box.heightAnchor.constraint(equalToConstant: 20).isActive = true
            leading = box
        default:
            let symbol = tone == .success ? "checkmark.circle.fill" : tone == .warning ? "exclamationmark.circle" : "info.circle"
            let icon = NSImageView(image: NSImage(systemSymbolName: symbol, accessibilityDescription: nil)!)
            icon.contentTintColor = tone == .success ? Design.green : tone == .warning ? Design.amber : Design.accent
            icon.symbolConfiguration = .init(pointSize: 17, weight: .regular)
            icon.widthAnchor.constraint(equalToConstant: 20).isActive = true
            leading = icon
        }
        let heading = NativeUI.label(title, size: 13, weight: detail == nil ? .regular : .semibold)
        if tone == .warning { heading.textColor = Design.amber }
        heading.maximumNumberOfLines = pill ? 1 : 0; heading.lineBreakMode = .byTruncatingTail
        var texts: [NSView] = [heading]
        if let detail { texts.append(NativeUI.label(detail, size: 11, secondary: true)) }
        let labels = NativeUI.column(texts, spacing: 3)
        labels.setContentHuggingPriority(.defaultLow, for: .horizontal)
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let row = NSStackView(views: [leading, labels]); row.spacing = 11; row.alignment = .centerY; row.distribution = .fill
        for action in actions { row.addArrangedSubview(Self.button(action, hud: self)) }
        let column = NativeUI.column([row], spacing: 10)
        for option in options {
            let box = SurfaceView(); box.fill = Design.surface; box.radius = 9
            let text = NativeUI.column([NativeUI.label(option.title, size: 12, weight: .semibold), NativeUI.label(option.detail, size: 11, secondary: true)], spacing: 2)
            text.setContentHuggingPriority(.defaultLow, for: .horizontal)
            text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
            let line = NSStackView(views: [text, Self.button(option.action, hud: self)]); line.spacing = 10; line.alignment = .centerY; line.distribution = .fill
            NativeUI.place(line, in: box, inset: 10, bottom: true)
            column.addArrangedSubview(box)
        }
        NativeUI.fillWidth(column)
        let inset: CGFloat = pill ? 0 : 14
        column.translatesAutoresizingMaskIntoConstraints = false; surface.addSubview(column)
        NSLayoutConstraint.activate([
            column.leadingAnchor.constraint(equalTo: surface.leadingAnchor, constant: pill ? 18 : 16),
            column.trailingAnchor.constraint(equalTo: surface.trailingAnchor, constant: pill ? -10 : -14),
            pill ? column.centerYAnchor.constraint(equalTo: surface.centerYAnchor) : column.topAnchor.constraint(equalTo: surface.topAnchor, constant: inset)
        ])
        for label in texts.compactMap({ $0 as? NSTextField }) { label.preferredMaxLayoutWidth = Self.width - 120 - CGFloat(actions.count) * 90 }
        surface.layoutSubtreeIfNeeded()
        let height = pill ? 56 : ceil(column.fittingSize.height) + inset * 2
        let old = panel.frame
        panel.setFrame(NSRect(x: old.minX, y: old.minY, width: Self.width, height: height), display: true)
        surface.layoutSubtreeIfNeeded()
    }
    private static func button(_ action: Action, hud: StatusHUD) -> ActionButton {
        let button = ActionButton(action.title, primary: action.primary) { [weak hud] in
            hud?.hide(); action.run()
        }
        button.buttonHeight = 32; button.radius = 16; button.font = Design.font(ofSize: 12, weight: .medium)
        button.setAccessibilityLabel(action.title)
        return button
    }
}
