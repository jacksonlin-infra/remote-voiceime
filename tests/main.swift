import AppKit

var passed = 0
func check(_ condition: @autoclosure () -> Bool, _ name: String) {
    guard condition() else { fputs("FAIL: \(name)\n", stderr); exit(1) }
    passed += 1
    print("PASS: \(name)")
}

if CommandLine.arguments.contains("--native-clipboard") {
    // A unique named pasteboard; never reads, clears or writes NSPasteboard.general.
    let board = NSPasteboard(name: .init("local.voiceinput.tests." + UUID().uuidString))
    let native = NativeClipboard(board: board)
    let first = NSPasteboardItem()
    first.setString("原文 original", forType: .string)
    let rich = Data("{\\rtf1\\ansi original}".utf8)
    first.setData(rich, forType: .rtf)
    let second = NSPasteboardItem()
    let custom = NSPasteboard.PasteboardType("local.voiceinput.fixture")
    second.setData(Data([0, 1, 2, 255]), forType: custom)
    board.clearContents()
    check(board.writeObjects([first, second]), "named pasteboard accepts multiple items")
    guard let snapshot = native.capture() else { fatalError("Could not back up isolated pasteboard") }
    check(snapshot.items.count == 2 && snapshot.items[0][NSPasteboard.PasteboardType.rtf.rawValue] == rich, "native backup retains rich text and multiple items")
    let payload = "  中文 e\u{301} 👩🏽‍💻\n"
    let write = native.write(payload)
    check(write.succeeded && board.string(forType: .string) == payload, "native pasteboard preserves exact payload")
    let lease = ClipboardLease(snapshot: snapshot, generation: write.generation)
    check(lease.release(on: native) == .restored && native.capture()?.items == snapshot.items, "native lease restores all original formats")
    let newerWrite = native.write("dictation")
    let newerLease = ClipboardLease(snapshot: snapshot, generation: newerWrite.generation)
    board.clearContents(); board.setString("new user copy", forType: .string)
    check(newerLease.release(on: native) == .changedByUser && board.string(forType: .string) == "new user copy", "native changeCount prevents overwriting newer copy")
    board.clearContents(); board.setData(Data(repeating: 0, count: 16_000_001), forType: custom)
    check(native.capture() == nil && board.data(forType: custom)?.count == 16_000_001, "oversized native backup fails without destroying original")
    board.clearContents(); board.releaseGlobally()
    print("All \(passed) native clipboard checks passed using a disposable named pasteboard.")
    exit(0)
}

final class MemoryDefaults: UserDefaults {
    private var values: [String: Any] = [:]
    override func object(forKey key: String) -> Any? { values[key] }
    override func bool(forKey key: String) -> Bool { values[key] as? Bool ?? false }
    override func set(_ value: Any?, forKey key: String) { values[key] = value }
}

do {
    let defaults = MemoryDefaults(), preferences = RemotePreferences(defaults: MemoryDefaults())
    check(RemoteClientProfile.uu.matches(bundleID: "com.netease.uuremote"), "UU exact identity remains supported")
    check(RemoteClientProfile.windowsApp.matches(bundleID: "com.microsoft.rdc.macos"), "installed Windows App identity is recognized")
    check(!RemoteClientProfile.windowsApp.matches(bundleID: "com.microsoft.rdc.macos.fake") && !RemoteClientProfile.uu.matches(bundleID: nil), "unrecognized clients do not capture shortcuts")
    check(!preferences.options(for: .uu).control && preferences.options(for: .uu).syncDelay == 0.5, "UU clean defaults use command-V with the standard sync wait")
    check(!preferences.options(for: .windowsApp).control && preferences.options(for: .windowsApp).syncDelay == 1.2, "Windows App defaults to mapped command-V and compatibility delay")
    check(!preferences.restoresClipboard, "transcript stays on the clipboard by default so a late sync can be pasted manually")
    defaults.set(true, forKey: "pasteModeControl"); defaults.set(true, forKey: "compatibilityDelay")
    let migrated = RemotePreferences(defaults: defaults)
    check(migrated.options(for: .uu).control && migrated.options(for: .uu).speed == .compatible, "existing UU preferences survive upgrade")
    check(!migrated.options(for: .windowsApp).control, "UU legacy paste mode cannot leak into Windows App")
    let legacyFast = MemoryDefaults(); legacyFast.set(false, forKey: "remote.uu.compatibility"); legacyFast.set(false, forKey: "remote.windowsApp.compatibility")
    check(RemotePreferences(defaults: legacyFast).options(for: .uu).speed == .standard && RemotePreferences(defaults: legacyFast).options(for: .windowsApp).speed == .standard,
          "legacy 180 ms value saved alongside a paste-key change upgrades to the standard wait")
    migrated.save(RemoteDeliveryOptions(control: false, speed: .fast), for: .uu)
    migrated.save(RemoteDeliveryOptions(control: true, speed: .standard), for: .windowsApp)
    check(!migrated.options(for: .uu).control && migrated.options(for: .uu).speed == .fast && migrated.options(for: .uu).syncDelay == 0.18, "explicit new choice overrides legacy settings")
    check(migrated.options(for: .windowsApp).control && migrated.options(for: .windowsApp).speed == .standard, "each client saves its own paste options")
    migrated.restoresClipboard = true
    check(migrated.restoresClipboard, "clipboard restoration remains an explicit option")
}

