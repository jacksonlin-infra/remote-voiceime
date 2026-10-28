import AppKit
import ApplicationServices

enum RemoteClientProfile: String, CaseIterable {
    case uu, windowsApp
    var name: String { self == .uu ? "UU 远程" : "Windows App" }
    var bundleID: String { self == .uu ? "com.netease.uuremote" : "com.microsoft.rdc.macos" }
    func matches(bundleID: String?) -> Bool { bundleID == self.bundleID }
}

enum SyncSpeed: String, CaseIterable {
    case fast, standard, compatible
    var delay: TimeInterval {
        switch self {
        case .fast: return 0.18
        case .standard: return 0.5
        case .compatible: return 1.2
        }
    }
    var title: String {
        switch self {
        case .fast: return "快速 · 180 毫秒"
        case .standard: return "标准 · 0.5 秒"
        case .compatible: return "兼容 · 1.2 秒"
        }
    }
}

struct RemoteDeliveryOptions: Equatable {
    var control: Bool
    var speed: SyncSpeed
    var syncDelay: TimeInterval { speed.delay }
}

// UU keeps the user's existing paste-key choice. Older builds saved the 180 ms
// path even when the user only changed the paste key, so a legacy "not
// compatible" value maps to the standard wait; only an explicit new choice is fast.
final class RemotePreferences {
    private let defaults: UserDefaults
    init(defaults: UserDefaults = .standard) { self.defaults = defaults }
    func options(for profile: RemoteClientProfile) -> RemoteDeliveryOptions {
        let prefix = "remote.\(profile.rawValue)."
        let control = defaults.object(forKey: prefix + "control") as? Bool
            ?? (profile == .uu && defaults.bool(forKey: "pasteModeControl"))
        let legacy = defaults.object(forKey: prefix + "compatibility") as? Bool
            ?? (profile == .uu ? defaults.object(forKey: "compatibilityDelay") as? Bool : nil)
        let speed = (defaults.object(forKey: prefix + "speed") as? String).flatMap(SyncSpeed.init(rawValue:))
            ?? (legacy == true ? .compatible : profile == .uu || legacy == false ? .standard : .compatible)
        return RemoteDeliveryOptions(control: control, speed: speed)
    }
    func save(_ options: RemoteDeliveryOptions, for profile: RemoteClientProfile) {
        defaults.set(options.control, forKey: "remote.\(profile.rawValue).control")
        defaults.set(options.speed.rawValue, forKey: "remote.\(profile.rawValue).speed")
    }
    // A long right-Ctrl press re-pastes the last text. On unless turned off.
    var longPressRepaste: Bool {
        get { defaults.object(forKey: "shortcut.longPressRepaste") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "shortcut.longPressRepaste") }
    }
    // A test result counts only for the paste key and sync speed it was made with.
    func calibration(for profile: RemoteClientProfile) -> CalibrationResult {
        guard let raw = defaults.string(forKey: "calibration.\(profile.rawValue)") else { return .untested }
        let parts = raw.split(separator: "|").map(String.init)
        let options = options(for: profile)
        guard parts.count == 3, parts[1] == String(options.control), parts[2] == options.speed.rawValue else { return .untested }
        return parts[0] == "pass" ? .passed : .failed
    }
    func saveCalibration(_ passed: Bool, for profile: RemoteClientProfile) {
        let options = options(for: profile)
        defaults.set("\(passed ? "pass" : "fail")|\(options.control)|\(options.speed.rawValue)", forKey: "calibration.\(profile.rawValue)")
    }
    // Leaving the transcript on the clipboard lets a late remote sync be fixed
    // with one manual paste; restoring would put the old clipboard back.
    var restoresClipboard: Bool {
        get { defaults.bool(forKey: "delivery.restoreClipboard") }
        set { defaults.set(newValue, forKey: "delivery.restoreClipboard") }
    }
}

enum CalibrationResult: Equatable {
    case untested, passed, failed
    var label: String {
        switch self {
        case .untested: return "未测试"
        case .passed: return "已通过"
        case .failed: return "上次未通过"
        }
    }
}

protocol RemoteClientAdapter {
    var profile: RemoteClientProfile { get }
    var id: String { get }
    var name: String { get }
    func matches(_ app: NSRunningApplication) -> Bool
    func capture(_ app: NSRunningApplication) -> RemoteDestination?
}
extension RemoteClientAdapter {
    var id: String { profile.rawValue + "-clipboard" }
    var name: String { profile.name }
    func matches(_ app: NSRunningApplication) -> Bool { profile.matches(bundleID: app.bundleIdentifier) }
    func capture(_ app: NSRunningApplication) -> RemoteDestination? {
        guard matches(app) else { return nil }
        return AXRemoteDestination(app: app, profile: profile)
    }
}
struct UURemoteAdapter: RemoteClientAdapter {
    let profile = RemoteClientProfile.uu
}
struct WindowsAppAdapter: RemoteClientAdapter {
    let profile = RemoteClientProfile.windowsApp
}

final class AXRemoteDestination: RemoteDestination {
    let app: NSRunningApplication
    let window: AXUIElement
    let label: String
    let profile: RemoteClientProfile
    private let capturedAt = ProcessInfo.processInfo.systemUptime
    // Local identity only; no extra AX reads, titles or remote account details.
    var diagnosticIdentity: String {
        "pid=\(app.processIdentifier),window=\(CFHash(window)),age_ms=\(Int((ProcessInfo.processInfo.systemUptime - capturedAt) * 1000))"
    }
    init?(app: NSRunningApplication, profile: RemoteClientProfile) {
        let root = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(root, 0.3)
        guard let raw = axValue(root, kAXFocusedWindowAttribute), CFGetTypeID(raw) == AXUIElementGetTypeID() else { return nil }
        self.app = app
        self.profile = profile
        window = unsafeBitCast(raw, to: AXUIElement.self)
        AXUIElementSetMessagingTimeout(window, 0.3)
        let title = axString(window, kAXTitleAttribute)
        label = title.isEmpty ? profile.name : profile.name + " · " + title
    }
    func isValid() -> Bool {
        guard !app.isTerminated else { return false }
        let root = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(root, 0.3)
        guard let current = axValue(root, kAXFocusedWindowAttribute) else { return false }
        return CFEqual(current, window)
    }
    func isFrontmost() -> Bool { NSWorkspace.shared.frontmostApplication?.processIdentifier == app.processIdentifier }
    // Returns whether the window is still the target; whether it actually came
    // forward is checked by the delivery gate, which waits for it.
    func activate() -> Bool {
        guard isValid() else { return false }
        if !isFrontmost() {
            // macOS 14+ activation is cooperative: a request from an app that is
            // not active is declined, and its result is not worth failing on.
            if #available(macOS 14.0, *) { NSApp.yieldActivation(to: app) }
            _ = app.activate(options: [])
            // The accessibility API still brings the client forward from the
            // menu bar or the passive HUD, where this app is never active.
            let root = AXUIElementCreateApplication(app.processIdentifier)
            AXUIElementSetMessagingTimeout(root, 0.3)
            _ = AXUIElementSetAttributeValue(root, kAXFrontmostAttribute as CFString, kCFBooleanTrue)
        }
        // Some clients do not expose AXRaise, even when activation succeeds.
        // The delivery guard rechecks the focused window before any paste key.
        _ = AXUIElementPerformAction(window, kAXRaiseAction as CFString)
        return true
    }
}
