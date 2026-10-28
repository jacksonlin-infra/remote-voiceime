import Foundation

protocol RemoteDestination: AnyObject {
    var label: String { get }
    var profile: RemoteClientProfile { get }
    var diagnosticIdentity: String { get }
    func isValid() -> Bool
    func isFrontmost() -> Bool
    @discardableResult func activate() -> Bool
}
extension RemoteDestination {
    var diagnosticIdentity: String { "unavailable" }
}

struct ClipboardSnapshot {
    let generation: Int
    let items: [[String: Data]]
}
struct ClipboardWrite {
    let generation: Int
    let succeeded: Bool
}
protocol ClipboardAccess: AnyObject {
    var generation: Int { get }
    func capture() -> ClipboardSnapshot?
    func write(_ text: String) -> ClipboardWrite
    func restore(_ snapshot: ClipboardSnapshot) -> Bool
    // Exact plain-text comparison, used only to recognize a republished payload.
    func holds(_ text: String) -> Bool
}

enum ClipboardRelease { case restored, changedByUser, failed }
final class ClipboardLease {
    let snapshot: ClipboardSnapshot
    private(set) var generation: Int
    private var released = false
    init(snapshot: ClipboardSnapshot, generation: Int) { self.snapshot = snapshot; self.generation = generation }
    // The payload is still ours after a client republished it or we rewrote it.
    func adopt(_ generation: Int) { self.generation = generation }
    func release(on board: ClipboardAccess) -> ClipboardRelease {
        guard !released else { return .changedByUser }
        released = true
        guard board.generation == generation else { return .changedByUser }
        return board.restore(snapshot) ? .restored : .failed
    }
}

enum PasteEmission { case notSent, attempted }
protocol PreparedPaste: AnyObject {
    func send(whileValid: @escaping () -> Bool, completion: @escaping (PasteEmission) -> Void)
    func cancel()
}
protocol PasteEmitter: AnyObject {
    func prepare(control: Bool) -> PreparedPaste?
}

struct DeliveryJob {
    let id: UUID
    let text: String
    let destination: RemoteDestination
    let control: Bool
    let syncDelay: TimeInterval
    // When false the transcript stays on the clipboard, so a late remote sync
    // can still be completed with a manual paste on the remote side.
    var restoreClipboard = true
    // An explicit re-delivery may bring its window forward from another app.
    var focusTarget = false
}

enum DeliveryOutcome: Equatable {
    case failed(String)
    // This is deliberately not named "success": clipboard paste has no remote ACK.
    case unconfirmed(milliseconds: Int)
}

final class DeliveryQueue {
    private enum Phase { case synchronizing, posting, holding }
    private final class Attempt {
        let job: DeliveryJob
        let lease: ClipboardLease
        let sequence: PreparedPaste
        let began: TimeInterval
        var syncFrom: TimeInterval
        var frontSince: TimeInterval?
        var reclaimed = false
        var attemptedAt: TimeInterval?
        var phase = Phase.synchronizing
        init(job: DeliveryJob, lease: ClipboardLease, sequence: PreparedPaste, began: TimeInterval) {
            self.job = job; self.lease = lease; self.sequence = sequence; self.began = began; syncFrom = began
        }
    }
    // Activation and modifier release are asynchronous. The gate waits for them
    // instead of failing on a single sample taken exactly at the sync deadline.
    static let gateInterval: TimeInterval = 0.04
    static let gateTimeout: TimeInterval = 1.5
    static let frontDwell: TimeInterval = 0.1
    // Do not take focus from the client while it still processes the paste keys.
    static let quietAfterPaste: TimeInterval = 0.15
    static let backlog = 3
    private let board: ClipboardAccess
    private let emitter: PasteEmitter
    private let scheduler: WorkScheduler
    private var active: Attempt?
    private var pending: [DeliveryJob] = []
    private var cancelling = false
    var suspended = false
    var canDrain: () -> Bool = { true }
    var mayActivate: (DeliveryJob) -> Bool = { $0.destination.isFrontmost() }
    var modifiersClear: () -> Bool = { true }
    // Whether the user typed or clicked after a time. Without that, a clipboard
    // change during sync came from a client echo, not from a user copy.
    var userActedSince: (TimeInterval) -> Bool = { _ in true }
    var onOutcome: (DeliveryJob, DeliveryOutcome) -> Void = { _, _ in }
    var onRestoreFailure: () -> Void = {}
    var onChange: () -> Void = {}
    var onTrace: (UUID, DeliveryDiagnostics.Stage, TimeInterval) -> Void = { _, _, _ in }

