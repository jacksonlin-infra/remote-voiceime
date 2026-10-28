import AppKit

final class PreviewDestination: RemoteDestination {
    let label = "Windows App · 工作电脑"
    let profile = RemoteClientProfile.windowsApp
    func isValid() -> Bool { true }
    func isFrontmost() -> Bool { false }
    func activate() -> Bool { false }
}

@main enum Preview {
    static func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(descendants) }
    static func visible(_ view: NSView, in root: NSView) -> Bool {
        var current: NSView? = view
        while let node = current, node !== root { if node.isHidden { return false }; current = node.superview }
        return true
    }
    static func button(_ title: String, in view: NSView) -> NSButton {
        guard let button = descendants(view).compactMap({ $0 as? NSButton }).first(where: { $0.title == title && visible($0, in: view) }) else { fatalError("Missing button: \(title)") }
        return button
    }
    static func hasButton(_ title: String, in view: NSView) -> Bool {
        descendants(view).compactMap { $0 as? NSButton }.contains { $0.title == title && visible($0, in: view) }
    }
    static func hasText(_ fragment: String, in view: NSView) -> Bool {
        descendants(view).compactMap { $0 as? NSTextField }.contains { $0.stringValue.contains(fragment) && visible($0, in: view) }
    }
    static func render(_ view: NSView, named name: String, in directory: URL) throws {
        view.layoutSubtreeIfNeeded()
        precondition(!view.hasAmbiguousLayout, "Layout must be unambiguous: \(name)")
        var visibleControls: [(NSView, NSRect)] = []
        for control in descendants(view) where control is NSButton || control is NSTextField {
            var ancestor: NSView? = control
            var ignored = false
            while let current = ancestor, current !== view {
                if current.isHidden || current is NSClipView { ignored = true; break }
                ancestor = current.superview
            }
            if !ignored {
                let rect = control.convert(control.bounds, to: view)
                visibleControls.append((control, rect))
                precondition(view.bounds.insetBy(dx: -1, dy: -1).contains(rect), "Visible controls must stay in bounds: \(name), \(control), \(rect), root \(view.bounds)")
            }
        }
        for (index, pair) in visibleControls.enumerated() {
            for other in visibleControls.dropFirst(index + 1) where (pair.0 is NSButton) != (other.0 is NSButton) {
                let overlap = pair.1.intersection(other.1)
                precondition(overlap.width <= 1 || overlap.height <= 1, "Text must not overlap an action: \(name), \(pair.0), \(other.0)")
            }
        }
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(view.bounds.width * 2), pixelsHigh: Int(view.bounds.height * 2), bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        bitmap.size = view.bounds.size
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let png = bitmap.representation(using: .png, properties: [:]) else { fatalError("PNG rendering failed") }
        try png.write(to: directory.appendingPathComponent(name + ".png"))
        print("Rendered \(name): \(Int(view.bounds.width)) × \(Int(view.bounds.height)) pt")
    }
    static func main() throws {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(CommandLine.arguments.contains("--show-setup") ? .regular : .prohibited)
        let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let iconURL = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("resources/icons/AppIcon.icns")
        guard let appIcon = NSImage(contentsOf: iconURL) else { fatalError("Missing application icon") }
        precondition(appIcon.setName("AppIcon"), "Preview must use the bundled application icon")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        // Floating bar: the IME receiver survives every presentation change.
        let composer = Composer()
        let editor = composer.text
        composer.text.string = "保留原文  "
        composer.text.setSelectedRange(NSRange(location: (composer.text.string as NSString).length, length: 0))
        composer.text.withBridgeKey { composer.text.doCommand(by: #selector(NSTextView.insertNewline(_:))) }
        precondition(composer.text.string == "保留原文  ", "Bridge Return must not append an accidental newline")
        composer.text.doCommand(by: #selector(NSTextView.insertNewline(_:)))
        precondition(composer.text.string == "保留原文  \n", "Ordinary multiline editing must remain available")
        let payload = "  中文 e\u{301} 👩🏽‍💻\n第二行  "
        composer.text.string = payload
        composer.text.setSelectedRange(NSRange(location: 2, length: 3))
        composer.setState(.review, message: "核对"); composer.setExpanded(false); composer.setState(.listening, message: "")
        precondition(composer.text === editor && composer.text.string == payload, "Pill and card switches must preserve the real receiver and exact payload")
        precondition(composer.text.selectedRange() == NSRange(location: 2, length: 3), "Pill and card switches must preserve the editing selection")
        composer.text.setSelectedRange(NSRange(location: 0, length: 0))
        composer.text.setMarkedText("候选文字", selectedRange: NSRange(location: 4, length: 0), replacementRange: NSRange(location: NSNotFound, length: 0))
        let markedBody = composer.text.string
        composer.setState(.review, message: "核对"); composer.setState(.settling, message: "")
        precondition(composer.text.hasMarkedText() && composer.text.string == markedBody, "Presentation changes must preserve IME marked text")
        composer.text.unmarkText(); composer.text.string = payload

        var primaryCalls = 0
        composer.onPrimary = { primaryCalls += 1 }
        composer.setState(.listening, message: "请说话…")
        composer.endChip.performClick(nil)
        precondition(primaryCalls == 1 && composer.text.submitOnReturn, "Listening pill ends dictation by chip or Return")
        composer.text.finish?()
        precondition(primaryCalls == 2, "Return finishes listening")
        composer.setState(.settling, message: "等待豆包定稿…")
        composer.endChip.performClick(nil)
        precondition(primaryCalls == 2 && !composer.endChip.isEnabled, "Settling cannot be ended twice")
        composer.setState(.review, message: "上次已发出粘贴。先看一眼远端，确实没收到再按右 Ctrl 投送。")
        let card = composer.panel.contentView!
        composer.text.finish?()
        precondition(primaryCalls == 2 && composer.endChip.isHidden && !composer.text.submitOnReturn, "Review has no mouse or Return send action")
        precondition(hasText("右 Ctrl 投送", in: card) && !descendants(card).contains { $0 is DesignCheck && visible($0, in: card) }, "Review sends with right Ctrl and no acknowledgement checkbox")
        composer.text.string = "固定测试"; composer.text.setSelectedRange(NSRange(location: 4, length: 0))
        composer.text.doCommand(by: #selector(NSTextView.insertNewline(_:)))
        precondition(composer.text.string == "固定测试\n" && primaryCalls == 2, "Review newline preserves editing without dispatching delivery")
        composer.setState(.review, message: "无法启动豆包语音。轻按右 Ctrl 重试，Esc 取消。", reviewKey: "右 Ctrl 重试")
        precondition(hasText("右 Ctrl 重试", in: card), "A failed start tells the user that right Ctrl retries")

        // Doubao streams marked text without textDidChange: the hint must still yield.
        composer.hide()
        composer.configure(destination: "UU 远程 · 工作电脑", source: "豆包")
        composer.setState(.listening, message: "请说话…")
        let pill = composer.panel.contentView!
        precondition(hasText("请说话", in: pill), "An empty pill invites speech")
        composer.text.setMarkedText("把客户反馈", selectedRange: NSRange(location: 5, length: 0), replacementRange: NSRange(location: NSNotFound, length: 0))
        precondition(!hasText("请说话", in: pill), "Streaming marked text hides the placeholder")
        composer.text.unmarkText(); composer.text.string = ""; composer.refresh()
        let long = String(repeating: "这是一段比较长的口述内容，用来检查浮条会随内容增高。", count: 6)
        composer.text.insertText(long, replacementRange: NSRange(location: 0, length: 0))
        precondition(composer.panel.frame.height > 56 && composer.panel.frame.height <= 22 + CGFloat(Composer.maxCompactLines) * Composer.lineHeight, "Long dictation grows the pill to a bounded height")
        func textFits(_ name: String) {
            let visible = composer.text.enclosingScrollView!.contentView.bounds
            precondition(composer.text.frame.width <= visible.width + 0.5 && visible.minX == 0, "Text must wrap inside the visible area: \(name) text \(composer.text.frame.width) visible \(visible)")
        }
        textFits("long pill")
        try render(pill, named: "floatbar-long", in: directory)
        composer.hide(); composer.configure(destination: "UU 远程 · 工作电脑", source: "豆包"); composer.setState(.listening, message: "请说话…")
        precondition(composer.panel.frame.height == 56, "A new round starts as a one-line pill")

        let floatbars: [(String, NSAppearance.Name, CapturePhase, String, String)] = [
            ("floatbar-starting", .aqua, .starting, "", "正在唤起豆包…可直接开口"),
            ("floatbar-light", .aqua, .listening, "把客户反馈的三个问题整理进周报", "请说话…"),
            ("floatbar-dark", .darkAqua, .settling, "把客户反馈的三个问题整理进周报", "等待豆包定稿…"),
            ("floatbar-review", .aqua, .review, "1. 接口联调已完成\n2. 下周开始灰度", "文字含换行或控制字符，请核对。轻按右 Ctrl 会原样投送。")
        ]
        for (name, appearance, phase, text, message) in floatbars {
            composer.hide()
            composer.configure(text: text, destination: "UU 远程 · 工作电脑", source: "豆包", review: phase == .review)
            composer.panel.appearance = NSAppearance(named: appearance)
            composer.setState(phase, message: message)
            let view = composer.panel.contentView!
            view.layoutSubtreeIfNeeded()
            precondition(phase == .review || view.bounds.height == 56, "Short dictation stays a one-line pill")
            textFits(name)
            try render(view, named: name, in: directory)
        }
        composer.hide()
        composer.configure(text: "今天的会议纪要已经整理好了，请在远程电脑中补充下一步安排。", destination: "Windows App · 工作电脑", source: "恢复文字", review: true)
        composer.setState(.review, message: "上次已发出粘贴。先看一眼远端，确实没收到再按右 Ctrl 投送。")
        try render(composer.panel.contentView!, named: "floatbar-recovery", in: directory)
        precondition(composer.text === editor, "The capture text view is never replaced")

        // HUD: passive feedback for every press.
        let hud = StatusHUD()
        var repasted = 0
        hud.configure("已投送 23 字", tone: .success, actions: [.init(title: "再贴一次") { repasted += 1 }])
        try render(hud.panel.contentView!, named: "hud-delivered", in: directory)
        button("再贴一次", in: hud.panel.contentView!).performClick(nil)
        precondition(repasted == 1, "The delivered HUD offers a one-step re-paste")
        hud.configure("没有识别到文字，再按右 Ctrl 重试", tone: .warning)
        try render(hud.panel.contentView!, named: "hud-no-speech", in: directory)
        hud.configure("切到远端输入框，轻按右 Ctrl", tone: .test, actions: [.init(title: "取消") {}])
        try render(hud.panel.contentView!, named: "hud-test", in: directory)
        hud.configure("远端出现测试文字了吗？", detail: "“Remote VoiceIME 测试 143022”", tone: .test, actions: [.init(title: "出现了", primary: true) {}, .init(title: "没有 / 不完整") {}])
        try render(hud.panel.contentView!, named: "hud-test-question", in: directory)
        hud.configure("没出现？按看到的现象调整后重试", detail: "调整后会立即用同一个远端窗口再测一次。", tone: .warning, actions: [.init(title: "关闭") {}], options: [
            .init(title: "贴出了别的旧内容", detail: "剪贴板同步慢于粘贴", action: .init(title: "改用兼容速度并重试") {}),
            .init(title: "出现一个 v，或什么都没有", detail: "远端可能需要 Control + V", action: .init(title: "改用 Control + V 并重试") {}),
            .init(title: "手动复制粘贴也不行", detail: "UU 远程 的剪贴板同步没有打开", action: .init(title: "查看开启方法") {})
        ])
        try render(hud.panel.contentView!, named: "hud-test-fix", in: directory)

        // Setup checklist: derived from live state, nothing to resume.
        let setup = SetupController()
        var relaunched = 0, started = 0
        setup.onRelaunch = { relaunched += 1 }; setup.onStart = { started += 1 }
        setup.update(SetupStatus(doubao: true, accessibility: true, inputMonitoring: true, listening: false, running: [.uu], clipboardReady: false))
        let setupView = setup.window.contentView!
        precondition(!button("开始使用", in: setupView).isEnabled, "Setup cannot start before required items are ready")
        button("重新打开应用", in: setupView).performClick(nil)
        precondition(relaunched == 1, "Granted Input Monitoring offers an in-app relaunch")
        try render(setupView, named: "setup-checklist", in: directory)
        setup.update(SetupStatus(doubao: true, accessibility: true, inputMonitoring: true, listening: true, running: [.uu], clipboardReady: true, calibration: nil))
        precondition(button("开始使用", in: setupView).isEnabled && button("试一次", in: setupView).isEnabled, "Ready setup starts without forcing the optional test")
        try render(setupView, named: "setup-ready", in: directory)
        button("开始使用", in: setupView).performClick(nil)
        precondition(started == 1, "Start completes setup")
        setup.update(SetupStatus())
        precondition(hasButton("打开键盘设置", in: setupView) && hasButton("授权", in: setupView), "Missing items offer their own fix")
        setup.window.appearance = NSAppearance(named: .darkAqua)
        try render(setupView, named: "setup-dark", in: directory)

        // Settings: three pages.
        let settings = SettingsController()
        var state = SettingsState(profile: .uu, options: RemoteDeliveryOptions(control: false, speed: .standard), paused: false, status: "已就绪：在 UU / Windows App 中轻按右 Ctrl 开始听写", accessibility: true, inputMonitoring: true, listening: true, restoresClipboard: false, longPressRepaste: true, calibration: .passed)
        settings.update(state)
        settings.window.appearance = NSAppearance(named: .aqua)
        for (index, name) in ["settings-general", "settings-remote", "settings-permissions"].enumerated() {
            settings.selectSection(index)
            try render(settings.window.contentView!, named: name, in: directory)
        }
        settings.selectSection(1)
        precondition(hasText("试一次 · 已通过", in: settings.window.contentView!), "Remote page shows the stored test result")
        state.options.speed = .compatible; state.calibration = .untested; settings.update(state)
        precondition(hasText("试一次 · 未测试", in: settings.window.contentView!), "Changed options show the client as untested")
        state.listening = false; settings.update(state); settings.selectSection(2)
        precondition(hasButton("重新打开应用", in: settings.window.contentView!), "Permissions page offers the relaunch when the tap is not running")
        settings.selectSection(0); settings.window.appearance = NSAppearance(named: .darkAqua)
        try render(settings.window.contentView!, named: "settings-dark", in: directory)

        let recovery = RecoveryController()
        let target = PreviewDestination()
        let records = [RecoveryRecord(id: UUID(), text: "请把今天讨论的三项任务整理到项目记录中，稍后我们逐项确认。", destination: target, createdAt: 10, status: .failed("目标窗口已变化，本次未投送")), RecoveryRecord(id: UUID(), text: "今天的会议纪要已经整理好了，请在远程电脑中补充下一步安排。", destination: target, createdAt: 5, status: .unconfirmed)]
        recovery.canRestore = { _ in true }; recovery.reload(records, now: 11)
        try render(recovery.window.contentView!, named: "recent-text", in: directory)
        let recoveryText = descendants(recovery.window.contentView!).compactMap { $0 as? NSTextView }.first!
        recoveryText.setSelectedRange(NSRange(location: 2, length: 3))
        recovery.reload(records, now: 12)
        precondition(recoveryText.selectedRange() == NSRange(location: 2, length: 3), "Age refresh must preserve the user's text selection")
        recovery.reload([], now: 400)
        precondition(recoveryText.string.isEmpty, "Hidden recovery windows must also discard expired text")
        precondition(!button("恢复草稿", in: recovery.window.contentView!).isEnabled, "Expired recovery text cannot be restored")
        try render(recovery.window.contentView!, named: "recent-empty", in: directory)
        print("Passed AppKit checks: bridge Return, receiver/selection/marked text preservation, review without checkbox, marked-text placeholder, long-text pill, HUD re-paste, setup relaunch and start gates, settings pages and native layouts.")
        if CommandLine.arguments.contains("--show-setup") { setup.present(); NSApp.run() }
        print("Preview only: no window shown, no microphone, event tap, AX control or general clipboard access.")
    }
}