final class TestClock: WorkScheduler {
    var now: TimeInterval = 0
    private var counter = 0
    private var work: [(time: TimeInterval, order: Int, run: () -> Void)] = []
    func after(_ interval: TimeInterval, _ task: @escaping () -> Void) {
        counter += 1; work.append((now + interval, counter, task))
    }
    func advance(_ seconds: TimeInterval) {
        let end = now + seconds
        while let index = work.indices.min(by: { (work[$0].time, work[$0].order) < (work[$1].time, work[$1].order) }), work[index].time <= end {
            let task = work.remove(at: index); now = task.time; task.run()
        }
        now = end
    }
}
final class TestBoard: ClipboardAccess {
    var generation = 1
    var items = [["public.utf8-plain-text": Data("original".utf8), "public.rtf": Data([1, 2, 3])]]
    var captureFails = false
    var changeDuringCapture = false
    var writeFails = false
    var restoreFails = false
    var writes: [String] = []
    var restores = 0
    func capture() -> ClipboardSnapshot? {
        if captureFails { return nil }
        let copy = ClipboardSnapshot(generation: generation, items: items)
        if changeDuringCapture { userCopy("new during capture") }
        return copy
    }
    func write(_ text: String) -> ClipboardWrite {
        generation += 1; writes.append(text)
        items = [["public.utf8-plain-text": Data(text.utf8)]]
        return ClipboardWrite(generation: generation, succeeded: !writeFails)
    }
    func restore(_ snapshot: ClipboardSnapshot) -> Bool {
        restores += 1; generation += 1; items = snapshot.items
        return !restoreFails
    }
    func userCopy(_ text: String) { generation += 1; items = [["public.utf8-plain-text": Data(text.utf8)]] }
    func holds(_ value: String) -> Bool { text == value }
    var text: String { String(data: items.first?["public.utf8-plain-text"] ?? Data(), encoding: .utf8) ?? "" }
}
final class TestDestination: RemoteDestination {
    let label = "test remote"
    let profile = RemoteClientProfile.uu
    var valid = true
    var front = true
    var activationSucceeds = true
    var activations = 0
    func isValid() -> Bool { valid }
    func isFrontmost() -> Bool { front }
    func activate() -> Bool { activations += 1; if activationSucceeds { front = true }; return activationSucceeds }
}
final class TestPaste: PreparedPaste {
    var autoComplete = true
    var completed: ((PasteEmission) -> Void)?
    var wasSent = false
    var cancelled = false
    func send(whileValid: @escaping () -> Bool, completion: @escaping (PasteEmission) -> Void) {
        completed = completion
        if autoComplete { wasSent = whileValid(); completion(wasSent ? .attempted : .notSent) }
    }
    func cancel() { cancelled = true; completed?(wasSent ? .attempted : .notSent) }
}
final class TestEmitter: PasteEmitter {
    var fails = false
    var autoComplete = true
    var sequences: [TestPaste] = []
    func prepare(control: Bool) -> PreparedPaste? {
        guard !fails else { return nil }
        let paste = TestPaste(); paste.autoComplete = autoComplete; sequences.append(paste); return paste
    }
}
final class Rig {
    let clock = TestClock()
    let board = TestBoard()
    let emitter = TestEmitter()
    let target = TestDestination()
    let queue: DeliveryQueue
    var results: [(UUID, DeliveryOutcome)] = []
    var captureActive = false
    init() {
        queue = DeliveryQueue(board: board, emitter: emitter, scheduler: clock)
        queue.canDrain = { [unowned self] in !self.captureActive }
        queue.mayActivate = { _ in true }
        queue.onOutcome = { [unowned self] job, result in self.results.append((job.id, result)) }
    }
    func job(_ text: String = "你好，远端 👩🏽‍💻", restore: Bool = true) -> DeliveryJob {
        DeliveryJob(id: UUID(), text: text, destination: target, control: false, syncDelay: 0.18, restoreClipboard: restore)
    }
    func submit(_ job: DeliveryJob) { check(queue.enqueue(job), "enqueue accepted"); queue.pump() }
}

