import AppKit
import ApplicationServices
import Carbon

final class VoiceApp: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let scheduler = MainScheduler()
    private let source: VoiceSourceAdapter = DoubaoSource()
    private let remotes: [RemoteClientAdapter] = [UURemoteAdapter(), WindowsAppAdapter()]
    private let remotePreferences = RemotePreferences()
    private var settingsProfile = RemoteClientProfile.uu
    private var rememberedTargets: [RemoteClientProfile: RemoteDestination] = [:]
    private let captures = CaptureSessions()
    private let recovery = RecoveryStore()
    private let diagnostics = DeliveryDiagnostics()
    private let composer = Composer()
    private lazy var deliveries = DeliveryQueue(board: NativeClipboard(), emitter: NativePasteEmitter(scheduler: scheduler), scheduler: scheduler)
    private var destination: RemoteDestination?
    private var sourceToken: SourceCancellation?
    private var probingID: UUID?
    private var finishRequested = false
    private var localKeyPending = false
    private var cancelledSourceNeedsCheck = false {
        didSet { if cancelledSourceNeedsCheck && !oldValue { sourceCheckSince = scheduler.now } }
    }
    private var cancellationID: UUID?
    private let shortcut = RightControlShortcut()
    private let keyTap = EventTapThread()
    private var tap: CFMachPort? { keyTap.port }
    // Read by the tap thread; written on the main thread. Guarded by tapLock.
    private let tapLock = NSLock()
    private var tapEnabled = true
    private var tapCapturing = false
    private var lastUserInputAt: TimeInterval = -1
    // A right-Ctrl press that arrived while the previous paste was still being
    // sent. It starts the next round as soon as the keys are out.
    private var pendingActivation: (pid: pid_t, at: TimeInterval)?
    private var sourceFailedID: UUID?
    private var sourceRetriedID: UUID?
    private var sourceCheckSince: TimeInterval?
    private var lastPasteAt: TimeInterval?
    // Test mode: the next right-Ctrl press in a remote window pastes fixed text.
    private var testArmed = false
    private var testFromSetup = false
    private var testJobID: UUID?
    private var testOptions: RemoteDeliveryOptions?
    private var enabled = true
    private var permissionsReady = false
    private var attention = false
    private var observer: NSObjectProtocol?
    private var statusItem: NSStatusItem!
    private let menu = NSMenu()
    private var settingsUI: SettingsController?
    private var recoveryUI: RecoveryController?
    private var setupUI: SetupController?
    private let hud = StatusHUD()
    private var timer: Timer?
    private var sessionTimer: Timer?
    private var message = "等待权限"

    func applicationDidFinishLaunching(_ notification: Notification) {
        let own = Bundle.main.bundleIdentifier ?? "local.jackson.UURightOptionVoice"
        if let existing = NSRunningApplication.runningApplications(withBundleIdentifier: own).first(where: { $0.processIdentifier != getpid() }) {
            let alert = NSAlert()
            alert.messageText = "\(ProductInfo.name) 的另一个实例仍在运行"
            alert.informativeText = "本次打开的是 \(ProductInfo.versionLabel)。解压或覆盖文件不会更新已运行的进程。请先从菜单栏退出旧实例，再打开当前应用。\n\n正在运行的位置：\n\(existing.bundleURL?.path ?? "未知")\n进程：\(existing.processIdentifier)"
            alert.addButton(withTitle: "知道了")
            NSApp.activate(ignoringOtherApps: true); alert.runModal()
            NSApp.terminate(nil); return
        }
        setupMenu()
        captures.onChange = { [weak self] in self?.syncTapState(); self?.updateMenuTitle() }
        composer.onPrimary = { [weak self] in self?.primaryAction() }
        composer.onCancel = { [weak self] in self?.cancelCapture() }
        composer.onChanged = { [weak self] in
            guard let self, let id = self.captures.current?.id else { return }
            self.captures.update(id) { $0.lastChangeAt = self.scheduler.now }
        }
        deliveries.canDrain = { [weak self] in
            guard let self else { return false }
            return self.captures.current == nil && !self.localKeyPending && !self.cancelledSourceNeedsCheck && !self.isManagingUI
        }
        deliveries.mayActivate = { [weak self] job in
            guard let self, self.enabled, self.captures.current == nil else { return false }
            return job.focusTarget || job.destination.isFrontmost() || NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid()
        }
        deliveries.modifiersClear = {
            let conflicting: CGEventFlags = [.maskCommand, .maskControl, .maskAlternate, .maskShift]
            return CGEventSource.flagsState(.combinedSessionState).intersection(conflicting).isEmpty
        }
        deliveries.userActedSince = { [weak self] time in
            guard let self else { return true }
            self.tapLock.lock(); defer { self.tapLock.unlock() }
            return self.lastUserInputAt >= time
        }
        deliveries.onOutcome = { [weak self] job, outcome in self?.received(job, outcome: outcome) }
        deliveries.onRestoreFailure = { [weak self] in self?.setStatus("原剪贴板恢复失败；听写文字可从最近文字找回") }
        deliveries.onChange = { [weak self] in
            self?.updateMenuTitle()
            DispatchQueue.main.async { self?.resumePendingActivation() }
        }
        deliveries.onTrace = { [weak self] id, stage, now in self?.diagnostics.record(id: id, stage: stage, now: now) }
        observer = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            guard let self, let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
            self.tapLock.lock(); self.shortcut.cancelActivation(); self.tapLock.unlock()
            self.rememberRemote(app)
            if let state = self.captures.current, app.processIdentifier != getpid(), !self.source.owns(app) {
                self.sourceToken?.cancel()
                self.captures.update(state.id) { $0.phase = .review }
                self.composer.setState(.review, message: "焦点已离开，自动投送已暂停。先结束豆包录音，再核对文字。")
            }
            self.deliveries.pump()
        }
        refreshPermissions()
        // Calibration needs a remembered remote window; clients opened before
        // this app launched never sent an activation notification.
        for app in NSWorkspace.shared.runningApplications where remotes.contains(where: { $0.matches(app) }) { rememberRemote(app) }
        // The checklist appears only while something required is missing.
        if !UserDefaults.standard.bool(forKey: "onboardingCompleted") || !setupStatus().ready { showChecklist(nil) }
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.refreshPermissions()
            self.recheckCancelledSource()
            self.recovery.prune(now: self.scheduler.now)
            self.refreshNativeUI(); self.deliveries.pump(); self.updateMenuTitle()
        }
    }
    private func startPolling() {
        sessionTimer?.invalidate()
        sessionTimer = Timer.scheduledTimer(withTimeInterval: 0.08, repeats: true) { [weak self] _ in self?.pollSession() }
    }
    private func stopPolling() { sessionTimer?.invalidate(); sessionTimer = nil; probingID = nil }

    // MARK: Menu bar

    private func setupMenu() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        menu.delegate = self; menu.autoenablesItems = false
        statusItem.menu = menu; updateMenuTitle()
    }
    private enum Readiness { case ready, listening, delivering, attention, paused }
    private var readiness: Readiness {
        if !enabled { return .paused }
        if !permissionsReady || attention { return .attention }
        if captures.current != nil { return .listening }
        if deliveries.isInjecting || deliveries.pendingCount > 0 { return .delivering }
        return .ready
    }
    private func updateMenuTitle() {
        guard let button = statusItem?.button else { return }
        let state = readiness
        var symbol = "waveform"
        switch state {
        case .paused: symbol = "pause.circle"
        case .attention: symbol = "waveform.badge.exclamationmark"
        case .delivering: symbol = "arrow.up.right"
        case .ready, .listening: break
        }
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: "远程语音输入")
            ?? NSImage(systemSymbolName: "exclamationmark.circle", accessibilityDescription: "远程语音输入")
        image?.isTemplate = true; button.image = image; button.title = ""
        button.contentTintColor = state == .listening ? .controlAccentColor : state == .attention ? .systemOrange : nil
        button.toolTip = message + (deliveries.pendingCount > 0 ? " · \(deliveries.pendingCount) 条等待投送" : "")
    }
    private func setStatus(_ value: String) {
        message = value; updateMenuTitle(); refreshNativeUI()
    }
    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === self.menu else { return }
        attention = false
        recovery.prune(now: scheduler.now); menu.removeAllItems()
        let (label, color): (String, NSColor) = {
            switch readiness {
            case .paused: return ("已暂停", .secondaryLabelColor)
            case .attention: return ("需要设置", .systemOrange)
            case .listening: return ("听写中", .controlAccentColor)
            case .delivering: return ("投送中", .controlAccentColor)
            case .ready: return ("就绪", .systemGreen)
            }
        }()
        let head = NSMenuItem(title: label, action: nil, keyEquivalent: ""); head.isEnabled = false
        let title = NSMutableAttributedString(string: "● ", attributes: [.foregroundColor: color, .font: NSFont.menuFont(ofSize: 11)])
        title.append(NSAttributedString(string: label, attributes: [.font: NSFont.boldSystemFont(ofSize: 13), .foregroundColor: NSColor.labelColor]))
        head.attributedTitle = title
        head.toolTip = "\(ProductInfo.name) \(ProductInfo.versionLabel)\n\(Bundle.main.bundleURL.path)"
        menu.addItem(head)
        let detail = NSMenuItem(title: message, action: nil, keyEquivalent: ""); detail.isEnabled = false
        menu.addItem(detail); menu.addItem(.separator())
        let section = NSMenuItem(title: "最近文字 · 5 分钟后自动清除", action: nil, keyEquivalent: ""); section.isEnabled = false
        menu.addItem(section)
        if recovery.records.isEmpty {
            let empty = NSMenuItem(title: "暂无最近文字", action: nil, keyEquivalent: ""); empty.isEnabled = false
            menu.addItem(empty)
        }
        let idle = enabled && !localKeyPending && captures.current == nil && !deliveries.isInjecting
        for record in recovery.records {
            let preview = record.text.replacingOccurrences(of: "\n", with: " ")
            let short = preview.count > 18 ? String(preview.prefix(18)) + "…" : preview
            let age = max(0, Int(scheduler.now - record.createdAt))
            let item = NSMenuItem(title: "", action: nil, keyEquivalent: "")
            let line = NSMutableAttributedString()
            if case .failed = record.status { line.append(NSAttributedString(string: "未投送 · ", attributes: [.foregroundColor: NSColor.systemOrange])) }
            if case .cancelled = record.status { line.append(NSAttributedString(string: "已取消 · ", attributes: [.foregroundColor: NSColor.secondaryLabelColor])) }
            line.append(NSAttributedString(string: short))
            line.append(NSAttributedString(string: "  " + (age < 60 ? "\(age) 秒前" : "\(age / 60) 分钟前"), attributes: [.foregroundColor: NSColor.secondaryLabelColor, .font: NSFont.menuFont(ofSize: 11)]))
            item.attributedTitle = line
            let actions = NSMenu(); actions.autoenablesItems = false
            let busy = deliveries.isSending(record.id)
            for (title, selector, allowed) in [("复制原文", #selector(copyRecord(_:)), true),
                                               ("再投送到原窗口", #selector(redeliverRecord(_:)), idle && !busy),
                                               ("打开核对…", #selector(restoreRecord(_:)), idle && !busy)] {
                let entry = NSMenuItem(title: title, action: selector, keyEquivalent: "")
                entry.target = self; entry.representedObject = record.id.uuidString; entry.isEnabled = allowed
                actions.addItem(entry)
            }
            item.submenu = actions
            menu.addItem(item)
        }
        addItem(menu, "全部最近文字…", #selector(showRecovery(_:)))
        if deliveries.pendingCount > 0 { addItem(menu, "取消等待中的投送（\(deliveries.pendingCount) 条）", #selector(cancelDeliveries(_:))) }
        menu.addItem(.separator())
        addItem(menu, enabled ? "暂停远程语音输入" : "恢复远程语音输入", #selector(toggle(_:)))
        addItem(menu, "设置…", #selector(showSettings(_:)), key: ",")
        menu.addItem(.separator())
        addItem(menu, "退出 \(ProductInfo.name)", #selector(quit(_:)), key: "q")
        updateMenuTitle()
    }
    private func addItem(_ menu: NSMenu, _ title: String, _ action: Selector, key: String = "") {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self; menu.addItem(item)
    }

    // MARK: Permissions and key tap

    private func refreshPermissions() {
        guard enabled else { return }
        guard AXIsProcessTrusted(), CGPreflightListenEventAccess() else {
            permissionsReady = false
            deliveries.suspended = true
            if captures.current != nil { cancelCapture() }
            deliveries.cancelAll(reason: "系统权限已变化，已停止投送")
            if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
            let status = AXIsProcessTrusted() ? "请授权输入监控，然后重新打开工具" : "请授权辅助功能，再返回工具"
            if message != status { setStatus(status) }
            return
        }
        deliveries.suspended = false
        if tap == nil { installTap() }
        else if let tap, !CGEvent.tapIsEnabled(tap: tap) { CGEvent.tapEnable(tap: tap, enable: true) }
        permissionsReady = tap != nil
        if message == "等待权限" || message.hasPrefix("请授权") { setStatus("已就绪：在 UU / Windows App 中轻按右 Ctrl 开始听写") }
    }
    private func syncTapState() {
        tapLock.lock(); tapEnabled = enabled; tapCapturing = captures.current != nil; tapLock.unlock()
    }
    private func installTap() {
        let types: [CGEventType] = [.flagsChanged, .keyDown, .keyUp, .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .otherMouseDown, .otherMouseUp, .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged, .scrollWheel]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        // Runs on the tap thread: only lock-guarded state and immutable adapters.
        let callback: CGEventTapCallBack = { proxy, type, event, context in
            guard let context else { return Unmanaged.passUnretained(event) }
            let app = Unmanaged<VoiceApp>.fromOpaque(context).takeUnretainedValue()
            app.tapLock.lock(); defer { app.tapLock.unlock() }
            if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                app.shortcut.reset()
                if app.tapEnabled, let tap = app.keyTap.port { CGEvent.tapEnable(tap: tap, enable: true) }
                return Unmanaged.passUnretained(event)
            }
            if [.keyDown, .leftMouseDown, .rightMouseDown, .otherMouseDown].contains(type) {
                let marker = event.getIntegerValueField(.eventSourceUserData)
                if marker != localFinishMarker && marker != localPasteMarker { app.lastUserInputAt = ProcessInfo.processInfo.systemUptime }
            }
            let isShortcut = type == .flagsChanged && event.getIntegerValueField(.keyboardEventKeycode) == RightControlShortcut.keyCode
            let front = isShortcut ? NSWorkspace.shared.frontmostApplication : nil
            let local = front.map { $0.processIdentifier == getpid() || app.source.owns($0) } ?? false
            let remote = front.map { front in app.remotes.contains { $0.matches(front) } } ?? false
            let result = app.shortcut.handle(type: type, event: event,
                eligible: app.tapEnabled && (remote || (app.tapCapturing && local)),
                context: front?.processIdentifier, replay: { $0.tapPostEvent(proxy) })
            if result.activate {
                let expectedPID = front?.processIdentifier
                let long = app.shortcut.lastHold >= RightControlShortcut.longPress
                DispatchQueue.main.async {
                    guard NSWorkspace.shared.frontmostApplication?.processIdentifier == expectedPID else { return }
                    app.activateVoice(long: long)
                }
            }
            if result.consume { return nil }
            return Unmanaged.passUnretained(event)
        }
        syncTapState()
        guard keyTap.start(mask: mask, callback: callback, userInfo: Unmanaged.passUnretained(self).toOpaque()) else {
            let status = "键盘监听未启动：输入监控需重新打开应用后生效"
            if message != status { setStatus(status) }
            refreshNativeUI(); return
        }
        setStatus("已就绪：在 UU / Windows App 中轻按右 Ctrl 开始听写")
    }
    // Feedback for a right-Ctrl press that could not start; the menu bar status
    // alone is invisible while the user looks at the remote window.
    private func hint(_ title: String, _ detail: String) {
        setStatus(title + "：" + detail)
        hud.show(title, detail: detail, tone: .warning, duration: 4)
    }

    // MARK: Dictation

    private func activateVoice(long: Bool = false) {
        if captures.current != nil { primaryAction(); return }
        guard enabled else { return }
        if localKeyPending || deliveries.isInjecting {
            if long { hud.show("上一条正在投送，稍后再长按右 Ctrl", duration: 2); return }
            if let front = NSWorkspace.shared.frontmostApplication { pendingActivation = (front.processIdentifier, scheduler.now) }
            hud.show("上一条正在投送，完成后自动开始听写", duration: 2)
            return
        }
        if long && !testArmed && remotePreferences.longPressRepaste { repasteLast(); return }
        guard let remote = NSWorkspace.shared.frontmostApplication,
              let adapter = remotes.first(where: { $0.matches(remote) }), let target = adapter.capture(remote) else {
            hint("没有找到远程窗口", "请先点一下 UU / Windows App 的远端输入框，再按右 Ctrl。"); return
        }
        pendingActivation = nil
        rememberedTargets[target.profile] = target
        if testArmed { runTest(target); return }
        destination = target
        settingsUI?.window.orderOut(nil); recoveryUI?.window.orderOut(nil); setupUI?.window.orderOut(nil); hud.hide()
        cancellationID = nil
        let id = captures.begin(now: scheduler.now)
        let token = SourceCancellation(); sourceToken = token; finishRequested = false
        sourceFailedID = nil; sourceRetriedID = nil
        composer.show(destination: target.label, source: source.name)
        startPolling(); setStatus("正在启动本地\(source.name)语音")
        if cancelledSourceNeedsCheck { checkPreviousSource(id: id, token: token, target: target, attempt: 0) }
        else { startSource(id: id, token: token, target: target) }
    }
    private func resumePendingActivation() {
        guard let pending = pendingActivation, !deliveries.isInjecting, !localKeyPending, captures.current == nil else { return }
        pendingActivation = nil
        guard enabled, scheduler.now - pending.at < 5,
              NSWorkspace.shared.frontmostApplication?.processIdentifier == pending.pid else { return }
        activateVoice()
    }
    // A cancelled round may leave Doubao recording. Never press its menu blind:
    // adopt a recording that is still running, or start once it is idle.
    private func checkPreviousSource(id: UUID, token: SourceCancellation, target: RemoteDestination, attempt: Int) {
        source.probe { [weak self] visibility in
            guard let self, self.captures.isCurrent(id), !token.isCancelled else { return }
            switch visibility {
            case .hidden:
                self.cancelledSourceNeedsCheck = false
                self.startSource(id: id, token: token, target: target)
            case .visible:
                self.cancelledSourceNeedsCheck = false
                self.captures.update(id) { $0.phase = .listening; $0.observe(.visible, now: self.scheduler.now) }
                self.composer.setState(.listening, message: "豆包仍在录音，已接入这一轮")
                self.setStatus("正在本地听写 → " + target.label)
                if self.finishRequested { self.finishVoice() }
            case .unknown:
                if attempt < 3 {
                    self.scheduler.after(0.15) { [weak self] in self?.checkPreviousSource(id: id, token: token, target: target, attempt: attempt + 1) }
                } else {
                    self.cancelledSourceNeedsCheck = false
                    self.startSource(id: id, token: token, target: target)
                }
            }
        }
    }
    private func startSource(id: UUID, token: SourceCancellation, target: RemoteDestination) {
        source.start(cancellation: token) { [weak self] result in
            guard let self, self.captures.isCurrent(id), !token.isCancelled else { return }
            switch result {
            case .success:
                self.sourceFailedID = nil
                self.captures.update(id) { $0.phase = .listening }
                self.composer.setState(.listening, message: "请说话…")
                self.setStatus("正在本地听写 → " + target.label)
                if self.finishRequested { self.finishVoice() }
            case .failure(let error):
                // The input-method menu is occasionally not ready on the first try.
                if self.sourceRetriedID != id {
                    self.sourceRetriedID = id
                    self.scheduler.after(0.3) { [weak self] in
                        guard let self, self.captures.isCurrent(id), !token.isCancelled, self.captures.current?.phase == .starting else { return }
                        self.startSource(id: id, token: token, target: target)
                    }
                    return
                }
                self.sourceFailedID = id
                self.review(error.message + "。轻按右 Ctrl 重试，Esc 取消；已有文字会保留。", key: "右 Ctrl 重试")
            }
        }
    }
    private func primaryAction() {
        guard let state = captures.current else { return }
        switch state.phase {
        case .starting: finishRequested = true; composer.setState(.starting, message: "启动完成后将结束本次听写…")
        case .listening: finishVoice()
        case .settling: break
        case .review:
            if sourceFailedID == state.id, TextPolicy.isEmpty(composer.text.string), let target = destination {
                sourceFailedID = nil; sourceRetriedID = state.id
                let token = SourceCancellation(); sourceToken = token; finishRequested = false
                captures.update(state.id) { $0.phase = .starting }
                composer.setState(.starting, message: "正在重新唤起豆包…")
                startSource(id: state.id, token: token, target: target)
                return
            }
            deliverCurrentText(confirmed: true)
        }
    }
    private func finishVoice() {
        guard let state = captures.current, state.phase == .listening else { return }
        captures.update(state.id) { $0.finish(now: scheduler.now) }
        composer.setState(.settling, message: "等待豆包定稿…")
        composer.focus()
        scheduler.after(0.1) { [weak self] in
            guard let self, self.captures.isCurrent(state.id), self.captures.current?.phase == .settling else { return }
            if !self.source.stop(ownsInputFocus: self.composer.ownsInputFocus) { self.review("焦点已变化，未发送结束键。请先结束豆包录音再重试。") }
        }
    }
    private func pollSession() {
        guard let state = captures.current else { return }
        composer.refresh()
        if !state.manual && probingID != state.id {
            probingID = state.id
            let probeBegan = scheduler.now
            source.probe { [weak self] visibility in
                guard let self, self.captures.isCurrent(state.id), self.probingID == state.id else { return }
                self.probingID = nil
                let wasListening = self.captures.current?.phase == .listening
                // A slow pre-stop AX request is not a fresh post-stop observation.
                self.captures.update(state.id) { $0.observe(visibility, now: probeBegan) }
                if wasListening && self.captures.current?.phase == .settling { self.composer.setState(.settling, message: "已结束录音，等待最终文字…") }
                self.checkSettled()
            }
        }
        checkSettled()
    }
    private func checkSettled() {
        guard let state = captures.current, state.phase == .settling else { return }
        let text = composer.text.string, marked = composer.text.hasMarkedText()
        if state.ready(text: text, marked: marked, now: scheduler.now) {
            deliverCurrentText(confirmed: false)
        } else if state.heardNothing(text: text, marked: marked, now: scheduler.now) {
            endWithoutText(state.id)
        } else if let stopped = state.stoppedAt, scheduler.now - stopped > 30 { review("等待定稿超时，文字已保留。先结束豆包录音，再核对后投送。") }
    }
    // Recording ended with no text: close the round and hand focus back.
    private func endWithoutText(_ id: UUID) {
        let target = destination
        sourceToken?.cancel(); sourceToken = nil
        captures.end(id); stopPolling(); composer.hide(); destination = nil
        if let target, target.isValid() { _ = target.activate() }
        setStatus("没有识别到文字")
        hud.show("没有识别到文字，再按右 Ctrl 重试", tone: .warning, duration: 3)
        deliveries.pump()
    }
    private func review(_ reason: String, key: String? = nil) {
        guard let id = captures.current?.id else { return }
        captures.update(id) { $0.phase = .review }; composer.setState(.review, message: reason, reviewKey: key); setStatus(reason)
    }
    private func currentInputSource() -> String {
        guard let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(), let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { return "unknown" }
        return Unmanaged<CFString>.fromOpaque(raw).takeUnretainedValue() as String
    }
    private func deliverCurrentText(confirmed: Bool) {
        guard let state = captures.current, let target = destination else { return }
        let origin: DeliveryDiagnostics.Origin = state.manual ? .recovery : .voice
        // Every refusal is recorded; a review card that "did nothing" must be
        // traceable from the copied diagnostics.
        func refuse(_ code: String, _ reason: String) {
            diagnostics.refused(code, origin: origin, now: scheduler.now); review(reason)
            if confirmed { hud.show("未投送", detail: reason, tone: .warning, duration: 4) }
        }
        guard enabled else { refuse("paused", "工具已暂停，恢复后再投送"); return }
        guard !composer.text.hasMarkedText() else { refuse("markedText", "输入法仍有未提交文字，请先完成识别或选词"); return }
        if !state.manual {
            guard state.visibility == .hidden, let observed = state.observedAt, scheduler.now - observed <= 1 else {
                refuse("sourceNotStopped", "尚不能确认录音已结束。请在豆包中结束录音，状态可读后再投送。"); return
            }
            cancelledSourceNeedsCheck = false
        } else if cancelledSourceNeedsCheck {
            refuse("previousSourceUnknown", "请先确认上次豆包录音已结束，再取消此轮并重新开始。"); return
        }
        let text = composer.text.string
        guard !TextPolicy.isEmpty(text) else { refuse("empty", "还没有收到文字，可继续输入或取消后重新听写"); return }
        guard TextPolicy.sizeAllowed(text) else { refuse("tooLarge", "正文超过 64 KB，请展开后分段投送；当前文字保持完整"); composer.setExpanded(true); return }
        if TextPolicy.needsReview(text) && !confirmed {
            review("文字含换行或控制字符，请核对。轻按右 Ctrl 会原样投送。")
            composer.setExpanded(true); return
        }
        guard target.isValid() else { refuse("windowChanged", "原远程窗口已改变，请回到原窗口后重试；文字已保留"); return }
        // A right-Ctrl confirmation is an explicit request: it may bring the
        // window forward from anywhere, exactly like "再贴一次".
        guard confirmed || target.isFrontmost() || NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid() else {
            refuse("frontChanged", "前台应用已变化，已停止自动投送；请返回浮条核对"); return
        }
        let options = remotePreferences.options(for: target.profile)
        // The first paste after a quiet period has been the one that went
        // missing; give the client's clipboard channel a little more time.
        let idle = lastPasteAt.map { scheduler.now - $0 > 120 } ?? true
        let delay = idle ? max(options.syncDelay, 0.8) : options.syncDelay
        let job = DeliveryJob(id: UUID(), text: text, destination: target, control: options.control, syncDelay: delay,
                              restoreClipboard: remotePreferences.restoresClipboard, focusTarget: confirmed)
        diagnostics.begin(id: job.id, origin: origin, profile: target.profile, control: job.control, delay: job.syncDelay, inputSource: currentInputSource(), targetIdentity: target.diagnosticIdentity)
        guard deliveries.enqueue(job) else { refuse("queueFull", "已有多条文字等待投送；当前文字保留，稍后再试"); return }
        recovery.save(RecoveryRecord(id: job.id, text: text, destination: target, createdAt: scheduler.now, status: .queued), now: scheduler.now)
        sourceToken?.cancel(); sourceToken = nil
        captures.end(state.id); stopPolling(); composer.hide(); destination = nil
        setStatus("文字已进入投送队列；可在原远程窗口继续下一轮听写")
        // Same route as re-delivery: the queue activates the client once, right
        // before its own checks. Only a job left waiting behind an earlier one
        // hands focus back early, so the user is not stranded in this app.
        deliveries.pump()
        if deliveries.contains(job.id) && deliveries.isSending(job.id) && !deliveries.isActive(job.id) {
            _ = target.activate()
            hud.show("已排队，按顺序投送；可继续下一轮听写", duration: 2)
        }
    }
    private func cancelCapture() {
        hud.hide(); testArmed = false
        guard let state = captures.current else { composer.hide(); return }
        sourceToken?.cancel(); sourceToken = nil
        let text = composer.text.string
        if !TextPolicy.isEmpty(text), TextPolicy.sizeAllowed(text), let destination {
            recovery.save(RecoveryRecord(id: UUID(), text: text, destination: destination, createdAt: scheduler.now, status: .cancelled, sourceMustStop: !state.manual), now: scheduler.now)
        }
        let sentLocalEscape = !state.manual && source.cancel(ownsInputFocus: composer.ownsInputFocus)
        if !state.manual { cancelledSourceNeedsCheck = true }
        captures.end(state.id); destination = nil; stopPolling()
        // Do not immediately focus UU after posting a local Escape. Keep its
        // receiver until the event has had a run-loop turn; no new capture or
        // queued delivery may take its focus during this small cleanup interval.
        if sentLocalEscape {
            localKeyPending = true
            let cleanupID = UUID()
            cancellationID = cleanupID
            scheduler.after(0.12) { [weak self] in
                guard let self else { return }
                self.localKeyPending = false
                self.composer.hide()
                self.source.probe { [weak self] visibility in
                    guard let self, self.cancellationID == cleanupID, self.captures.current == nil else { return }
                    self.cancellationID = nil
                    if visibility == .hidden {
                        self.cancelledSourceNeedsCheck = false
                        self.deliveries.pump()
                    }
                    // Otherwise earlier queued texts wait; the periodic check
                    // releases them once Doubao is confirmed idle.
                    self.resumePendingActivation()
                }
            }
        } else {
            composer.hide()
            deliveries.pump()
        }
        setStatus("本次投送已取消；已有文字可从最近文字恢复。若豆包仍在录音，请手动结束。")
    }

    // MARK: Delivery results, re-paste and recovery

    private func received(_ job: DeliveryJob, outcome: DeliveryOutcome) {
        if job.id == testJobID { testFinished(job, outcome: outcome); return }
        let record = RecoveryRecord(id: job.id, text: job.text, destination: job.destination, createdAt: scheduler.now, status: .queued)
        switch outcome {
        case .failed(let reason):
            var failed = record; failed.status = .failed(reason)
            recovery.save(failed, now: scheduler.now)
            attention = true
            if captures.current == nil {
                setStatus(reason + "；可从菜单“最近文字”恢复")
                hud.show("本次未投送", detail: reason + "。文字已保留。", tone: .warning, actions: [
                    .init(title: "重新投送", primary: true) { [weak self] in self?.redeliver(failed, focusTarget: true) },
                    .init(title: "打开核对") { [weak self] in self?.restoreRecord(id: failed.id) }
                ], duration: 8)
            }
        case .unconfirmed(let milliseconds):
            lastPasteAt = scheduler.now; attention = false
            var sent = record; sent.status = .unconfirmed
            recovery.save(sent, now: scheduler.now)
            if captures.current == nil {
                setStatus("已投送 · \(job.text.count) 字 · 本机 \(milliseconds) ms；远端无回执")
                if enabled, !localKeyPending, job.destination.isFrontmost() {
                    hud.show("已投送 \(job.text.count) 字", tone: .success, actions: [
                        .init(title: "再贴一次") { [weak self] in self?.redeliver(sent, focusTarget: true) }
                    ], duration: 3.5)
                }
            }
        }
        updateMenuTitle()
    }
    // An explicit user request: no confirmation step. Every refusal is shown;
    // a silent return here looked exactly like "the button does nothing".
    private func redeliver(_ record: RecoveryRecord, focusTarget: Bool) {
        guard enabled else { hud.show("已暂停，恢复后再投送", tone: .warning, duration: 3); return }
        guard captures.current == nil else { return }
        guard !deliveries.isSending(record.id) else { hud.show("这条正在投送，请稍候", duration: 2); return }
        guard record.destination.isValid() else {
            hint("原远程窗口已改变", "回到原窗口后再试；也可从菜单“最近文字”复制原文。"); return
        }
        let options = remotePreferences.options(for: record.destination.profile)
        // A fresh job: the previous attempt may still hold the clipboard.
        let job = DeliveryJob(id: UUID(), text: record.text, destination: record.destination, control: options.control,
                              syncDelay: options.syncDelay, restoreClipboard: remotePreferences.restoresClipboard, focusTarget: focusTarget)
        diagnostics.begin(id: job.id, origin: .recovery, profile: job.destination.profile, control: job.control, delay: job.syncDelay, inputSource: currentInputSource(), targetIdentity: job.destination.diagnosticIdentity)
        guard deliveries.enqueue(job) else { hud.show("已有多条文字等待投送，稍后再试", tone: .warning, duration: 3); return }
        recovery.remove(record.id)
        recovery.save(RecoveryRecord(id: job.id, text: record.text, destination: record.destination, createdAt: scheduler.now, status: .queued, sourceMustStop: record.sourceMustStop), now: scheduler.now)
        hud.show("正在再贴一次…", duration: 1.5)
        deliveries.pump()
    }
    private func repasteLast() {
        recovery.prune(now: scheduler.now)
        let last = recovery.records.first { record in
            switch record.status { case .unconfirmed, .failed: return true; default: return false }
        }
        guard let last else { hint("没有可重贴的文字", "最近 5 分钟内没有投送过的文字。"); return }
        guard last.destination.isValid(), last.destination.isFrontmost() else {
            hint("上一条属于其他窗口", "回到“\(last.destination.label)”后再长按右 Ctrl。"); return
        }
        redeliver(last, focusTarget: false)
    }
    private func record(from sender: NSMenuItem) -> RecoveryRecord? {
        guard let string = sender.representedObject as? String, let id = UUID(uuidString: string) else { return nil }
        recovery.prune(now: scheduler.now)
        return recovery.records.first { $0.id == id }
    }
    @objc private func copyRecord(_ sender: NSMenuItem) {
        guard let record = record(from: sender) else { return }
        let board = NSPasteboard.general; board.clearContents()
        hud.show(board.setString(record.text, forType: .string) ? "已复制原文，可手动粘贴" : "复制失败，请重试", duration: 2)
    }
    @objc private func redeliverRecord(_ sender: NSMenuItem) {
        guard let record = record(from: sender) else { return }
        redeliver(record, focusTarget: true)
    }
    @objc private func restoreRecord(_ sender: NSMenuItem) {
        guard let record = record(from: sender) else { return }
        restoreRecord(id: record.id)
    }
    private func restoreRecord(id: UUID) {
        guard captures.current == nil, !deliveries.isInjecting, !localKeyPending else { return }
        recovery.prune(now: scheduler.now)
        guard enabled, !deliveries.isSending(id), let record = recovery.records.first(where: { $0.id == id }) else { return }
        settingsUI?.window.orderOut(nil); recoveryUI?.window.orderOut(nil); setupUI?.window.orderOut(nil); hud.hide()
        testArmed = false
        destination = record.destination
        let captureID = captures.begin(now: scheduler.now, manual: !record.sourceMustStop)
        captures.update(captureID) { $0.phase = .review }
        if record.sourceMustStop { startPolling() }
        composer.show(text: record.text, destination: record.destination.label, source: "恢复文字", review: true)
        let note: String
        switch record.status {
        case .unconfirmed: note = "上次已发出粘贴。先看一眼远端，确实没收到再按右 Ctrl 投送。"
        case .failed(let reason): note = reason + "；确认目标后可重新投送。"
        case .cancelled: note = "已恢复取消前的文字。请确认豆包录音已结束，再投送。"
        case .queued: note = "文字已恢复，请核对后投送。"
        }
        composer.setState(.review, message: note); composer.setExpanded(true)
    }
    @objc private func clearRecovery(_ sender: Any?) {
        recovery.clear(); recoveryUI?.reload([], now: scheduler.now)
        setStatus("最近文字已清除；正在编辑及排队的任务不受影响")
    }
    @objc private func cancelDeliveries(_ sender: Any?) { deliveries.cancelAll(reason: "已取消等待中的投送"); setStatus("等待中的投送已取消；文字可从最近文字恢复") }
    @objc private func toggle(_ sender: Any?) {
        tapLock.lock(); shortcut.cancelActivation(); tapLock.unlock()
        enabled.toggle(); deliveries.suspended = !enabled; syncTapState()
        if !enabled { pendingActivation = nil; testArmed = false }
        if !enabled { cancelCapture(); deliveries.cancelAll(reason: "工具已暂停，投送已取消") }
        setStatus(enabled ? "已恢复：在远程窗口轻按右 Ctrl 开始听写" : "已暂停：右 Ctrl 保持原样")
        refreshPermissions()
    }
    @objc private func quit(_ sender: Any?) { NSApp.terminate(nil) }
    private func copyDiagnostics() {
        guard captures.current == nil, !deliveries.isInjecting, deliveries.pendingCount == 0 else {
            setStatus("请先结束当前听写和投送，再复制诊断"); return
        }
        let board = NSPasteboard.general
        board.clearContents()
        setStatus(board.setString(diagnostics.report, forType: .string) ? "已复制投送诊断；不含正文、剪贴板内容或窗口标题" : "诊断复制失败，请重试")
    }
    func applicationWillTerminate(_ notification: Notification) {
        hud.hide(); deliveries.suspended = true; cancelCapture(); deliveries.cancelAll(reason: "工具退出"); recovery.clear()
        timer?.invalidate(); stopPolling()
        keyTap.stop()
        if let observer { NSWorkspace.shared.notificationCenter.removeObserver(observer) }
    }

    // MARK: Test in the real remote window

    private func armTest(profile: RemoteClientProfile?) {
        guard enabled, captures.current == nil else { return }
        testArmed = true
        settingsUI?.window.orderOut(nil); setupUI?.window.orderOut(nil); recoveryUI?.window.orderOut(nil)
        hud.show("切到远端输入框，轻按右 Ctrl", tone: .test, actions: [
            .init(title: "取消") { [weak self] in self?.testArmed = false; self?.returnToSetup() }
        ], duration: nil)
        if let profile, let target = rememberedTargets[profile], target.isValid() { _ = target.activate() }
    }
    private func runTest(_ target: RemoteDestination) {
        testArmed = false
        let formatter = DateFormatter(); formatter.dateFormat = "HHmmss"
        let options = remotePreferences.options(for: target.profile)
        let job = DeliveryJob(id: UUID(), text: "\(ProductInfo.name) 测试 " + formatter.string(from: Date()), destination: target,
                              control: options.control, syncDelay: options.syncDelay, restoreClipboard: remotePreferences.restoresClipboard)
        diagnostics.begin(id: job.id, origin: .calibration, profile: target.profile, control: job.control, delay: job.syncDelay, inputSource: currentInputSource(), targetIdentity: target.diagnosticIdentity)
        guard deliveries.enqueue(job) else { hud.show("已有文字等待投送，稍后再试", tone: .warning, duration: 3); return }
        testJobID = job.id; testOptions = options; settingsProfile = target.profile
        hud.show("正在投送测试文字…", tone: .test, duration: nil)
        deliveries.pump()
    }
    private func testFinished(_ job: DeliveryJob, outcome: DeliveryOutcome) {
        let profile = job.destination.profile
        let unchanged = remotePreferences.options(for: profile) == testOptions
        testJobID = nil; testOptions = nil
        switch outcome {
        case .unconfirmed:
            lastPasteAt = scheduler.now
            guard unchanged else { hud.show("投送设置已变化，请重新试一次", tone: .warning, duration: 4); return }
            hud.show("远端出现测试文字了吗？", detail: "“\(job.text)”", tone: .test, actions: [
                .init(title: "出现了", primary: true) { [weak self] in self?.testPassed(profile) },
                .init(title: "没有 / 不完整") { [weak self] in self?.testFailed(profile, reason: nil) }
            ], duration: nil)
        case .failed(let reason): testFailed(profile, reason: reason)
        }
    }
    private func testPassed(_ profile: RemoteClientProfile) {
        remotePreferences.saveCalibration(true, for: profile)
        hud.show("\(profile.name) 可以用了。右 Ctrl 开始听写", tone: .success, duration: 3.5)
        refreshNativeUI(); returnToSetup()
    }
    // Map what the user saw to the one setting that usually explains it.
    private func testFailed(_ profile: RemoteClientProfile, reason: String?) {
        remotePreferences.saveCalibration(false, for: profile)
        var options = remotePreferences.options(for: profile)
        let compatible = options.speed == .compatible
        hud.show("没出现？按看到的现象调整后重试", detail: reason.map { $0 + "。" } ?? "调整后会立即用同一个远端窗口再测一次。", tone: .warning, actions: [
            .init(title: "关闭") { [weak self] in self?.returnToSetup() }
        ], options: [
            .init(title: "贴出了别的旧内容", detail: compatible ? "已是兼容速度，可再试一次" : "剪贴板同步慢于粘贴", action: .init(title: compatible ? "再试一次" : "改用兼容速度并重试") { [weak self] in
                options.speed = .compatible; self?.saveOptions(options, for: profile); self?.armTest(profile: profile)
            }),
            .init(title: "出现一个 v，或什么都没有", detail: options.control ? "远端可能需要 ⌘ V" : "远端可能需要 Control + V", action: .init(title: options.control ? "改用 ⌘ V 并重试" : "改用 Control + V 并重试") { [weak self] in
                options.control.toggle(); self?.saveOptions(options, for: profile); self?.armTest(profile: profile)
            }),
            .init(title: "手动复制粘贴也不行", detail: "\(profile.name) 的剪贴板同步没有打开", action: .init(title: "查看开启方法") { [weak self] in
                self?.hud.show("在 \(profile.name) 中开启剪贴板同步", detail: "在连接设置里打开“剪贴板”同步（本地 → 远端）。先在本机复制一段文字，到远端手动粘贴确认能贴出，再重新测试。", tone: .info, actions: [
                    .init(title: "重新测试", primary: true) { [weak self] in self?.armTest(profile: profile) },
                    .init(title: "关闭") { [weak self] in self?.returnToSetup() }
                ], duration: nil)
            })
        ], duration: nil)
        refreshNativeUI()
    }
    private func returnToSetup() {
        guard testFromSetup else { return }
        testFromSetup = false
        if !UserDefaults.standard.bool(forKey: "onboardingCompleted") { showChecklist(nil) }
    }
    private func saveOptions(_ options: RemoteDeliveryOptions, for profile: RemoteClientProfile) {
        remotePreferences.save(options, for: profile)
        setStatus(profile.name + " 设置已保存，后续投送使用新配置")
    }

    // MARK: Windows

    private var isManagingUI: Bool {
        NSApp.isActive && (settingsUI?.window.isVisible == true || recoveryUI?.window.isVisible == true || setupUI?.window.isVisible == true)
    }
    private func mayOpenManagementUI() -> Bool {
        guard captures.current == nil, !localKeyPending else { composer.focus(); return false }
        guard !deliveries.isInjecting else { setStatus("正在投送，请稍后打开设置或最近文字"); return false }
        return true
    }
    private var clipboardConfirmed: Bool {
        UserDefaults.standard.object(forKey: "setup.clipboardReady") as? Bool ?? UserDefaults.standard.bool(forKey: "onboardingCompleted")
    }
    private func setupStatus() -> SetupStatus {
        var status = SetupStatus()
        status.doubao = source.isAvailable
        status.accessibility = AXIsProcessTrusted(); status.inputMonitoring = CGPreflightListenEventAccess(); status.listening = tap != nil
        let running = NSWorkspace.shared.runningApplications.map(\.bundleIdentifier)
        status.running = RemoteClientProfile.allCases.filter { profile in running.contains { profile.matches(bundleID: $0) } }
        status.clipboardReady = clipboardConfirmed
        let passed = RemoteClientProfile.allCases.filter { remotePreferences.calibration(for: $0) == .passed }
        status.calibration = passed.isEmpty ? nil : passed.map(\.name).joined(separator: "、") + " 已通过测试"
        return status
    }
    private func settingsState() -> SettingsState {
        SettingsState(profile: settingsProfile, options: remotePreferences.options(for: settingsProfile), paused: !enabled, status: message,
                      accessibility: AXIsProcessTrusted(), inputMonitoring: CGPreflightListenEventAccess(), listening: tap != nil,
                      restoresClipboard: remotePreferences.restoresClipboard, longPressRepaste: remotePreferences.longPressRepaste,
                      calibration: remotePreferences.calibration(for: settingsProfile))
    }
    private func refreshNativeUI() {
        settingsUI?.update(settingsState())
        if let setupUI, setupUI.window.isVisible { setupUI.update(setupStatus()) }
        // Hidden windows must discard expired text too, without disrupting selection.
        recoveryUI?.reload(recovery.records, now: scheduler.now)
    }
    @objc private func showSettings(_ sender: Any?) {
        guard mayOpenManagementUI() else { return }
        if let front = NSWorkspace.shared.frontmostApplication, let profile = rememberRemote(front) { settingsProfile = profile }
        if settingsUI == nil { _ = makeSettingsWindow() }
        setupUI?.window.orderOut(nil); recoveryUI?.window.orderOut(nil); hud.hide(); testArmed = false
        refreshNativeUI(); settingsUI?.present()
    }
    // Construction remains separate from presentation for isolated AppKit previews.
    func makeSettingsWindow() -> NSWindow {
        let ui = SettingsController(); settingsUI = ui
        ui.onProfile = { [weak self] profile in
            guard let self else { return }; self.settingsProfile = profile
            self.setStatus("正在设置 " + profile.name + "；听写时跟随前台客户端")
        }
        ui.onOptions = { [weak self] options in
            guard let self else { return }
            self.saveOptions(options, for: self.settingsProfile)
        }
        ui.onPermission = { [weak self] ax in ax ? self?.accessibility(nil) : self?.inputMonitoring(nil) }
        ui.onRecheck = { [weak self] in self?.refreshPermissions(); self?.refreshNativeUI() }
        ui.onRelaunch = { [weak self] in self?.relaunch() }
        ui.onTest = { [weak self] in guard let self else { return }; self.testFromSetup = false; self.armTest(profile: self.settingsProfile) }
        ui.onPause = { [weak self] in self?.toggle(nil) }
        ui.onRestoreClipboard = { [weak self] value in self?.remotePreferences.restoresClipboard = value; self?.refreshNativeUI() }
        ui.onLongPress = { [weak self] value in self?.remotePreferences.longPressRepaste = value; self?.refreshNativeUI() }
        ui.onOpenSetup = { [weak self] in self?.showChecklist(nil) }
        ui.onCopyDiagnostics = { [weak self] in self?.copyDiagnostics() }
        ui.onClearRecent = { [weak self] in self?.clearRecovery(nil) }
        refreshNativeUI(); return ui.window
    }
    @objc private func showRecovery(_ sender: Any?) {
        guard mayOpenManagementUI() else { return }
        recovery.prune(now: scheduler.now)
        if recoveryUI == nil {
            let ui = RecoveryController(); recoveryUI = ui
            ui.onRestore = { [weak self] id in self?.restoreRecord(id: id) }
            ui.onClear = { [weak self] in self?.clearRecovery(nil) }
            ui.canRestore = { [weak self] id in
                guard let self else { return false }
                return self.enabled && self.captures.current == nil && !self.deliveries.isInjecting && !self.localKeyPending && !self.deliveries.isSending(id) && self.recovery.records.contains { $0.id == id }
            }
        }
        settingsUI?.window.orderOut(nil); setupUI?.window.orderOut(nil); hud.hide(); testArmed = false
        recoveryUI?.reload(recovery.records, now: scheduler.now); recoveryUI?.present()
    }
    @objc private func showChecklist(_ sender: Any?) {
        guard mayOpenManagementUI() else { return }
        if setupUI == nil {
            let ui = SetupController(); setupUI = ui
            ui.onPermission = { [weak self] ax in ax ? self?.accessibility(nil) : self?.inputMonitoring(nil) }
            ui.onRelaunch = { [weak self] in self?.relaunch() }
            ui.onOpenKeyboard = { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Keyboard-Settings.extension")!) }
            ui.onClipboard = { [weak self] ready in UserDefaults.standard.set(ready, forKey: "setup.clipboardReady"); self?.refreshNativeUI() }
            ui.onTest = { [weak self] in
                guard let self else { return }
                self.testFromSetup = true
                self.armTest(profile: self.setupStatus().running.first ?? self.settingsProfile)
            }
            ui.onStart = { [weak self] in self?.completeSetup() }
            // Closing a fully ready checklist is the same as starting.
            ui.onClose = { [weak self] in
                guard let self, self.setupStatus().ready, !self.testArmed else { return }
                UserDefaults.standard.set(true, forKey: "onboardingCompleted")
            }
        }
        settingsUI?.window.orderOut(nil); recoveryUI?.window.orderOut(nil); hud.hide(); testArmed = false
        setupUI?.update(setupStatus()); setupUI?.present()
    }
    private func completeSetup() {
        UserDefaults.standard.set(true, forKey: "onboardingCompleted")
        setStatus("已就绪：回到远端，右 Ctrl 开始 / 结束听写")
        hud.show("准备就绪。回到远端输入框，轻按右 Ctrl 开始", tone: .success, duration: 4)
    }
    // Requesting first adds this app to the system list, so the user only flips
    // a switch instead of locating the app with the + button.
    @objc private func accessibility(_ sender: Any?) {
        _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }
    @objc private func inputMonitoring(_ sender: Any?) {
        _ = CGRequestListenEventAccess()
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!)
    }
    // Waits for this process to exit first; the single-instance check would
    // otherwise reject the new copy.
    private func relaunch() {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", "while /bin/kill -0 \(getpid()) 2>/dev/null; do /bin/sleep 0.1; done; /usr/bin/open \"$0\"", Bundle.main.bundleURL.path]
        do { try task.run() } catch { setStatus("无法自动重开，请从菜单退出后手动打开"); return }
        NSApp.terminate(nil)
    }
    private func recheckCancelledSource() {
        guard cancelledSourceNeedsCheck, captures.current == nil, cancellationID == nil else { return }
        let since = sourceCheckSince ?? scheduler.now
        source.probe { [weak self] visibility in
            guard let self, self.cancelledSourceNeedsCheck, self.captures.current == nil else { return }
            if visibility == .hidden {
                self.cancelledSourceNeedsCheck = false; self.deliveries.pump()
            } else if self.scheduler.now - since > 8, self.deliveries.pendingCount > 0 {
                self.deliveries.cancelAll(reason: "未确认豆包录音已结束，文字未投送")
                self.hint("待投送文字已保留", "豆包似乎仍在录音。请先结束录音，再从“最近文字”恢复。")
            }
        }
    }
    @discardableResult private func rememberRemote(_ app: NSRunningApplication) -> RemoteClientProfile? {
        guard AXIsProcessTrusted(), let adapter = remotes.first(where: { $0.matches(app) }),
              let target = adapter.capture(app) else { return nil }
        rememberedTargets[adapter.profile] = target
        return adapter.profile
    }
}
