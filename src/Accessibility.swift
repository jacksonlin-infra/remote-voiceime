import AppKit
import ApplicationServices

func axValue(_ element: AXUIElement, _ attribute: String) -> CFTypeRef? {
    var value: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
    return value
}
func axChildren(_ element: AXUIElement) -> [AXUIElement] { axValue(element, kAXChildrenAttribute) as? [AXUIElement] ?? [] }
func axString(_ element: AXUIElement, _ attribute: String) -> String { axValue(element, attribute) as? String ?? "" }