// The original 16 regression checks, now using the extracted production types.
do {
    let rig = Rig(), diagnostics = DeliveryDiagnostics()
    let job = rig.job("PRIVATE-TRANSCRIPT-MUST-NOT-APPEAR")
    var stages: [DeliveryDiagnostics.Stage] = []
    diagnostics.begin(id: job.id, origin: .calibration, profile: .uu, control: job.control, delay: job.syncDelay, inputSource: "test.input")
    rig.queue.onTrace = { id, stage, now in stages.append(stage); diagnostics.record(id: id, stage: stage, now: now) }
    rig.submit(job)
    check(stages == [.queued, .clipboardWritten, .targetActivated], "diagnostics distinguish clipboard preparation from emitted paste")
    rig.clock.advance(0.2)
    check(stages == [.queued, .clipboardWritten, .targetActivated, .pasteAttempted], "diagnostics record paste only after input backend completion")
    check(!diagnostics.report.contains(job.text) && diagnostics.report.contains("origin=calibration"), "diagnostic export identifies test path without transcript")
    check(rig.emitter.sequences.count == 1 && rig.results.count == 1, "diagnostic observer does not duplicate delivery")
    for _ in 0..<100 { diagnostics.record(id: job.id, stage: .queued, now: 1) }
    check(diagnostics.report.components(separatedBy: "\n").count == 82, "diagnostics retain at most 80 metadata events")
}
do {
    let rig = Rig(); var stages: [DeliveryDiagnostics.Stage] = []
    rig.queue.onTrace = { _, stage, _ in stages.append(stage) }
    rig.submit(rig.job()); rig.board.userCopy("intervening copy"); rig.clock.advance(0.2)
    check(stages.last == .failed && !stages.contains(.pasteAttempted), "diagnostics do not call an aborted paste emitted")
}
// Exercise the exact event router used by the installed tap. Replays go into
// an array, never into the system event stream.
func shortcutEvent(_ type: CGEventType, key: Int64 = 62, flags: CGEventFlags = []) -> CGEvent {
    let event = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(key), keyDown: type != .keyUp)!
    event.type = type; event.flags = flags
    return event
}
let rightControl = CGEventFlags.maskControl.union(CGEventFlags(rawValue: 0x2000))
func route(_ shortcut: RightControlShortcut, _ type: CGEventType = .flagsChanged,
           key: Int64 = 62, flags: CGEventFlags = [], eligible: Bool = true, context: pid_t? = 10,
           replay: (CGEvent) -> Void = { _ in fatalError("Unexpected replay") }) -> (consume: Bool, activate: Bool) {
    shortcut.handle(type: type, event: shortcutEvent(type, key: key, flags: flags), eligible: eligible, context: context, replay: replay)
}
do {
    let shortcut = RightControlShortcut()
    let down = route(shortcut, flags: rightControl)
    check(down.consume && !down.activate, "right Ctrl down is reserved without starting dictation")
    check(route(shortcut, flags: rightControl).consume, "held right Ctrl stays reserved")
    let up = route(shortcut)
    check(up.consume && up.activate, "solitary right Ctrl activates exactly on release")
    check(!route(shortcut).activate, "duplicate right Ctrl release cannot activate again")
    check(!route(shortcut, key: 61, flags: .maskAlternate).consume, "right Alt no longer triggers or gets consumed")
    check(!route(shortcut, key: 59, flags: .maskControl).consume, "left Ctrl is never the voice trigger")
}
do {
    let shortcut = RightControlShortcut()
    check(!route(shortcut, flags: rightControl, eligible: false).consume, "other apps keep right Ctrl down")
    check(!route(shortcut, flags: rightControl).consume, "press begun outside target cannot be captured on repeat")
    let up = route(shortcut)
    check(!up.consume && !up.activate, "press begun outside target stays untouched after focus changes")
    _ = route(shortcut, flags: rightControl)
    let changed = route(shortcut, context: 20)
    check(changed.consume && !changed.activate, "moving between supported apps cancels activation but consumes reserved release")
    _ = route(shortcut, flags: rightControl)
    shortcut.cancelActivation()
    check(!route(shortcut).activate, "focus leaving and returning before release still cancels activation")
    _ = route(shortcut, flags: rightControl)
    check(!route(shortcut, eligible: false).activate, "pause or loss of eligibility prevents activation")
    _ = route(shortcut, flags: rightControl)
    shortcut.reset()
    check(!route(shortcut).activate, "tap interruption clears the pending gesture")
    _ = route(shortcut, flags: rightControl)
    check(route(shortcut).activate, "new press works after tap recovery")
}
for modifier: CGEventFlags in [.maskShift, .maskCommand, .maskAlternate, .maskSecondaryFn, CGEventFlags(rawValue: 0x1)] {
    let shortcut = RightControlShortcut()
    check(!route(shortcut, flags: rightControl.union(modifier)).consume, "preheld modifier prevents right Ctrl capture: \(modifier.rawValue)")
    let up = route(shortcut, flags: modifier)
    check(!up.consume && !up.activate, "preheld modifier chord releases without dictation: \(modifier.rawValue)")
}
for type: CGEventType in [.keyDown, .keyUp, .flagsChanged, .leftMouseDown, .rightMouseDown, .otherMouseDown, .scrollWheel, .leftMouseDragged] {
    let shortcut = RightControlShortcut()
    var replayed: [CGEvent] = []
    _ = route(shortcut, flags: rightControl)
    let current = shortcutEvent(type, key: type == .flagsChanged ? 59 : 8, flags: rightControl)
    let chord = shortcut.handle(type: type, event: current, eligible: true, context: 10, replay: { replayed.append($0) })
    check(!chord.consume && !chord.activate && current.flags == rightControl,
          "Ctrl combination keeps current event and modifier: \(type.rawValue)")
    check(replayed.count == 1 && replayed[0].type == .flagsChanged && replayed[0].getIntegerValueField(.keyboardEventKeycode) == 62 && replayed[0].flags == rightControl,
          "reserved Ctrl down is replayed before combination: \(type.rawValue)")
    _ = route(shortcut, .keyDown, key: 9, flags: rightControl, replay: { replayed.append($0) })
    check(replayed.count == 1, "holding a Ctrl chord never replays its down twice: \(type.rawValue)")
    let up = route(shortcut)
    check(!up.consume && !up.activate, "Ctrl combination forwards release without voice activation: \(type.rawValue)")
}
do {
    let shortcut = RightControlShortcut()
    _ = route(shortcut, flags: rightControl)
    let motion = shortcutEvent(.mouseMoved, flags: rightControl)
    _ = shortcut.handle(type: .mouseMoved, event: motion, eligible: false, context: nil, replay: { _ in fatalError("Pointer motion is not a chord") })
    check(!motion.flags.contains(.maskControl), "pointer motion does not leak the reserved Ctrl modifier")
    check(route(shortcut).activate, "pointer motion alone does not cancel solitary Ctrl")
    _ = route(shortcut, flags: rightControl)
    _ = route(shortcut, key: 59, flags: rightControl.union(CGEventFlags(rawValue: 1)), replay: { _ in })
    let up = route(shortcut, flags: CGEventFlags.maskControl.union(CGEventFlags(rawValue: 1)))
    check(!up.consume && !up.activate, "right Ctrl release with left Ctrl still held remains a normal chord")
}
check(!SettleRule.ready(text: "hello", marked: true, stopped: 3, stable: 3, visibility: .hidden), "marked text never delivered")
check(!SettleRule.ready(text: "hello", marked: false, stopped: 3, stable: 3, visibility: .visible), "active dictation never delivered")
check(!SettleRule.ready(text: "hello", marked: false, stopped: 0.05, stable: 3, visibility: .hidden), "wait for final recognition")
check(!SettleRule.ready(text: "  \n", marked: false, stopped: 3, stable: 3, visibility: .hidden), "empty result never pasted")
check(SettleRule.ready(text: "hello", marked: false, stopped: 2, stable: 2, visibility: .hidden), "completed text can deliver")
check(pasteEvents(control: false).count == 4 && pasteEvents(control: true).count == 4, "both paste modes have full down/up sequence")
check(SettleRule.ready(text: "final", marked: false, stopped: 0.25, stable: 0.25, visibility: .hidden), "low latency final text path")
check(!SettleRule.ready(text: "final", marked: false, stopped: 2, stable: 0.05, visibility: .hidden), "unstable final text waits")

