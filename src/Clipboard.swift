import AppKit
import ApplicationServices

final class NativeClipboard: ClipboardAccess {
    let board: NSPasteboard
    init(board: NSPasteboard = .general) { self.board = board }
    var generation: Int { board.changeCount }
    func capture() -> ClipboardSnapshot? {
        let start = generation
        guard let items = board.pasteboardItems else {
            return (board.types ?? []).isEmpty ? ClipboardSnapshot(generation: start, items: []) : nil
        }
        var all: [[String: Data]] = []
        var total = 0
        for item in items {
            var values: [String: Data] = [:]
            for type in item.types {
                guard let data = item.data(forType: type) else { return nil }
                total += data.count
                guard total <= 16_000_000 else { return nil }
                values[type.rawValue] = data
            }
            all.append(values)
        }
        guard generation == start else { return nil }
        return ClipboardSnapshot(generation: start, items: all)
    }
    func write(_ text: String) -> ClipboardWrite {
        board.clearContents()
        let success = board.setString(text, forType: .string)
        return ClipboardWrite(generation: generation, succeeded: success)
    }
    func holds(_ text: String) -> Bool { board.string(forType: .string) == text }
    func restore(_ snapshot: ClipboardSnapshot) -> Bool {
        let items = snapshot.items.map { values -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (type, data) in values { item.setData(data, forType: NSPasteboard.PasteboardType(type)) }
            return item
        }
        board.clearContents()
        return items.isEmpty || board.writeObjects(items)
    }
}
