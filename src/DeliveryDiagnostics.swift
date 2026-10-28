import Foundation

// Metadata only: never accepts a transcript, clipboard contents or window title.
final class DeliveryDiagnostics {
    enum Origin: String { case calibration, voice, recovery }
    enum Stage: String {
        case queued, clipboardWritten, targetActivated, pasteAttempted, failed
        case clipboardRepublished, clipboardReclaimed
        case blockedByClipboard, blockedByFocus, blockedByWindow, blockedByModifiers
    }
    private var lines: [String] = []
    func begin(id: UUID, origin: Origin, profile: RemoteClientProfile, control: Bool, delay: TimeInterval, inputSource: String, targetIdentity: String = "unavailable") {
        append("\(id.uuidString.prefix(8)) origin=\(origin.rawValue) client=\(profile.rawValue) paste=\(control ? "Ctrl+V" : "Cmd+V") delay_ms=\(Int(delay * 1000)) input=\(inputSource) target=\(targetIdentity)")
    }
    func record(id: UUID, stage: Stage, now: TimeInterval) {
        append("\(id.uuidString.prefix(8)) t=\(String(format: "%.3f", now)) stage=\(stage.rawValue)")
    }
    // A submission that never became a job, named by a fixed code.
    func refused(_ code: String, origin: Origin, now: TimeInterval) {
        append("submit t=\(String(format: "%.3f", now)) origin=\(origin.rawValue) refused=\(code)")
    }
    private func append(_ value: String) {
        lines.append(value)
        if lines.count > 80 { lines.removeFirst(lines.count - 80) }
    }
    var report: String {
        (["\(ProductInfo.name) \(ProductInfo.versionLabel)", "Metadata only; pasteAttempted is not a remote receipt."] + lines).joined(separator: "\n")
    }
}