check(!SettleRule.ready(text: "hello", marked: false, stopped: 3, stable: 3, visibility: .unknown), "AX failure is not interpreted as recording stopped")
let captures = CaptureSessions()
let first = captures.begin(now: 0)
captures.update(first) { $0.phase = .listening; $0.observe(.visible, now: 0.1); $0.finish(now: 1) }
check(captures.current?.visibility == .unknown, "stop invalidates pre-stop probe")
captures.update(first) { $0.observe(.hidden, now: 0.9) }
check(!captures.current!.ready(text: "final", marked: false, now: 1.3), "late pre-stop probe cannot finalize new stop")
captures.update(first) { $0.observe(.hidden, now: 1.1) }
check(captures.current!.ready(text: "final", marked: false, now: 1.3), "fresh hidden probe after stop permits finalization")
check(!captures.current!.ready(text: "final", marked: false, now: 3), "stale hidden probe cannot release text")
captures.end(first)
let second = captures.begin(now: 4)
check(!captures.update(first) { $0.phase = .review }, "old source callback cannot change new capture")
check(!captures.end(first) && captures.isCurrent(second), "old cancellation cannot clear new capture")
captures.update(second) { $0.phase = .listening; $0.observe(.hidden, now: 4.1) }
check(captures.current?.phase == .listening, "initial hidden panel is not a completion signal")
captures.update(second) { $0.observe(.visible, now: 4.2); $0.observe(.unknown, now: 4.3) }
check(captures.current?.phase == .listening, "unknown after visible does not auto-finish")
captures.update(second) { $0.observe(.hidden, now: 4.4) }
check(captures.current?.phase == .settling, "visible then hidden auto-finishes")
check(TextPolicy.needsReview("line 1\nline 2") && TextPolicy.needsReview("\u{1b}[A"), "multiline and control text require review")
check(!TextPolicy.needsReview("中文 English 👩🏽‍💻 e\u{301}"), "ordinary Unicode text needs no extra action")
check(!TextPolicy.sizeAllowed(String(repeating: "字", count: 30_000)), "large payload is bounded by bytes without truncation")
let cancellation = SourceCancellation(); cancellation.cancel()
check(cancellation.isCancelled, "source work observes cancellation")