    init(board: ClipboardAccess, emitter: PasteEmitter, scheduler: WorkScheduler) {
        self.board = board; self.emitter = emitter; self.scheduler = scheduler
    }
    var isInjecting: Bool {
        guard let active else { return false }
        if active.phase != .holding { return true }
        return active.attemptedAt.map { scheduler.now - $0 < Self.quietAfterPaste } ?? false
    }
    var pendingCount: Int { pending.count }
    func contains(_ id: UUID) -> Bool { active?.job.id == id || pending.contains { $0.id == id } }
    func isActive(_ id: UUID) -> Bool { active?.job.id == id }
    // Queued or still before its paste keys; a job only holding the clipboard
    // afterwards may be pasted again.
    func isSending(_ id: UUID) -> Bool { pending.contains { $0.id == id } || (active?.job.id == id && active?.phase != .holding) }

    @discardableResult func enqueue(_ job: DeliveryJob) -> Bool {
        guard !suspended, !cancelling, pending.count < Self.backlog, !contains(job.id) else { return false }
        pending.append(job)
        onTrace(job.id, .queued, scheduler.now)
        onChange()
        return true
    }

    // Capture may run during a clipboard lease. Only the next delivery must wait.
    func pump() {
        guard !suspended, !cancelling, active == nil, !pending.isEmpty, canDrain() else { return }
        let job = pending.removeFirst()
        guard job.destination.isValid(), mayActivate(job) else {
            fail(job, "原窗口或前台应用已变化，请回到原目标后重试"); return
        }
        guard let sequence = emitter.prepare(control: job.control) else {
            fail(job, "无法创建完整粘贴按键"); return
        }
        guard let snapshot = board.capture(), snapshot.generation == board.generation else {
            fail(job, "剪贴板无法完整备份，或备份时已变化"); return
        }
        let write = board.write(job.text)
        let lease = ClipboardLease(snapshot: snapshot, generation: write.generation)
        guard write.succeeded else {
            release(lease, job: job); fail(job, "无法写入本机剪贴板"); return
        }
        onTrace(job.id, .clipboardWritten, scheduler.now)
        let attempt = Attempt(job: job, lease: lease, sequence: sequence, began: scheduler.now)
        active = attempt
        guard job.destination.activate() else {
            finish(attempt, outcome: .failed("无法激活原远控窗口")); return
        }
        onTrace(job.id, .targetActivated, scheduler.now)
        onChange()
        gate(attempt)
    }

    private enum Block { case clipboard, front, window, modifiers }
    private func gate(_ attempt: Attempt) {
        guard active === attempt, attempt.phase == .synchronizing else { return }
        let now = scheduler.now
        if board.generation != attempt.lease.generation {
            if board.holds(attempt.job.text) {
                attempt.lease.adopt(board.generation)
                onTrace(attempt.job.id, .clipboardRepublished, now)
            } else if !attempt.reclaimed && !userActedSince(attempt.began) {
                // Some clients copy the remote clipboard back when they regain
                // focus. Claim the clipboard once more and wait a full sync again.
                let write = board.write(attempt.job.text)
                guard write.succeeded else { stop(attempt, .clipboard); return }
                attempt.lease.adopt(write.generation); attempt.reclaimed = true; attempt.syncFrom = now
                onTrace(attempt.job.id, .clipboardReclaimed, now)
            } else { stop(attempt, .clipboard); return }
        }
        let front = attempt.job.destination.isFrontmost()
        if !front { attempt.frontSince = nil } else if attempt.frontSince == nil { attempt.frontSince = now }
        let synced = now - attempt.syncFrom >= attempt.job.syncDelay - 0.0001
        let settled = attempt.frontSince.map { now - $0 >= Self.frontDwell - 0.0001 } ?? false
        var block: Block?
        if !synced || !settled { block = front ? nil : .front }
        else if !modifiersClear() { block = .modifiers }
        else if !attempt.job.destination.isValid() { block = .window }
        else { post(attempt); return }
        let deadline = attempt.syncFrom + attempt.job.syncDelay + Self.gateTimeout
        if now >= deadline - 0.0001 { stop(attempt, block ?? .front); return }
        let remaining = attempt.syncFrom + attempt.job.syncDelay - now
        scheduler.after(synced ? Self.gateInterval : min(Self.gateInterval, remaining)) { [weak self, weak attempt] in
            guard let self, let attempt else { return }
            self.gate(attempt)
        }
    }
    private func stop(_ attempt: Attempt, _ block: Block) {
        let detail: (DeliveryDiagnostics.Stage, String)
        switch block {
        case .clipboard: detail = (.blockedByClipboard, "投送前剪贴板被其他内容改写，已停止粘贴")
        case .front: detail = (.blockedByFocus, "远控窗口未能回到前台，已停止粘贴")
        case .window: detail = (.blockedByWindow, "原远程窗口已改变，已停止粘贴")
        case .modifiers: detail = (.blockedByModifiers, "检测到仍按住的修饰键，已停止粘贴")
        }
        onTrace(attempt.job.id, detail.0, scheduler.now)
        finish(attempt, outcome: .failed(detail.1))
    }
    private func post(_ attempt: Attempt) {
        attempt.phase = .posting
        attempt.sequence.send(whileValid: { [weak self, weak attempt] in
            guard let self, let attempt, self.active === attempt else { return false }
            return self.valid(attempt)
        }, completion: { [weak self, weak attempt] result in
            guard let self, let attempt, self.active === attempt, attempt.phase == .posting else { return }
            switch result {
            case .notSent:
                self.finish(attempt, outcome: .failed("粘贴按键发出前被取消，文字已保留"))
            case .attempted:
                self.onTrace(attempt.job.id, .pasteAttempted, self.scheduler.now)
                attempt.phase = .holding; attempt.attemptedAt = self.scheduler.now
                self.onOutcome(attempt.job, .unconfirmed(milliseconds: Int((self.scheduler.now - attempt.began) * 1000)))
                self.onChange()
                self.scheduler.after(Self.quietAfterPaste) { [weak self] in self?.onChange() }
                // Restoring waits for lazy remote fetches; otherwise only a short
                // hold keeps the next write from racing this paste.
                self.scheduler.after(attempt.job.restoreClipboard ? 3 : 0.8) { [weak self, weak attempt] in
                    guard let self, let attempt, self.active === attempt else { return }
                    self.finish(attempt, outcome: nil)
                }
            }
        })
    }

