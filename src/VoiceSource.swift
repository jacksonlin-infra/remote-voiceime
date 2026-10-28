import AppKit
import ApplicationServices

import Carbon

protocol VoiceSourceAdapter: AnyObject {
    var id: String { get }
    var name: String { get }
    func owns(_ application: NSRunningApplication) -> Bool
    // Installed and enabled as an input source; drives the setup checklist.
    var isAvailable: Bool { get }
    func start(cancellation: SourceCancellation, completion: @escaping (Result<Void, SourceError>) -> Void)
    func probe(completion: @escaping (VoiceVisibility) -> Void)
    func stop(ownsInputFocus: Bool) -> Bool
    func cancel(ownsInputFocus: Bool) -> Bool
}
struct SourceError: Error { let message: String }

final class DoubaoSource: VoiceSourceAdapter {
    let id = "doubao-ime"
    let name = "豆包"
    private let prefix = "com.bytedance.inputmethod.doubaoime"
    private let worker = DispatchQueue(label: "voice.doubao.ax", qos: .userInitiated)
    func owns(_ application: NSRunningApplication) -> Bool { application.bundleIdentifier?.hasPrefix(prefix) == true }

    var isAvailable: Bool {
        let filter = [kTISPropertyInputSourceIsEnabled as String: true] as CFDictionary
        guard let sources = TISCreateInputSourceList(filter, false)?.takeRetainedValue() as? [TISInputSource] else { return false }
        return sources.contains { source in
            guard let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { return false }
            return (Unmanaged<CFString>.fromOpaque(raw).takeUnretainedValue() as String).hasPrefix(prefix)
        }
    }
    private func selectInputSource() -> Bool {
        if let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
           let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceID),
           (Unmanaged<CFString>.fromOpaque(raw).takeUnretainedValue() as String).hasPrefix(prefix) { return true }
        let filter = [kTISPropertyInputSourceIsEnabled as String: true] as CFDictionary
        guard let sources = TISCreateInputSourceList(filter, false)?.takeRetainedValue() as? [TISInputSource] else { return false }
        for source in sources {
            guard let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { continue }
            let id = Unmanaged<CFString>.fromOpaque(raw).takeUnretainedValue() as String
            if id.hasPrefix(prefix) && TISSelectInputSource(source) == noErr { return true }
        }
        return false
    }

    func start(cancellation: SourceCancellation, completion: @escaping (Result<Void, SourceError>) -> Void) {
        guard selectInputSource() else { completion(.failure(SourceError(message: "未找到已启用的豆包输入法"))); return }
        let pids = NSWorkspace.shared.runningApplications.filter { $0.executableURL?.lastPathComponent == "TextInputMenuAgent" }.map(\.processIdentifier)
        guard !pids.isEmpty else { completion(.failure(SourceError(message: "找不到本机输入法菜单代理"))); return }
        worker.asyncAfter(deadline: .now() + 0.12) {
            // Activation can take longer when the remote client is full screen
            // or on another Space; wait for it instead of failing the start.
            let deadline = ProcessInfo.processInfo.systemUptime + 1.0
            while !cancellation.isCancelled, NSWorkspace.shared.frontmostApplication?.processIdentifier != getpid(),
                  ProcessInfo.processInfo.systemUptime < deadline { Thread.sleep(forTimeInterval: 0.02) }
            let result = self.pressMenu(pids: pids, cancellation: cancellation)
            DispatchQueue.main.async { completion(result) }
        }
    }

    private func mayStart(_ token: SourceCancellation) -> Bool {
        !token.isCancelled && NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid()
    }
    private func findVoiceItem(_ element: AXUIElement, depth: Int = 0) -> AXUIElement? {
        guard depth < 5 else { return nil }
        let title = axString(element, kAXTitleAttribute)
        if axString(element, kAXRoleAttribute) == kAXMenuItemRole,
           title.contains("语音输入") || title.contains("Voice Input") { return element }
        for child in axChildren(element) {
            if let found = findVoiceItem(child, depth: depth + 1) { return found }
        }
        return nil
    }
    private func pressMenu(pids: [pid_t], cancellation: SourceCancellation) -> Result<Void, SourceError> {
        for pid in pids {
            guard mayStart(cancellation) else { return .failure(SourceError(message: "焦点已变化，已停止启动语音")) }
            let root = AXUIElementCreateApplication(pid)
            AXUIElementSetMessagingTimeout(root, 0.7)
            guard let raw = axValue(root, "AXExtrasMenuBar"), CFGetTypeID(raw) == AXUIElementGetTypeID() else { continue }
            let bar = unsafeBitCast(raw, to: AXUIElement.self)
            for item in axChildren(bar) {
                guard mayStart(cancellation) else { return .failure(SourceError(message: "启动已取消")) }
                // Preserve the validated menu path: opening AXPress may block until the menu closes.
                if axChildren(item).isEmpty {
                    DispatchQueue.global(qos: .userInitiated).async {
                        guard self.mayStart(cancellation) else { return }
                        _ = AXUIElementPerformAction(item, kAXPressAction as CFString)
                    }
                }
                let deadline = ProcessInfo.processInfo.systemUptime + 1.2
                repeat {
                    guard mayStart(cancellation) else {
                        _ = AXUIElementPerformAction(item, kAXCancelAction as CFString)
                        return .failure(SourceError(message: "焦点已变化，已取消启动"))
                    }
                    if let voice = findVoiceItem(item) {
                        if let enabled = axValue(voice, kAXEnabledAttribute) as? Bool, !enabled { break }
                        guard mayStart(cancellation) else { break }
                        if AXUIElementPerformAction(voice, kAXPressAction as CFString) == .success { return .success(()) }
                        break
                    }
                    Thread.sleep(forTimeInterval: 0.025)
                } while ProcessInfo.processInfo.systemUptime < deadline
                _ = AXUIElementPerformAction(item, kAXCancelAction as CFString)
            }
        }
        return .failure(SourceError(message: "无法启动豆包语音，请检查输入法菜单与辅助功能权限"))
    }

    func probe(completion: @escaping (VoiceVisibility) -> Void) {
        let pids = NSWorkspace.shared.runningApplications.filter(owns).map(\.processIdentifier)
        worker.async {
            var unknown = pids.isEmpty
            var visible = false
            for pid in pids {
                let root = AXUIElementCreateApplication(pid)
                AXUIElementSetMessagingTimeout(root, 0.15)
                guard let windows = axValue(root, kAXWindowsAttribute) as? [AXUIElement] else { unknown = true; continue }
                for window in windows {
                    guard let role = axValue(window, kAXRoleAttribute) as? String else { unknown = true; continue }
                    var subrole: CFTypeRef?
                    let error = AXUIElementCopyAttributeValue(window, kAXSubroleAttribute as CFString, &subrole)
                    if error != .success && error != .attributeUnsupported && error != .noValue { unknown = true }
                    if role == "AXSystemDialog" || subrole as? String == "AXSystemDialog" { visible = true }
                }
            }
            let result: VoiceVisibility = visible ? .visible : (unknown ? .unknown : .hidden)
            DispatchQueue.main.async { completion(result) }
        }
    }
    private func localKey(_ key: CGKeyCode, ownsInputFocus: Bool) -> Bool {
        guard ownsInputFocus, NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid(),
              let down = CGEvent(keyboardEventSource: nil, virtualKey: key, keyDown: true),
              let up = CGEvent(keyboardEventSource: nil, virtualKey: key, keyDown: false) else { return false }
        for event in [down, up] {
            event.flags = []
            event.setIntegerValueField(.eventSourceUserData, value: localFinishMarker)
            event.post(tap: .cghidEventTap)
        }
        return true
    }
    func stop(ownsInputFocus: Bool) -> Bool { localKey(36, ownsInputFocus: ownsInputFocus) }
    func cancel(ownsInputFocus: Bool) -> Bool { localKey(53, ownsInputFocus: ownsInputFocus) }
}