do {
    let rig = Rig(), original = TestBoard().items
    let payload = "  缩进 e\u{301} 👩🏽‍💻\n"
    rig.submit(rig.job(payload))
    check(rig.queue.isInjecting && rig.results.isEmpty, "clipboard sync blocks new keyboard injection")
    rig.clock.advance(0.2)
    check(rig.queue.isInjecting && rig.results.count == 1, "paste keys get a short quiet period before focus may move")
    rig.clock.advance(0.2)
    check(!rig.queue.isInjecting && rig.results.count == 1, "new capture allowed while clipboard lease remains")
    check(rig.board.text == payload, "payload preserves boundary whitespace and Unicode exactly")
    if case .unconfirmed = rig.results[0].1 { check(true, "emitted paste remains unconfirmed") } else { check(false, "expected unconfirmed") }
    rig.captureActive = true
    let next = rig.job("second")
    check(rig.queue.enqueue(next), "second final text can queue during clipboard lease")
    rig.clock.advance(3)
    check(rig.board.items == original && rig.board.writes.count == 1, "lease restores all original formats without disrupting new capture")
    check(rig.queue.pendingCount == 1, "next delivery waits for active capture")
    rig.captureActive = false; rig.queue.pump(); rig.clock.advance(0.2)
    check(rig.board.writes == [payload, "second"] && rig.results.count == 2, "queued delivery runs once after capture ends")
    rig.clock.advance(3)
    check(rig.board.items == original, "consecutive delivery restores original clipboard, not previous transcript")
}
do {
    let rig = Rig(); rig.submit(rig.job()); rig.clock.advance(0.2)
    rig.board.userCopy("user copied new content"); rig.clock.advance(3)
    check(rig.board.text == "user copied new content" && rig.board.restores == 0, "delayed restore never overwrites newer user copy")
}
do {
    let rig = Rig(); rig.submit(rig.job()); rig.board.userCopy("intervening copy"); rig.clock.advance(0.2)
    check(rig.results.count == 1 && !rig.emitter.sequences[0].wasSent, "clipboard change during sync aborts before paste")
    check(rig.board.text == "intervening copy", "abort also preserves newer clipboard")
}
do {
    let rig = Rig(); var stages: [DeliveryDiagnostics.Stage] = []
    rig.queue.onTrace = { _, stage, _ in stages.append(stage) }
    rig.submit(rig.job()); rig.target.front = false; rig.clock.advance(0.2)
    check(!rig.emitter.sequences[0].wasSent && rig.results.isEmpty, "slow activation waits instead of failing at the sync deadline")
    rig.clock.advance(2)
    check(!rig.emitter.sequences[0].wasSent && rig.board.text == "original", "focus loss cancels paste and restores owned clipboard")
    check(stages.suffix(2) == [.blockedByFocus, .failed], "diagnostics name the focus gate that stopped the paste")
}
do {
    let rig = Rig(); rig.submit(rig.job()); rig.target.valid = false; rig.clock.advance(2)
    check(!rig.emitter.sequences[0].wasSent && rig.results.count == 1, "window change blocks queued paste")
}
do {
    let rig = Rig(); rig.queue.modifiersClear = { false }; rig.submit(rig.job()); rig.clock.advance(2)
    check(!rig.emitter.sequences[0].wasSent && rig.results.count == 1, "held physical modifier blocks paste")
}
do {
    let rig = Rig(); rig.board.captureFails = true; rig.submit(rig.job())
    check(rig.board.writes.isEmpty && rig.results.count == 1, "failed clipboard backup leaves original intact")
}
do {
    let rig = Rig(); rig.board.changeDuringCapture = true; rig.submit(rig.job())
    check(rig.board.writes.isEmpty && rig.board.text == "new during capture", "generation change during capture prevents replacement")
}
do {
    let rig = Rig(); rig.board.writeFails = true; rig.submit(rig.job())
    check(rig.board.text == "original" && rig.board.restores == 1, "failed write rolls back owned clipboard")
}
do {
    let rig = Rig(); rig.emitter.fails = true; rig.submit(rig.job())
    check(rig.board.writes.isEmpty, "event construction failure happens before clipboard mutation")
}
do {
    let rig = Rig(); rig.target.activationSucceeds = false; rig.submit(rig.job())
    check(rig.board.text == "original" && !rig.queue.isInjecting, "failed window activation releases lease")
}
do {
    let rig = Rig(); rig.queue.mayActivate = { _ in false }; rig.submit(rig.job())
    check(rig.target.activations == 0 && rig.board.writes.isEmpty, "queued delivery cannot steal focus from unrelated app")
}
do {
    let rig = Rig(); rig.submit(rig.job("cancelled")); rig.queue.cancelAll(reason: "cancel")
    let fresh = rig.job("new session"); rig.submit(fresh); rig.clock.advance(0.2)
    check(rig.results.count == 2 && rig.results.last?.0 == fresh.id && rig.board.text == "new session", "old delayed sync callback cannot clear new attempt")
    rig.clock.advance(3)
    check(rig.board.text == "original", "cancel and retry maintain clipboard ownership")
}
do {
    let rig = Rig(); rig.emitter.autoComplete = false; rig.submit(rig.job())
    rig.clock.advance(0.2)
    let old = rig.emitter.sequences[0]
    rig.queue.cancelAll(reason: "cancel while posting")
    rig.emitter.autoComplete = true
    let new = rig.job("fresh"); rig.submit(new); rig.clock.advance(0.2)
    old.completed?(.attempted)
    check(rig.results.count == 2 && rig.board.text == "fresh", "late posting callback from cancelled job is ignored")
}
do {
    let rig = Rig(); rig.captureActive = true
    let job = rig.job(); check(rig.queue.enqueue(job), "first pending task accepted")
    check(!rig.queue.enqueue(job), "duplicate delivery ID rejected")
    check(rig.queue.enqueue(rig.job()) && rig.queue.enqueue(rig.job()) && !rig.queue.enqueue(rig.job()), "delivery backlog is bounded")
    rig.queue.suspended = true; rig.captureActive = false; rig.queue.pump()
    check(rig.board.writes.isEmpty, "pause prevents draining")
    rig.queue.cancelAll(reason: "paused"); rig.queue.suspended = false; rig.queue.pump()
    check(rig.results.count == 3 && rig.board.writes.isEmpty, "pause cancellation preserves drafts without later auto-replay")
}
do {
    let rig = Rig(); var restorationFailure = false
    rig.queue.onRestoreFailure = { restorationFailure = true }
    rig.board.restoreFails = true; rig.submit(rig.job()); rig.clock.advance(4)
    check(restorationFailure, "clipboard restoration failure is surfaced")
}
do {
    let rig = Rig(); rig.submit(rig.job()); rig.clock.advance(0.2)
    rig.emitter.sequences[0].completed?(.attempted)
    check(rig.results.count == 1, "duplicate completion callback cannot report or schedule a second paste")
}
do {
    let store = RecoveryStore(), target = TestDestination()
    for number in 0..<7 {
        store.save(RecoveryRecord(id: UUID(), text: "draft \(number)", destination: target, createdAt: Double(number), status: .unconfirmed), now: Double(number))
    }
    check(store.records.count == 5 && store.records[0].text == "draft 6", "recovery retains only five newest entries")
    let record = store.records[0]
    store.update(record.id, status: .failed("retry"), now: 10)
    check(store.records[0].text == "draft 6" && store.records[0].status.label == "未投送", "delivery outcome preserves recovery text")
    store.prune(now: 305)
    check(store.records.count == 1, "expired transcripts leave recovery memory")
    store.prune(now: 306)
    check(store.records.isEmpty, "five minute expiry includes boundary")
    store.save(record, now: 6); store.clear()
    check(store.records.isEmpty, "explicit recovery clear removes text")
}
for control in [false, true] {
    let events = pasteEvents(control: control)
    check(events.map { $0.getIntegerValueField(.keyboardEventKeycode) } == [control ? 59 : 55, 9, 9, control ? 59 : 55], "paste keycodes match selected mode")
    // CGEvent canonicalizes modifier keys as flagsChanged (verified on macOS).
    check(events.map(\.type) == [.flagsChanged, .keyDown, .keyUp, .flagsChanged] && events.last!.flags.isEmpty, "paste includes modifier release without residual flags")
}
do {
    let clock = TestClock(); var posted: [CGEvent] = []; var valid = true; var result: PasteEmission?
    let sequence = NativePasteSequence(events: pasteEvents(control: false), scheduler: clock, post: { posted.append($0) })
    sequence.send(whileValid: { valid }, completion: { result = $0 })
    valid = false; clock.advance(0.2)
    check(posted.count == 2 && posted.allSatisfy { $0.getIntegerValueField(.keyboardEventKeycode) == 55 }, "focus loss between modifier and V releases modifier without typing")
    check(result == .notSent && posted.last!.flags.isEmpty, "interrupted pre-V sequence reports not sent and no held modifier")
}
do {
    let clock = TestClock(); var posted: [CGEvent] = []; var result: PasteEmission?
    let sequence = NativePasteSequence(events: pasteEvents(control: true), scheduler: clock, post: { posted.append($0) })
    sequence.send(whileValid: { true }, completion: { result = $0 })
    clock.advance(0.04); sequence.cancel(); clock.advance(0.3)
    check(posted.count == 4 && posted.last!.flags.isEmpty, "cancellation after V-down releases V and modifier exactly once")
    check(result == .attempted, "cancellation after V-down never claims no paste occurred")
}