    private func valid(_ attempt: Attempt) -> Bool {
        board.generation == attempt.lease.generation && attempt.job.destination.isFrontmost() && attempt.job.destination.isValid()
    }
    private func release(_ lease: ClipboardLease, job: DeliveryJob) {
        guard job.restoreClipboard else { return }
        if lease.release(on: board) == .failed { onRestoreFailure() }
    }
    private func fail(_ job: DeliveryJob, _ reason: String) {
        onTrace(job.id, .failed, scheduler.now)
        onOutcome(job, .failed(reason)); onChange(); pump()
    }
    private func finish(_ attempt: Attempt, outcome: DeliveryOutcome?) {
        guard active === attempt else { return }
        release(attempt.lease, job: attempt.job)
        active = nil
        if let outcome {
            if case .failed = outcome { onTrace(attempt.job.id, .failed, scheduler.now) }
            onOutcome(attempt.job, outcome)
        }
        onChange()
        pump()
    }

    func cancelAll(reason: String) {
        cancelling = true
        let jobs = pending
        pending.removeAll()
        for job in jobs { onOutcome(job, .failed(reason)) }
        if let attempt = active {
            if attempt.phase == .posting {
                // Releases held keys and reports whether V-down was already sent.
                attempt.sequence.cancel()
            }
            if active === attempt {
                finish(attempt, outcome: attempt.phase == .holding ? nil : .failed(reason))
            }
        }
        cancelling = false
        onChange()
    }
}

enum RecoveryStatus {
    case queued, cancelled, failed(String), unconfirmed
    var label: String {
        switch self {
        case .queued: return "等待投送"
        case .cancelled: return "已取消"
        case .failed: return "未投送"
        case .unconfirmed: return "已发出 · 未确认"
        }
    }
}
struct RecoveryRecord {
    let id: UUID
    let text: String
    let destination: RemoteDestination
    let createdAt: TimeInterval
    var status: RecoveryStatus
    var sourceMustStop = false
}
final class RecoveryStore {
    private(set) var records: [RecoveryRecord] = []
    func prune(now: TimeInterval) { records.removeAll { now - $0.createdAt >= 300 } }
    func save(_ record: RecoveryRecord, now: TimeInterval) {
        prune(now: now)
        records.removeAll { $0.id == record.id }
        records.insert(record, at: 0)
        if records.count > 5 { records.removeLast(records.count - 5) }
    }
    func update(_ id: UUID, status: RecoveryStatus, now: TimeInterval) {
        prune(now: now)
        if let i = records.firstIndex(where: { $0.id == id }) { records[i].status = status }
    }
    func remove(_ id: UUID) { records.removeAll { $0.id == id } }
    func clear() { records.removeAll() }
}
