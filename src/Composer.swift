import AppKit

final class CapturePanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}
final class CaptureText: NSTextView {
    var finish: (() -> Void)?
    var cancel: (() -> Void)?
    var submitOnReturn = true
    // Doubao streams recognition as marked text, which never posts
    // textDidChange; every content change, marked or committed, lands here.
    var onContentChange: (() -> Void)?
    private var handlingBridgeKey = false
    func withBridgeKey(_ work: () -> Void) {
        handlingBridgeKey = true
        defer { handlingBridgeKey = false }
        work()
    }
    override func keyDown(with event: NSEvent) {
        if event.cgEvent?.getIntegerValueField(.eventSourceUserData) == localFinishMarker {
            withBridgeKey { super.keyDown(with: event) }
            return
        }
        if submitOnReturn && event.keyCode == 36 && !hasMarkedText() && !event.modifierFlags.contains(.shift) { finish?(); return }
        if event.keyCode == 53 { cancel?(); return }
        super.keyDown(with: event)
    }
    override func setMarkedText(_ string: Any, selectedRange: NSRange, replacementRange: NSRange) {
        super.setMarkedText(string, selectedRange: selectedRange, replacementRange: replacementRange)
        onContentChange?()
    }
    override func unmarkText() { super.unmarkText(); onContentChange?() }
    override func didChangeText() { super.didChangeText(); onContentChange?() }
    override func doCommand(by selector: Selector) {
        // Offer the event to the IME, but do not insert our synthetic Return if
        // recognition already stopped. Payload whitespace must remain untouched.
        if handlingBridgeKey && (selector == #selector(insertNewline(_:)) || selector == #selector(cancelOperation(_:))) { return }
        super.doCommand(by: selector)
    }
}

// The visible NSTextView remains the IME client. Presentation never replaces
// or reparents it: the compact pill and the review card only show, hide and
// resize the views around it.
final class Composer: NSObject, NSTextViewDelegate {
    static let width: CGFloat = 600
    let panel = CapturePanel(contentRect: NSRect(x: 0, y: 0, width: Composer.width, height: 56), styleMask: [.borderless], backing: .buffered, defer: false)
    let text = CaptureText(frame: NSRect(x: 0, y: 0, width: 300, height: 24))
    let status = NSTextField(wrappingLabelWithString: "")
    let target = NSTextField(labelWithString: "UU 远程")
    // The pill's key chip ends listening; the card has no mouse send action.
    let endChip = ActionButton("右 Ctrl 结束", action: {})
    private let surface = SurfaceView()
    private let header = NSStackView()
    private let cardTitle = NSTextField(labelWithString: "核对后投送")
    private let middle = NSStackView()
    private let icon = NSImageView()
    private let textSurface = SurfaceView()
    private let scroll = NSScrollView()
    private let placeholder = NSTextField(labelWithString: "")
    private let cancelButton = NSButton()
    private let collapse = NSButton()
    private let noteSurface = SurfaceView()
    private let footer = NSStackView()
    private let hint = NSTextField(labelWithString: "Return 换行 · Esc 取消")
    private let copy = ActionButton("复制原文", action: {})
    private let reviewShortcut = NativeUI.label("右 Ctrl 投送", size: 12, weight: .semibold)
    private let reviewKey = SurfaceView()
    private let stack = NSStackView()
    private var textHeight: NSLayoutConstraint!
    private(set) var expanded = false
    private(set) var phase: CapturePhase = .starting
    private var reviewKeyTitle: String?
    // Long dictation grows the pill upward instead of hiding all but a few words.
    static let lineHeight: CGFloat = 23
    static let maxCompactLines = 8
    private var compactLines = 1
    private var compactTextHeight = Composer.lineHeight
    var isCard: Bool { phase == .review }
    var onPrimary: (() -> Void)?
    var onCancel: (() -> Void)?
    var onChanged: (() -> Void)?