// Gate behavior added after field reports of missing or stale remote pastes.
do {
    let rig = Rig(); rig.submit(rig.job()); rig.target.front = false
    rig.clock.advance(0.5); rig.target.front = true
    check(!rig.emitter.sequences[0].wasSent, "returning focus must settle before the paste keys")
    rig.clock.advance(0.2)
    check(rig.emitter.sequences[0].wasSent && rig.board.text == "你好，远端 👩🏽‍💻", "late activation still delivers the transcript")
}
do {
    let rig = Rig(); var held = true
    rig.queue.modifiersClear = { !held }
    rig.submit(rig.job()); rig.clock.advance(0.6)
    check(!rig.emitter.sequences[0].wasSent && rig.results.isEmpty, "held modifier delays the paste instead of failing")
    held = false; rig.clock.advance(0.1)
    check(rig.emitter.sequences[0].wasSent, "paste proceeds after the modifier is released")
}
do {
    let rig = Rig(); var stages: [DeliveryDiagnostics.Stage] = []
    rig.queue.onTrace = { _, stage, _ in stages.append(stage) }
    let job = rig.job("同样的文字"); rig.submit(job)
    rig.board.userCopy("同样的文字"); rig.clock.advance(0.3)
    check(rig.emitter.sequences[0].wasSent && stages.contains(.clipboardRepublished), "a client republishing the same text keeps the delivery")
    rig.clock.advance(3)
    check(rig.board.text == "original", "republished lease still restores the original clipboard")
}
do {
    let rig = Rig(); var stages: [DeliveryDiagnostics.Stage] = []
    rig.queue.onTrace = { _, stage, _ in stages.append(stage) }
    rig.queue.userActedSince = { _ in false }
    rig.submit(rig.job("新的听写")); rig.board.userCopy("远端旧剪贴板"); rig.clock.advance(0.1)
    check(rig.board.text == "新的听写" && stages.contains(.clipboardReclaimed), "client echo without user input is reclaimed once")
    check(!rig.emitter.sequences[0].wasSent, "reclaimed clipboard waits a full sync again")
    rig.clock.advance(0.3)
    check(rig.emitter.sequences[0].wasSent && rig.board.text == "新的听写", "reclaimed delivery pastes the new transcript, not the stale one")
}
do {
    let rig = Rig(); rig.queue.userActedSince = { _ in false }
    rig.submit(rig.job("新的听写")); rig.board.userCopy("回声 1"); rig.clock.advance(0.1)
    rig.board.userCopy("回声 2"); rig.clock.advance(0.1)
    check(!rig.emitter.sequences[0].wasSent && rig.results.count == 1, "a second clipboard rewrite stops instead of fighting the client")
}
do {
    let rig = Rig(); rig.submit(rig.job("保留在剪贴板", restore: false)); rig.clock.advance(4)
    check(rig.board.text == "保留在剪贴板" && rig.board.restores == 0, "without restoration the transcript stays available for a manual remote paste")
    let failed = Rig(); failed.target.front = false
    failed.submit(failed.job("未投送也保留", restore: false)); failed.clock.advance(3)
    check(failed.results.count == 1 && failed.board.text == "未投送也保留", "a stopped paste leaves the transcript on the clipboard")
}
check(SettleRule.noSpeech(text: " ", marked: false, stopped: 3, visibility: .hidden), "stopped recording without text ends the round")
check(!SettleRule.noSpeech(text: " ", marked: false, stopped: 1, visibility: .hidden), "empty text right after stop still waits for recognition")
check(!SettleRule.noSpeech(text: "", marked: false, stopped: 5, visibility: .unknown), "unknown source state is not treated as no speech")
check(!SettleRule.noSpeech(text: "有字", marked: false, stopped: 5, visibility: .hidden), "received text is never discarded as no speech")
do {
    let sessions = CaptureSessions(); var changes = 0
    sessions.onChange = { changes += 1 }
    let id = sessions.begin(now: 0); sessions.update(id) { $0.phase = .listening }; sessions.end(id)
    check(changes == 2, "capture begin and end notify the key tap state")
    let quiet = sessions.begin(now: 0)
    sessions.update(quiet) { $0.phase = .listening; $0.finish(now: 1); $0.observe(.hidden, now: 1.2) }
    check(!sessions.current!.heardNothing(text: "", marked: false, now: 2) && sessions.current!.heardNothing(text: "", marked: false, now: 3.6) == false,
          "no-speech needs a fresh hidden observation")
    sessions.update(quiet) { $0.observe(.hidden, now: 3.6) }
    check(sessions.current!.heardNothing(text: "", marked: false, now: 3.7), "fresh hidden observation after 2.5 s ends a silent round")
}

