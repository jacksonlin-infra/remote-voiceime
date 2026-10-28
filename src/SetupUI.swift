import AppKit

// Everything the checklist shows is derived from live checks plus the one
// answer the app cannot detect: whether the client's clipboard sync is on.
struct SetupStatus: Equatable {
    var doubao = false
    var accessibility = false
    var inputMonitoring = false
    var listening = false
    var running: [RemoteClientProfile] = []
    var clipboardReady = false
    var calibration: String?
    var keysReady: Bool { inputMonitoring && listening }
    var requiredDone: Int { [doubao, accessibility, keysReady, clipboardReady].filter { $0 }.count }
    var ready: Bool { requiredDone == 4 }
}

// One page instead of a linear wizard: nothing to resume, because every row
// is recomputed after a relaunch and finished rows are never asked again.
final class SetupController: NSObject, NSWindowDelegate {
    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 720, height: 600), styleMask: [.titled, .closable], backing: .buffered, defer: false)
    var onPermission: ((Bool) -> Void)?
    var onRelaunch: (() -> Void)?
    var onOpenKeyboard: (() -> Void)?
    var onClipboard: ((Bool) -> Void)?
    var onTest: (() -> Void)?
    var onStart: (() -> Void)?
    var onClose: (() -> Void)?
    private(set) var status = SetupStatus()
    private let body = NSView()
    private var starting = false

    override init() {
        super.init()
        window.title = "准备 \(ProductInfo.name)"; window.isReleasedWhenClosed = false; window.delegate = self
        let root = NativeUI.windowBody(window, title: "准备 \(ProductInfo.name)", width: 720, height: 560)
        NativeUI.place(body, in: root, inset: 0, bottom: true)
        render()
    }
    func present() { NativeUI.present(window) }
    func update(_ value: SetupStatus) {
        guard value != status else { return }
        status = value; render()
    }
    func windowWillClose(_ notification: Notification) { if !starting { onClose?() } }

    private func render() {
        body.subviews.forEach { $0.removeFromSuperview() }
        let missing = 4 - status.requiredDone
        let title = NativeUI.label(status.ready ? "准备就绪" : missing == 1 ? "还差一步就能用" : "还差 \(missing) 步就能用", size: 22, weight: .semibold)
        let subtitle = NativeUI.label(status.ready ? "回到远端输入框，轻按右 Ctrl 开始，再按一次结束。" : "能检测的项目会自动打勾。随时可以关掉，重开应用后从这里继续。", size: 12, secondary: true)
        let current = [status.doubao, status.accessibility, status.keysReady, status.clipboardReady].firstIndex(of: false)
        var rows: [NSView] = []
        rows.append(row(0, current: current, done: status.doubao, title: "豆包输入法",
                        detail: status.doubao ? "已检测到并启用" : "未检测到已启用的豆包输入法。安装后在“键盘 › 输入法”中添加。",
                        control: status.doubao ? note("自动检测") : ActionButton("打开键盘设置") { [weak self] in self?.onOpenKeyboard?() }))
        rows.append(row(1, current: current, done: status.accessibility, title: "辅助功能", detail: "调用豆包语音，并向远控窗口发出粘贴",
                        control: status.accessibility ? note("已授权") : ActionButton("授权", primary: current == 1) { [weak self] in self?.onPermission?(true) }))
        let keysControl: NSView
        let keysDetail: String
        if status.keysReady { keysControl = note("已生效"); keysDetail = "在远程窗口中识别右 Ctrl" }
        else if status.inputMonitoring {
            keysDetail = "开关已打开；macOS 要求重新打开应用后才生效"
            keysControl = ActionButton("重新打开应用", primary: true) { [weak self] in self?.onRelaunch?() }
        } else {
            keysDetail = "在远程窗口中识别右 Ctrl；授权后需重新打开应用"
            keysControl = ActionButton("授权", primary: current == 2) { [weak self] in self?.onPermission?(false) }
        }
        rows.append(row(2, current: current, done: status.keysReady, title: "输入监控", detail: keysDetail, control: keysControl))
        let clients = status.running.map(\.name).joined(separator: "、")
        let check = DesignCheck("我已在远控软件中开启“本地 → 远端”剪贴板同步", target: self, action: #selector(clipboardChanged(_:)))
        check.state = status.clipboardReady ? .on : .off; check.font = Design.font(ofSize: 11)
        rows.append(row(3, current: current, done: status.clipboardReady, title: "远程客户端",
                        detail: clients.isEmpty ? "尚未检测到 UU 远程或 Windows App 在运行，可稍后再打开" : "检测到 \(clients) 正在运行",
                        control: nil, extra: check))
        let test = ActionButton("试一次") { [weak self] in self?.onTest?() }
        test.isEnabled = status.accessibility && status.keysReady
        rows.append(row(4, current: nil, done: false, optional: true, title: "试一次", detail: status.calibration ?? "在真实的远端输入框里投送一句测试文字", control: test))

        let later = ActionButton("稍后") { [weak self] in self?.window.close() }
        later.style = .subtle
        let start = ActionButton("开始使用", primary: true) { [weak self] in
            guard let self, self.status.ready else { return }
            self.starting = true; self.window.close(); self.starting = false; self.onStart?()
        }
        start.isEnabled = status.ready
        let progress = NativeUI.label(status.ready ? "必需项已全部就绪 · 之后启动不再弹出" : "必需项 \(status.requiredDone) / 4 · 完成后驻留在菜单栏", size: 11, secondary: true)
        progress.setContentHuggingPriority(.defaultLow, for: .horizontal)
        progress.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let footer = NSStackView(views: [progress, later, start]); footer.spacing = 10; footer.alignment = .centerY; footer.distribution = .fill
        let column = NativeUI.column([title, subtitle, NativeUI.group(rows), NativeUI.separator(), footer], spacing: 8)
        column.setCustomSpacing(22, after: subtitle); column.setCustomSpacing(26, after: column.arrangedSubviews[2])
        column.setCustomSpacing(16, after: column.arrangedSubviews[3])
        NativeUI.fillWidth(column)
        NativeUI.place(column, in: body, inset: 32)
        body.layoutSubtreeIfNeeded()
        window.setContentSize(NSSize(width: 720, height: max(520, ceil(column.fittingSize.height) + 64 + 45)))
    }
    private func note(_ text: String) -> NSView { NativeUI.label(text, size: 11, secondary: true) }
    private func row(_ index: Int, current: Int?, done: Bool, optional: Bool = false, title: String, detail: String, control: NSView?, extra: NSView? = nil) -> NSView {
        let symbol = done ? "checkmark.circle.fill" : optional ? "circle.dashed" : index == current ? "arrow.right.circle.fill" : "circle"
        let icon = NSImageView(image: NSImage(systemSymbolName: symbol, accessibilityDescription: done ? "已完成" : "未完成")!)
        icon.symbolConfiguration = .init(pointSize: 18, weight: .regular)
        icon.contentTintColor = done ? Design.green : index == current ? Design.accent : Design.strongLine
        icon.widthAnchor.constraint(equalToConstant: 22).isActive = true
        let heading = NativeUI.label(optional ? title + " · 可选" : title, weight: .semibold)
        var texts: [NSView] = [heading, NativeUI.label(detail, size: 10, secondary: true)]
        if let extra { texts.append(extra) }
        let labels = NativeUI.column(texts, spacing: 5)
        labels.setContentHuggingPriority(.defaultLow, for: .horizontal)
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let line = NSStackView(views: [icon, labels]); line.spacing = 14; line.alignment = .centerY; line.distribution = .fill
        if let control {
            control.setContentHuggingPriority(.required, for: .horizontal)
            control.setContentCompressionResistancePriority(.required, for: .horizontal)
            line.addArrangedSubview(control)
        }
        return line
    }
    @objc private func clipboardChanged(_ sender: NSButton) { onClipboard?(sender.state == .on) }
}