    override init() {
        super.init()
        panel.title = "\(ProductInfo.name) · 浮条"; panel.setAccessibilityLabel("远程语音输入浮条")
        panel.level = .floating; panel.hidesOnDeactivate = false; panel.isReleasedWhenClosed = false
        panel.isMovableByWindowBackground = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.backgroundColor = .clear; panel.isOpaque = false; panel.hasShadow = true
        surface.stroke = Design.strongLine
        panel.contentView = surface

        cardTitle.font = Design.font(ofSize: 13, weight: .semibold); cardTitle.textColor = Design.ink
        target.font = Design.font(ofSize: 11); target.textColor = Design.secondary
        target.lineBreakMode = .byTruncatingMiddle
        target.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        target.setContentHuggingPriority(.defaultLow, for: .horizontal)
        collapse.bezelStyle = .inline; collapse.isBordered = false
        collapse.target = self; collapse.action = #selector(toggleExpanded(_:))
        collapse.setAccessibilityLabel("展开或收起编辑区")
        header.spacing = 8; header.alignment = .centerY; header.distribution = .fill
        header.addArrangedSubview(cardTitle); header.addArrangedSubview(target); header.addArrangedSubview(collapse)

        icon.widthAnchor.constraint(equalToConstant: 22).isActive = true
        icon.symbolConfiguration = .init(pointSize: 16, weight: .medium)
        text.isRichText = false; text.font = Design.font(ofSize: 14); text.drawsBackground = false; text.textColor = Design.ink
        text.textContainer?.lineFragmentPadding = 0
        let paragraph = NSMutableParagraphStyle(); paragraph.minimumLineHeight = 23; paragraph.maximumLineHeight = 23
        text.defaultParagraphStyle = paragraph
        text.isVerticallyResizable = true; text.isHorizontallyResizable = false
        text.autoresizingMask = [.width]; text.textContainer?.widthTracksTextView = true
        text.textContainerInset = NSSize(width: 0, height: 0)
        text.isAutomaticQuoteSubstitutionEnabled = false; text.isAutomaticDashSubstitutionEnabled = false
        text.isAutomaticSpellingCorrectionEnabled = false; text.delegate = self
        text.finish = { [weak self] in self?.performPrimary() }
        text.onContentChange = { [weak self] in self?.contentChanged() }
        text.cancel = { [weak self] in self?.onCancel?() }
        text.setAccessibilityLabel("本地听写文字")
        scroll.documentView = text; scroll.drawsBackground = false; scroll.borderType = .noBorder
        scroll.hasVerticalScroller = true; scroll.autohidesScrollers = true; scroll.hasHorizontalScroller = false
        scroll.translatesAutoresizingMaskIntoConstraints = false
        textHeight = scroll.heightAnchor.constraint(equalToConstant: 23); textHeight.isActive = true
        textSurface.radius = 8
        NativeUI.place(scroll, in: textSurface, inset: 0, bottom: true)
        placeholder.font = Design.font(ofSize: 14); placeholder.textColor = Design.secondary
        placeholder.lineBreakMode = .byTruncatingTail
        placeholder.translatesAutoresizingMaskIntoConstraints = false; textSurface.addSubview(placeholder)
        NSLayoutConstraint.activate([
            placeholder.leadingAnchor.constraint(equalTo: textSurface.leadingAnchor),
            placeholder.trailingAnchor.constraint(lessThanOrEqualTo: textSurface.trailingAnchor),
            placeholder.centerYAnchor.constraint(equalTo: textSurface.centerYAnchor)
        ])
        textSurface.setContentHuggingPriority(.defaultLow, for: .horizontal)
        textSurface.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        endChip.style = .tinted; endChip.radius = 17; endChip.buttonHeight = 34
        endChip.font = Design.font(ofSize: 12, weight: .semibold)
        endChip.target = self; endChip.action = #selector(primaryAction(_:))
        cancelButton.image = NSImage(systemSymbolName: "xmark", accessibilityDescription: "取消本次投送")
        cancelButton.bezelStyle = .inline; cancelButton.isBordered = false; cancelButton.toolTip = "取消本次投送 · Esc"
        cancelButton.contentTintColor = Design.secondary
        cancelButton.target = self; cancelButton.action = #selector(cancelAction(_:))
        cancelButton.widthAnchor.constraint(equalToConstant: 22).isActive = true
        middle.spacing = 11; middle.alignment = .centerY; middle.distribution = .fill
        for view in [icon, textSurface, endChip, cancelButton] as [NSView] { middle.addArrangedSubview(view) }

        status.font = Design.font(ofSize: 11); status.textColor = Design.amber
        status.maximumNumberOfLines = 4; status.lineBreakMode = .byTruncatingTail
        status.preferredMaxLayoutWidth = Composer.width - 32 - 20
        noteSurface.radius = 7; noteSurface.fill = Design.amberSoft
        NativeUI.place(status, in: noteSurface, inset: 9, bottom: true)

        hint.font = Design.font(ofSize: 11); hint.textColor = Design.secondary
        hint.setContentHuggingPriority(.defaultLow, for: .horizontal)
        hint.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        copy.style = .subtle; copy.buttonHeight = 30; copy.font = Design.font(ofSize: 11)
        copy.target = self; copy.action = #selector(copyAction(_:)); copy.toolTip = "复制原文，不自动投送"
        reviewKey.fill = Design.accent; reviewKey.radius = 15
        reviewShortcut.textColor = Design.accentInk; reviewShortcut.alignment = .center
        reviewShortcut.setContentCompressionResistancePriority(.required, for: .horizontal)
        reviewShortcut.translatesAutoresizingMaskIntoConstraints = false; reviewKey.addSubview(reviewShortcut)
        NSLayoutConstraint.activate([
            reviewKey.heightAnchor.constraint(equalToConstant: 30),
            reviewShortcut.leadingAnchor.constraint(equalTo: reviewKey.leadingAnchor, constant: 13),
            reviewShortcut.trailingAnchor.constraint(equalTo: reviewKey.trailingAnchor, constant: -13),
            reviewShortcut.centerYAnchor.constraint(equalTo: reviewKey.centerYAnchor)
        ])
        footer.spacing = 8; footer.alignment = .centerY; footer.distribution = .fill
        for view in [hint, copy, reviewKey] as [NSView] { footer.addArrangedSubview(view) }

        stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 10
        stack.translatesAutoresizingMaskIntoConstraints = false
        for view in [header, middle, noteSurface, footer] as [NSView] { stack.addArrangedSubview(view) }
        surface.addSubview(stack)
        stackInsets = [
            stack.leadingAnchor.constraint(equalTo: surface.leadingAnchor, constant: 18),
            stack.trailingAnchor.constraint(equalTo: surface.trailingAnchor, constant: -10),
            stack.topAnchor.constraint(equalTo: surface.topAnchor, constant: 11)
        ]
        NSLayoutConstraint.activate(stackInsets)
        for view in stack.arrangedSubviews { view.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true }
        setState(.starting, message: "正在唤起豆包…可直接开口")
    }
    private var stackInsets: [NSLayoutConstraint] = []
    var ownsInputFocus: Bool {
        panel.isKeyWindow && panel.firstResponder === text && NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid()
    }
    func focus() {
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil); panel.makeFirstResponder(text)
    }
    func configure(text value: String = "", destination: String, source: String, review: Bool = false) {
        text.string = value; text.isEditable = true
        text.setSelectedRange(NSRange(location: (value as NSString).length, length: 0))
        target.stringValue = "→ " + destination; target.toolTip = destination
        panel.setAccessibilityValue(source)
        expanded = review
        setState(review ? .review : .starting, message: review ? "核对正文与目标后，轻按右 Ctrl 投送。" : "正在唤起豆包…可直接开口")
    }
    func show(text value: String = "", destination: String, source: String, review: Bool = false) {
        configure(text: value, destination: destination, source: source, review: review)
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        if let frame = screen?.visibleFrame { panel.setFrameOrigin(NSPoint(x: frame.midX - Composer.width / 2, y: frame.minY + 72)) }
        focus()
    }
    // reviewKey overrides the review chip, e.g. when right Ctrl retries a failed start.
    func setState(_ phase: CapturePhase, message: String, reviewKey: String? = nil) {
        self.phase = phase; status.stringValue = message; status.toolTip = message
        reviewKeyTitle = phase == .review ? reviewKey : nil
        placeholder.stringValue = message
        switch phase {
        case .starting:
            icon.image = NSImage(systemSymbolName: "waveform", accessibilityDescription: nil); icon.contentTintColor = Design.secondary
            endChip.title = "右 Ctrl 结束"; endChip.isEnabled = true
        case .listening:
            icon.image = NSImage(systemSymbolName: "waveform", accessibilityDescription: nil); icon.contentTintColor = Design.accent
            endChip.title = "右 Ctrl 结束"; endChip.isEnabled = true
            placeholder.stringValue = "请说话…"
        case .settling:
            icon.image = NSImage(systemSymbolName: "ellipsis.circle", accessibilityDescription: nil); icon.contentTintColor = Design.accent
            endChip.title = "整理中…"; endChip.isEnabled = false
        case .review:
            expanded = true
            cardTitle.stringValue = "核对后投送"
        }
        endChip.setAccessibilityLabel(endChip.title)
        updateLayout()
    }
    func hide() { text.isEditable = false; panel.orderOut(nil); text.string = ""; compactLines = 1; compactTextHeight = Composer.lineHeight }
    private var isBlank: Bool { text.string.isEmpty && !text.hasMarkedText() }
    // Lines the current text needs at the pill's width, including marked text.
    // Autoresizing is not trusted with the text width: pin the view and its
    // container to the visible area so wrapped lines can never run past the
    // edges or be scrolled sideways out of view.
    private func pinTextWidth() {
        let width = scroll.contentSize.width
        guard width > 0 else { return }
        if abs(text.frame.width - width) > 0.5 { text.setFrameSize(NSSize(width: width, height: text.frame.height)) }
        text.textContainer?.size = NSSize(width: max(1, width - text.textContainerInset.width * 2), height: .greatestFiniteMagnitude)
        if scroll.contentView.bounds.minX != 0 {
            scroll.contentView.scroll(to: NSPoint(x: 0, y: scroll.contentView.bounds.minY)); scroll.reflectScrolledClipView(scroll.contentView)
        }
    }
    private func usedTextHeight() -> CGFloat {
        pinTextWidth()
        guard let manager = text.layoutManager, let container = text.textContainer else { return Composer.lineHeight }
        manager.ensureLayout(for: container)
        return ceil(manager.usedRect(for: container).height)
    }
    private func measuredLines() -> Int {
        min(Composer.maxCompactLines, max(1, Int(ceil((usedTextHeight() - 1) / Composer.lineHeight))))
    }
    // Called for every content change and by the session poll as a safety net.
    func refresh() { contentChanged(notify: false) }
    private func contentChanged(notify: Bool = true) {
        placeholder.isHidden = isCard || !isBlank
        if !isCard {
            let height = isBlank ? Composer.lineHeight : min(CGFloat(Composer.maxCompactLines) * Composer.lineHeight, max(Composer.lineHeight, usedTextHeight()))
            if abs(height - compactTextHeight) > 0.5 {
                compactTextHeight = height; compactLines = Int(ceil((height - 1) / Composer.lineHeight)); updateLayout()
            }
            text.scrollToEndOfDocument(nil); pinTextWidth()
        }
        if notify { onChanged?() }
    }
    func setExpanded(_ value: Bool) { expanded = value; updateLayout(); text.scrollRangeToVisible(text.selectedRange()) }
    private func updateLayout() {
        let card = isCard
        header.isHidden = !card; footer.isHidden = !card
        icon.isHidden = card; endChip.isHidden = card; cancelButton.isHidden = card
        noteSurface.isHidden = !card || status.stringValue.isEmpty
        textSurface.fill = card ? Design.soft : .clear; textSurface.needsDisplay = true
        placeholder.isHidden = card || !isBlank
        text.submitOnReturn = !card
        reviewShortcut.stringValue = reviewKeyTitle ?? "右 Ctrl 投送"
        hint.stringValue = "Return 换行 · Esc 取消"
        collapse.image = NSImage(systemSymbolName: expanded ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right", accessibilityDescription: expanded ? "收起" : "展开")
        middle.alignment = !card && compactLines > 1 ? .bottom : .centerY
        text.textContainerInset = card ? NSSize(width: 10, height: 8) : NSSize(width: 2, height: 0)
        stackInsets[0].constant = card ? 16 : 18; stackInsets[1].constant = card ? -16 : -10; stackInsets[2].constant = card ? 14 : 11
        surface.radius = card ? 16 : compactLines > 1 ? 20 : 28; surface.needsDisplay = true
        let old = panel.frame
        panel.setFrame(NSRect(x: old.minX, y: old.minY, width: Composer.width, height: old.height), display: false)
        surface.layoutSubtreeIfNeeded()
        // Measured at this mode's own width and inset. The review editor also
        // grows with long text, up to a readable height.
        let reviewLines = card ? measuredLines() : 1
        textHeight.constant = card ? (expanded ? max(140, min(320, CGFloat(reviewLines) * Composer.lineHeight + 16)) : 46) : compactTextHeight
        surface.layoutSubtreeIfNeeded()
        // The stack is pinned only at the top, so its laid-out height is its content height.
        let height = card ? ceil(stack.frame.height) + 30 : max(56, compactTextHeight + 22)
        panel.setFrame(NSRect(x: old.minX, y: old.minY, width: Composer.width, height: height), display: true)
        surface.layoutSubtreeIfNeeded(); pinTextWidth(); panel.invalidateShadow()
        if !card { text.scrollToEndOfDocument(nil); pinTextWidth() }
    }
    // Review submission belongs to the global right-Control path. Mouse actions and
    // Return must not steal the remote client's focus immediately before paste.
    private func performPrimary() {
        guard !isCard else { return }
        onPrimary?()
    }
    @objc private func toggleExpanded(_ sender: Any?) { setExpanded(!expanded); focus() }
    @objc private func primaryAction(_ sender: Any?) { performPrimary() }
    @objc private func cancelAction(_ sender: Any?) { onCancel?() }
    @objc private func copyAction(_ sender: Any?) {
        let board = NSPasteboard.general; board.clearContents()
        let message = board.setString(text.string, forType: .string) ? "已复制原文，可手动粘贴到所需位置。" : "复制失败，文字仍保留在浮条。"
        setState(phase, message: message, reviewKey: reviewKeyTitle)
    }
}