// Long press and persisted calibration added with the 0.3 interaction redesign.
do {
    let shortcut = RightControlShortcut()
    let down = shortcutEvent(.flagsChanged, flags: rightControl); down.timestamp = 1_000_000_000
    _ = shortcut.handle(type: .flagsChanged, event: down, eligible: true, context: 10, replay: { _ in fatalError("Unexpected replay") })
    let up = shortcutEvent(.flagsChanged); up.timestamp = 1_200_000_000
    check(shortcut.handle(type: .flagsChanged, event: up, eligible: true, context: 10, replay: { _ in }).activate && shortcut.lastHold < RightControlShortcut.longPress, "short right Ctrl tap is not a long press")
    let longDown = shortcutEvent(.flagsChanged, flags: rightControl); longDown.timestamp = 2_000_000_000
    _ = shortcut.handle(type: .flagsChanged, event: longDown, eligible: true, context: 10, replay: { _ in fatalError("Unexpected replay") })
    let longUp = shortcutEvent(.flagsChanged); longUp.timestamp = 2_700_000_000
    check(shortcut.handle(type: .flagsChanged, event: longUp, eligible: true, context: 10, replay: { _ in }).activate && shortcut.lastHold >= RightControlShortcut.longPress, "held right Ctrl reports a long press")
    let chordDown = shortcutEvent(.flagsChanged, flags: rightControl); chordDown.timestamp = 3_000_000_000
    _ = shortcut.handle(type: .flagsChanged, event: chordDown, eligible: true, context: 10, replay: { _ in fatalError("Unexpected replay") })
    _ = route(shortcut, .keyDown, key: 8, flags: rightControl, replay: { _ in })
    let chordUp = shortcutEvent(.flagsChanged); chordUp.timestamp = 4_000_000_000
    check(!shortcut.handle(type: .flagsChanged, event: chordUp, eligible: true, context: 10, replay: { _ in }).activate && shortcut.lastHold == 0, "a held Ctrl chord is never a re-paste")
}
do {
    let preferences = RemotePreferences(defaults: MemoryDefaults())
    check(preferences.longPressRepaste, "long-press re-paste is on by default")
    check(preferences.calibration(for: .uu) == .untested, "clients start untested")
    preferences.saveCalibration(true, for: .uu)
    check(preferences.calibration(for: .uu) == .passed && preferences.calibration(for: .windowsApp) == .untested, "a passed test is kept per client")
    preferences.save(RemoteDeliveryOptions(control: false, speed: .compatible), for: .uu)
    check(preferences.calibration(for: .uu) == .untested, "changing paste key or speed invalidates the stored result")
    preferences.saveCalibration(false, for: .uu)
    check(preferences.calibration(for: .uu) == .failed, "a failed test is remembered for the settings it used")
}
do {
    var status = SetupStatus(doubao: true, accessibility: true, inputMonitoring: true, listening: false, running: [], clipboardReady: true)
    check(!status.ready && status.requiredDone == 3, "granted Input Monitoring without a running key tap still needs a relaunch")
    status.listening = true
    check(status.ready, "all required checklist items make setup ready without a test")
}

