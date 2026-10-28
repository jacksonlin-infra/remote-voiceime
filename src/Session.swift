import Foundation

enum VoiceVisibility { case visible, hidden, unknown }
enum CapturePhase { case starting, listening, settling, review }

struct CaptureState {
    let id: UUID
    var phase: CapturePhase
    var lastChangeAt: TimeInterval
    var stoppedAt: TimeInterval?
    var visibility: VoiceVisibility = .unknown
    var observedAt: TimeInterval?
    var sawVoicePanel = false
    var manual = false

    mutating func observe(_ value: VoiceVisibility, now: TimeInterval) {
        visibility = value
        observedAt = now
        if value == .visible { sawVoicePanel = true }
        if value == .hidden && sawVoicePanel && phase == .listening {
            phase = .settling
            stoppedAt = now
        }
    }

    mutating func finish(now: TimeInterval) {
        guard phase == .listening else { return }
        phase = .settling
        stoppedAt = now
        // A pre-stop observation cannot establish that recognition has finished.
        visibility = .unknown
    }

    func ready(text: String, marked: Bool, now: TimeInterval) -> Bool {
        guard phase == .settling, let stoppedAt, let observedAt,
              observedAt >= stoppedAt, now - observedAt <= 1 else { return false }
        return SettleRule.ready(text: text, marked: marked, stopped: now - stoppedAt,
                                stable: now - lastChangeAt, visibility: visibility)
    }
    func heardNothing(text: String, marked: Bool, now: TimeInterval) -> Bool {
        guard phase == .settling, let stoppedAt, let observedAt,
              observedAt >= stoppedAt, now - observedAt <= 1 else { return false }
        return SettleRule.noSpeech(text: text, marked: marked, stopped: now - stoppedAt, visibility: visibility)
    }
}

// Every asynchronous source callback must carry the capture ID that created it.
final class CaptureSessions {
    private(set) var current: CaptureState?
    var onChange: (() -> Void)?
    @discardableResult func begin(now: TimeInterval, manual: Bool = false) -> UUID {
        let id = UUID()
        current = CaptureState(id: id, phase: manual ? .review : .starting, lastChangeAt: now, manual: manual)
        onChange?()
        return id
    }
    func isCurrent(_ id: UUID) -> Bool { current?.id == id }
    @discardableResult func update(_ id: UUID, _ change: (inout CaptureState) -> Void) -> Bool {
        guard var state = current, state.id == id else { return false }
        change(&state)
        current = state
        return true
    }
    @discardableResult func end(_ id: UUID) -> Bool {
        guard isCurrent(id) else { return false }
        current = nil
        onChange?()
        return true
    }
}

enum SettleRule {
    // Recognition stopped and nothing arrived: end the round instead of waiting.
    static func noSpeech(text: String, marked: Bool, stopped: TimeInterval, visibility: VoiceVisibility) -> Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !marked && visibility == .hidden && stopped >= 2.5
    }
    static func ready(text: String, marked: Bool, stopped: TimeInterval, stable: TimeInterval,
                      visibility: VoiceVisibility) -> Bool {
        !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !marked &&
            visibility == .hidden && stopped >= 0.18 && stable >= 0.18
    }
}

enum TextPolicy {
    static func isEmpty(_ text: String) -> Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    static func needsReview(_ text: String) -> Bool {
        // Foundation's controlCharacters also includes format characters such as
        // emoji's zero-width joiner. Only actual C0/C1 controls and line breaks
        // need this extra review step.
        text.unicodeScalars.contains { $0.value < 0x20 || (0x7f...0x9f).contains($0.value) || $0.value == 0x2028 || $0.value == 0x2029 }
    }
    // Check blankness without trimming or otherwise changing the payload.
    static func sizeAllowed(_ text: String) -> Bool { text.utf8.count <= 64 * 1024 }
}

protocol WorkScheduler: AnyObject {
    var now: TimeInterval { get }
    func after(_ interval: TimeInterval, _ work: @escaping () -> Void)
}

final class MainScheduler: WorkScheduler {
    var now: TimeInterval { ProcessInfo.processInfo.systemUptime }
    func after(_ interval: TimeInterval, _ work: @escaping () -> Void) {
        DispatchQueue.main.asyncAfter(deadline: .now() + interval, execute: work)
    }
}

// Used only by background AX work. App and session state stay on the main thread.
final class SourceCancellation {
    private let lock = NSLock()
    private var cancelled = false
    var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
    func cancel() { lock.lock(); cancelled = true; lock.unlock() }
}
