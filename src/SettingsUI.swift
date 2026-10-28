import AppKit

struct SettingsState: Equatable {
    var profile = RemoteClientProfile.uu
    var options = RemoteDeliveryOptions(control: false, speed: .standard)
    var paused = false
    var status = ""
    var accessibility = false
    var inputMonitoring = false
    var listening = false
    var restoresClipboard = false
    var longPressRepaste = true
    var calibration = CalibrationResult.untested
}

final class SettingsController: NSObject {
    static let sections = ["通用", "远程客户端", "权限与诊断"]
    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 600), styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
    var onProfile: ((RemoteClientProfile) -> Void)?
    var onOptions: ((RemoteDeliveryOptions) -> Void)?
    var onPermission: ((Bool) -> Void)?
    var onRecheck: (() -> Void)?
    var onRelaunch: (() -> Void)?
    var onTest: (() -> Void)?
    var onPause: (() -> Void)?
    var onRestoreClipboard: ((Bool) -> Void)?
    var onLongPress: ((Bool) -> Void)?
    var onOpenSetup: (() -> Void)?
    var onCopyDiagnostics: (() -> Void)?
    var onClearRecent: (() -> Void)?
    private(set) var state = SettingsState()
    private(set) var section = 1
    private let content = NSView()
    private let status = NativeUI.label("在远端放好光标，右 Ctrl 开始 / 结束。", size: 10, secondary: true)
    private var navigation: [NSButton] = []

    override init() {
        super.init()
        window.title = "\(ProductInfo.name) · 设置"; window.isReleasedWhenClosed = false
        let root = NativeUI.windowBody(window, title: "设置", width: 800, height: 560)
        let sidebar = SurfaceView(); sidebar.fill = Design.soft; sidebar.stroke = Design.line
        sidebar.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(sidebar)
        content.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(content)
        status.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(status)
        NSLayoutConstraint.activate([
            sidebar.leadingAnchor.constraint(equalTo: root.leadingAnchor), sidebar.topAnchor.constraint(equalTo: root.topAnchor),
            sidebar.bottomAnchor.constraint(equalTo: root.bottomAnchor), sidebar.widthAnchor.constraint(equalToConstant: 175),
            content.leadingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: 27), content.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -27),
            content.topAnchor.constraint(equalTo: root.topAnchor, constant: 24), content.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -22),
            status.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 14), status.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: -14),
            status.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -18), status.heightAnchor.constraint(equalToConstant: 52)
        ])
        status.maximumNumberOfLines = 3; status.lineBreakMode = .byTruncatingTail
        let brandIcon = NSImageView(image: NSImage(named: "AppIcon") ?? NSImage(systemSymbolName: "waveform", accessibilityDescription: nil)!)
        brandIcon.contentTintColor = Design.accent; brandIcon.widthAnchor.constraint(equalToConstant: 22).isActive = true
        let brand = NSStackView(views: [brandIcon, NativeUI.label(ProductInfo.name, size: 12, weight: .semibold)])
        brand.spacing = 9; brand.heightAnchor.constraint(equalToConstant: 30).isActive = true
        let nav = NativeUI.column([brand], spacing: 4)
        nav.setCustomSpacing(24, after: brand)
        let symbols = ["slider.horizontal.3", "display", "checkmark.shield"]
        for (index, title) in Self.sections.enumerated() {
            let button = ActionButton(title) { [weak self] in self?.selectSection(index) }
            button.style = .navigation; button.symbol = symbols[index]; button.font = Design.font(ofSize: 11)
            button.setButtonType(.toggle); button.buttonHeight = 36
            button.heightAnchor.constraint(equalToConstant: 36).isActive = true
            navigation.append(button); nav.addArrangedSubview(button)
        }
        NativeUI.fillWidth(nav); NativeUI.place(nav, in: sidebar, inset: 10)
        selectSection(1)
    }
    // The status line changes often; everything else re-renders only on change.
    func update(_ value: SettingsState) {
        var withoutStatus = value; withoutStatus.status = state.status
        let changed = withoutStatus != state
        state = value
        status.stringValue = value.status; status.toolTip = value.status
        if changed { render() }
    }
    func selectSection(_ index: Int) {
        section = min(max(0, index), Self.sections.count - 1)
        for (i, button) in navigation.enumerated() {
            button.state = i == section ? .on : .off
            button.needsDisplay = true
        }
        render()
    }
    func present() { NativeUI.present(window) }
    private func render() {
        content.subviews.forEach { $0.removeFromSuperview() }
        let column = NativeUI.column(spacing: 14)
        func heading(_ title: String, _ detail: String) {
            column.addArrangedSubview(NativeUI.label(title, size: 20, weight: .semibold))
            column.setCustomSpacing(8, after: column.arrangedSubviews.last!)
            column.addArrangedSubview(NativeUI.label(detail, size: 11, secondary: true))
            column.setCustomSpacing(20, after: column.arrangedSubviews.last!)
        }
        func popup(_ titles: [String], selected: Int, label: String, action: Selector) -> DesignPopUp {
            let popup = DesignPopUp(frame: .zero, pullsDown: false)
            popup.font = Design.font(ofSize: 11); popup.controlSize = .small
            popup.widthAnchor.constraint(equalToConstant: 140).isActive = true
            popup.addItems(withTitles: titles); popup.selectItem(at: selected)
            popup.target = self; popup.action = action; popup.setAccessibilityLabel(label)
            return popup
        }
        switch section {
        case 0:
            heading("通用", "熟悉一个语音键，就能完成日常输入。")
            let longPress = DesignCheck("开启", target: self, action: #selector(toggleLongPress(_:)))
            longPress.state = state.longPressRepaste ? .on : .off; longPress.setAccessibilityLabel("长按右 Ctrl 重贴上一条")
            let pause = DesignCheck("暂停", target: self, action: #selector(togglePause(_:)))
            pause.state = state.paused ? .on : .off; pause.setAccessibilityLabel("暂停远程语音输入")
            column.addArrangedSubview(NativeUI.group([
                NativeUI.row("语音键", detail: "单独轻按右 Ctrl 开始和结束；组合键保持原行为。仅在远程窗口和听写浮条中生效。", control: NativeUI.label("右 Ctrl", weight: .medium)),
                NativeUI.row("长按右 Ctrl 重贴上一条", detail: "远端没出现时补救一次，不重新录音。", control: longPress),
                NativeUI.row("投送后的剪贴板", detail: "保留听写文字时，远端没出现可直接手动粘贴。", control: popup(["保留听写文字", "恢复原内容"], selected: state.restoresClipboard ? 1 : 0, label: "投送后的剪贴板", action: #selector(selectClipboard(_:)))),
                NativeUI.row("多行文字", detail: "含换行或控制字符时先展开核对，右 Ctrl 投送。", control: NativeUI.label("先核对", secondary: true)),
                NativeUI.row("暂停远程语音输入", detail: "取消当前听写与待发任务，右 Ctrl 恢复原行为。", control: pause)
            ]))
            column.addArrangedSubview(NativeUI.label("外观跟随系统。最近文字只做短时恢复，不保存录音或长期历史。", size: 11, secondary: true))
        case 1:
            heading("远程客户端", "每个客户端分别保存设置，听写时跟随前台支持的客户端。")
            let clients = NativeUI.clientPicker(profile: state.profile) { [weak self] in self?.onProfile?($0) }
            clients.setAccessibilityLabel("客户端设置")
            column.addArrangedSubview(clients)
            column.addArrangedSubview(NativeUI.group([
                NativeUI.row("粘贴按键", detail: "按客户端的实际键位映射选择。", control: popup(["⌘ V", "Control + V"], selected: state.options.control ? 1 : 0, label: "粘贴按键", action: #selector(selectPaste(_:)))),
                NativeUI.row("同步速度", detail: "远端粘贴出旧内容时，改用兼容速度。", control: popup(SyncSpeed.allCases.map(\.title), selected: SyncSpeed.allCases.firstIndex(of: state.options.speed) ?? 1, label: "同步速度", action: #selector(selectSpeed(_:)))),
                NativeUI.row("试一次 · \(state.calibration.label)", detail: "点后切到远端输入框轻按右 Ctrl，投送一句测试文字，不启动录音。", control: ActionButton("试一次") { [weak self] in self?.onTest?() })
            ]))
            column.addArrangedSubview(NativeUI.label("请先在 \(state.profile.name) 中开启本地到远端的剪贴板同步。每次只使用一个活动同步目标。", size: 10, secondary: true))
        default:
            heading("权限与诊断", "只开启这条输入链路实际需要的权限；诊断不含正文。")
            let keys: NSView = state.inputMonitoring && state.listening ? NativeUI.label("已生效", size: 11, secondary: true)
                : state.inputMonitoring ? ActionButton("重新打开应用", primary: true) { [weak self] in self?.onRelaunch?() }
                : ActionButton("授权") { [weak self] in self?.onPermission?(false) }
            let ax: NSView = state.accessibility ? NativeUI.label("已授权", size: 11, secondary: true) : ActionButton("授权") { [weak self] in self?.onPermission?(true) }
            column.addArrangedSubview(NativeUI.group([
                NativeUI.row("辅助功能", detail: "调用豆包语音，并向远控窗口发出粘贴。", control: ax),
                NativeUI.row("输入监控", detail: state.inputMonitoring && !state.listening ? "开关已打开；需重新打开应用后生效。" : "在远程窗口中识别右 Ctrl。", control: keys)
            ]))
            let actions = NSStackView(views: [ActionButton("重新检查") { [weak self] in self?.onRecheck?() }, ActionButton("打开准备清单") { [weak self] in self?.onOpenSetup?() }])
            actions.spacing = 8; column.addArrangedSubview(actions)
            column.addArrangedSubview(NativeUI.group([
                NativeUI.row("投送诊断", detail: "仅元数据，不含正文、剪贴板内容或窗口标题。", control: ActionButton("复制诊断") { [weak self] in self?.onCopyDiagnostics?() }),
                NativeUI.row("最近文字", detail: "仅在本机内存，最多 5 条、5 分钟，退出即清空。", control: ActionButton("清除") { [weak self] in self?.onClearRecent?() })
            ]))
            let location = NativeUI.label("\(ProductInfo.name) \(ProductInfo.versionLabel) · " + Bundle.main.bundleURL.path, size: 10, secondary: true)
            location.maximumNumberOfLines = 2; location.lineBreakMode = .byTruncatingMiddle
            location.toolTip = Bundle.main.bundleURL.path; location.isSelectable = true
            column.addArrangedSubview(location)
        }
        NativeUI.fillWidth(column)
        NativeUI.place(column, in: content, inset: 0)
    }
    @objc private func togglePause(_ sender: Any?) { onPause?() }
    @objc private func toggleLongPress(_ sender: NSButton) { onLongPress?(sender.state == .on) }
    @objc private func selectClipboard(_ sender: NSPopUpButton) { onRestoreClipboard?(sender.indexOfSelectedItem == 1) }
    @objc private func selectPaste(_ sender: NSPopUpButton) { var value = state.options; value.control = sender.indexOfSelectedItem == 1; onOptions?(value) }
    @objc private func selectSpeed(_ sender: NSPopUpButton) {
        guard SyncSpeed.allCases.indices.contains(sender.indexOfSelectedItem) else { return }
        var value = state.options; value.speed = SyncSpeed.allCases[sender.indexOfSelectedItem]; onOptions?(value)
    }
}