// Re-paste and hold behavior from the 0.3.0 field test.
do {
    let rig = Rig(); let first = rig.job("第一条"); rig.submit(first); rig.clock.advance(0.3)
    check(rig.queue.contains(first.id) && !rig.queue.isSending(first.id), "a pasted job holding the clipboard is no longer sending")
    let again = DeliveryJob(id: UUID(), text: first.text, destination: rig.target, control: false, syncDelay: 0.18)
    check(rig.queue.enqueue(again), "re-paste during the hold is accepted as a fresh job")
    rig.clock.advance(3.5)
    check(rig.board.writes == ["第一条", "第一条"] && rig.results.count == 2, "re-paste runs once the hold ends")
}
do {
    let rig = Rig(); rig.submit(rig.job("保留", restore: false)); rig.clock.advance(0.3)
    let next = rig.job("下一条", restore: false); check(rig.queue.enqueue(next), "next job queues behind a kept clipboard")
    rig.clock.advance(1.2)
    check(rig.board.writes == ["保留", "下一条"], "without restoration the hold is short, so the next paste is not delayed 3 s")
}
do {
    let rig = Rig(); rig.queue.mayActivate = { $0.focusTarget }
    var plain = rig.job("不抢焦点"); plain.focusTarget = false; rig.submit(plain)
    var focused = rig.job("明确重投"); focused.focusTarget = true; rig.submit(focused); rig.clock.advance(0.3)
    check(rig.board.writes == ["明确重投"], "only an explicit re-delivery may bring its window forward")
}
do {
    let store = RecoveryStore(), target = TestDestination(), id = UUID()
    store.save(RecoveryRecord(id: id, text: "旧", destination: target, createdAt: 0, status: .unconfirmed), now: 0)
    store.remove(id)
    check(store.records.isEmpty, "a re-delivered record is replaced, not duplicated")
}

do {
    let diagnostics = DeliveryDiagnostics()
    diagnostics.refused("sourceNotStopped", origin: .voice, now: 1)
    check(diagnostics.report.contains("refused=sourceNotStopped origin") == false && diagnostics.report.contains("origin=voice refused=sourceNotStopped"), "refused review submissions are traceable in diagnostics")
    let rig = Rig(); rig.captureActive = true
    let waiting = rig.job("排队"); check(rig.queue.enqueue(waiting), "waiting job queued")
    check(rig.queue.isSending(waiting.id) && !rig.queue.isActive(waiting.id), "a queued job is sending but not yet active")
}
print("All \(passed) checks passed. No keyboard events posted; no general clipboard access.")
