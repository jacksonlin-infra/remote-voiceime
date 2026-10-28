import AppKit
import ApplicationServices

let localFinishMarker: Int64 = 0x55555256
// Marks our own paste keys so they never count as user activity.
let localPasteMarker: Int64 = 0x55555650

// Reserve a solitary right-Control press, but restore its down event before a
// chord so remote clients still receive a complete Control+C/V/click sequence.
// The caller posts replayed events through its tap proxy, after this tap and
// before the current event, preventing recursion and preserving event order.
final class RightControlShortcut {
    static let keyCode: Int64 = 62
    static let rightMask: UInt64 = 0x2000
    static let leftMask: UInt64 = 0x1
    private var tracking = false
    private var heldDown: CGEvent?
    private var origin: pid_t?
    private var mayActivate = false
    private var downAt: UInt64 = 0
    // How long the last activating press was held; a long press re-pastes.
    private(set) var lastHold: TimeInterval = 0
    static let longPress: TimeInterval = 0.6

    func cancelActivation() { mayActivate = false }
    func reset() { tracking = false; heldDown = nil; origin = nil; mayActivate = false }

    func handle(type: CGEventType, event: CGEvent, eligible: Bool, context: pid_t?,
                replay: (CGEvent) -> Void) -> (consume: Bool, activate: Bool) {
        if type == .flagsChanged && event.getIntegerValueField(.keyboardEventKeycode) == Self.keyCode {
            let down = event.flags.rawValue & Self.rightMask != 0
            if down {
                if !tracking {
                    tracking = true
                    let other: CGEventFlags = [.maskCommand, .maskAlternate, .maskShift, .maskSecondaryFn]
                    if eligible, context != nil, event.flags.intersection(other).isEmpty,
                       event.flags.rawValue & Self.leftMask == 0 {
                        heldDown = event.copy()
                        downAt = event.timestamp
                        origin = context
                        mayActivate = true
                    }
                }
                return (heldDown != nil, false)
            }
            let consume = heldDown != nil
            let activate = consume && mayActivate && eligible && origin == context
            lastHold = activate && event.timestamp >= downAt ? TimeInterval(event.timestamp - downAt) / 1_000_000_000 : 0
            reset()
            return (consume, activate)
        }
        if let down = heldDown {
            switch type {
            case .keyDown, .keyUp, .flagsChanged,
                 .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp,
                 .otherMouseDown, .otherMouseUp, .leftMouseDragged,
                 .rightMouseDragged, .otherMouseDragged, .scrollWheel:
                heldDown = nil
                mayActivate = false
                replay(down)
            default:
                // Plain pointer motion should not expose the reserved modifier.
                var flags = event.flags.rawValue & ~Self.rightMask
                if flags & Self.leftMask == 0 { flags &= ~CGEventFlags.maskControl.rawValue }
                event.flags = CGEventFlags(rawValue: flags)
            }
        }
        return (false, false)
    }
}

func pasteEvents(control: Bool) -> [CGEvent] {
    let key: CGKeyCode = control ? 59 : 55
    let source = CGEventSource(stateID: .combinedSessionState)
    guard let modDown = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true),
          let vDown = CGEvent(keyboardEventSource: source, virtualKey: 9, keyDown: true),
          let vUp = CGEvent(keyboardEventSource: source, virtualKey: 9, keyDown: false),
          let modUp = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false) else { return [] }
    let flags = (control ? CGEventFlags.maskControl : CGEventFlags.maskCommand).union(CGEventFlags(rawValue: control ? 0x1 : 0x8))
    modDown.flags = flags; vDown.flags = flags; vUp.flags = flags; modUp.flags = []
    for event in [modDown, vDown, vUp, modUp] { event.setIntegerValueField(.eventSourceUserData, value: localPasteMarker) }
    return [modDown, vDown, vUp, modUp]
}

final class NativePasteEmitter: PasteEmitter {
    let scheduler: WorkScheduler
    init(scheduler: WorkScheduler) { self.scheduler = scheduler }
    func prepare(control: Bool) -> PreparedPaste? {
        let events = pasteEvents(control: control)
        return events.count == 4 ? NativePasteSequence(events: events, scheduler: scheduler) : nil
    }
}
final class NativePasteSequence: PreparedPaste {
    private let events: [CGEvent]
    private let scheduler: WorkScheduler
    private let post: (CGEvent) -> Void
    private var next = 0
    private var attempted = false
    private var completed = false
    private var completion: ((PasteEmission) -> Void)?
    init(events: [CGEvent], scheduler: WorkScheduler, post: @escaping (CGEvent) -> Void = { $0.post(tap: .cghidEventTap) }) {
        self.events = events; self.scheduler = scheduler; self.post = post
    }
    func send(whileValid: @escaping () -> Bool, completion: @escaping (PasteEmission) -> Void) {
        guard self.completion == nil, !completed else { return }
        self.completion = completion
        step(whileValid)
    }
    private func step(_ valid: @escaping () -> Bool) {
        guard !completed else { return }
        if next <= 1 && !valid() { cancel(); return }
        post(events[next])
        if next == 1 { attempted = true }
        next += 1
        if next == 4 { complete(); return }
        scheduler.after(0.03) { [weak self] in self?.step(valid) }
    }
    func cancel() {
        guard !completed else { return }
        if next == 2 { post(events[2]) }
        if next > 0 && next < 4 { post(events[3]) }
        complete()
    }
    private func complete() {
        completed = true
        let callback = completion
        completion = nil
        callback?(attempted ? .attempted : .notSent)
    }
}

// The tap filters every key and pointer event in the session. Running it on its
// own thread keeps input responsive while the main thread waits on AX calls,
// and keeps macOS from disabling the tap for a slow callback.
final class EventTapThread {
    private final class Loop { var value: CFRunLoop? }
    private let loop = Loop()
    private(set) var port: CFMachPort?
    func start(mask: CGEventMask, callback: @escaping CGEventTapCallBack, userInfo: UnsafeMutableRawPointer) -> Bool {
        guard port == nil else { return true }
        guard let created = CGEvent.tapCreate(tap: .cghidEventTap, place: .headInsertEventTap, options: .defaultTap,
                                              eventsOfInterest: mask, callback: callback, userInfo: userInfo) else { return false }
        port = created
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, created, 0)
        let ready = DispatchSemaphore(value: 0), loop = self.loop
        let thread = Thread {
            loop.value = CFRunLoopGetCurrent()
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .commonModes)
            CGEvent.tapEnable(tap: created, enable: true)
            ready.signal()
            CFRunLoopRun()
        }
        thread.name = "voice.event-tap"; thread.qualityOfService = .userInteractive
        thread.start(); ready.wait()
        return true
    }
    func stop() {
        if let port { CGEvent.tapEnable(tap: port, enable: false); CFMachPortInvalidate(port) }
        if let value = loop.value { CFRunLoopStop(value) }
        port = nil
    }
}
