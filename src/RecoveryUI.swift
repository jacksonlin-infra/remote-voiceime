import AppKit

private final class RecoveryRowView: NSTableRowView {
    override func drawSelection(in dirtyRect: NSRect) {
        Design.surface.setFill()
        NSBezierPath(roundedRect: bounds.insetBy(dx: 10, dy: 4), xRadius: 7, yRadius: 7).fill()
    }
}

final class RecoveryController: NSObject, NSTableViewDataSource, NSTableViewDelegate {
    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 510), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
    var onRestore: ((UUID) -> Void)?
    var onClear: (() -> Void)?
    var canRestore: ((UUID) -> Bool)?
    private var records: [RecoveryRecord] = []
    private var now: TimeInterval = 0
    private let table = NSTableView()
    private let text = NSTextView()
    private let destination = NativeUI.label("", size: 11, secondary: true)
    private let note = NativeUI.label("", size: 11)
    private let empty = NativeUI.label("暂时没有最近文字\n\n回到远端输入框，按右 Ctrl 开始下一句。", size: 14, secondary: true)
    private lazy var restore = ActionButton("恢复草稿", primary: true) { [weak self] in self?.restoreSelected() }
    private lazy var copy = ActionButton("复制原文") { [weak self] in self?.copySelected() }
    private lazy var clear = ActionButton("清空") { [weak self] in self?.confirmClear() }
    private var selected: RecoveryRecord? { records.indices.contains(table.selectedRow) ? records[table.selectedRow] : nil }

    override init() {
        super.init()
        window.title = "最近文字"; window.minSize = NSSize(width: 700, height: 440); window.isReleasedWhenClosed = false
        let root = NativeUI.windowBody(window, title: "最近文字", width: 800, height: 506)
        let heading = NativeUI.column([NativeUI.label("最近文字", size: 19, weight: .semibold), NativeUI.label("最近 5 条 · 保留 5 分钟 · 退出即清空", size: 11, secondary: true)], spacing: 6)
        let spacer = NSView(); spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let header = NSStackView(views: [heading, spacer, clear]); header.spacing = 20; header.distribution = .fill
        heading.setContentHuggingPriority(.defaultLow, for: .horizontal)
        NativeUI.place(header, in: root, inset: 24)
        let list = NSScrollView(); list.hasVerticalScroller = true; list.borderType = .noBorder
        list.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(list)
        table.headerView = nil; table.rowHeight = 106; table.intercellSpacing = NSSize(width: 0, height: 3)
        table.backgroundColor = Design.soft; table.style = .fullWidth
        let column = NSTableColumn(identifier: .init("record")); column.width = 250; table.addTableColumn(column)
        table.delegate = self; table.dataSource = self; table.setAccessibilityLabel("最近文字列表")
        list.documentView = table
        let detail = NSView(); detail.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(detail)
        let editor = NSScrollView(); editor.hasVerticalScroller = true; editor.borderType = .noBorder; editor.drawsBackground = false
        text.frame = NSRect(x: 0, y: 0, width: 480, height: 170)
        text.isEditable = false; text.isRichText = false; text.drawsBackground = false; text.font = Design.font(ofSize: 13); text.textColor = Design.ink
        text.isVerticallyResizable = true; text.autoresizingMask = [.width]; text.textContainer?.widthTracksTextView = true
        text.textContainerInset = NSSize(width: 0, height: 3); text.setAccessibilityLabel("最近文字原文")
        editor.documentView = text
        let actions = NSStackView(views: [copy, restore]); actions.spacing = 8; actions.distribution = .gravityAreas
        for child in [destination, editor, note, actions] { child.translatesAutoresizingMaskIntoConstraints = false; detail.addSubview(child) }
        let footerSurface = SurfaceView(); footerSurface.fill = Design.soft
        footerSurface.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(footerSurface)
        let footerRule = NativeUI.separator(); footerRule.translatesAutoresizingMaskIntoConstraints = false; footerSurface.addSubview(footerRule)
        NSLayoutConstraint.activate([footerSurface.leadingAnchor.constraint(equalTo: root.leadingAnchor), footerSurface.trailingAnchor.constraint(equalTo: root.trailingAnchor), footerSurface.bottomAnchor.constraint(equalTo: root.bottomAnchor), footerSurface.heightAnchor.constraint(equalToConstant: 42), footerRule.topAnchor.constraint(equalTo: footerSurface.topAnchor), footerRule.leadingAnchor.constraint(equalTo: footerSurface.leadingAnchor), footerRule.trailingAnchor.constraint(equalTo: footerSurface.trailingAnchor)])
        let footer = NativeUI.label("仅保存在本机内存。本工具不将正文写入磁盘；豆包与远控软件的数据处理由各自负责。", size: 10, secondary: true)
        footer.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(footer)
        empty.alignment = .center; empty.translatesAutoresizingMaskIntoConstraints = false; detail.addSubview(empty)
        NSLayoutConstraint.activate([
            header.heightAnchor.constraint(equalToConstant: 48),
            list.leadingAnchor.constraint(equalTo: root.leadingAnchor), list.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 20),
            list.widthAnchor.constraint(equalToConstant: 255), list.bottomAnchor.constraint(equalTo: footer.topAnchor, constant: -16),
            detail.leadingAnchor.constraint(equalTo: list.trailingAnchor, constant: 24), detail.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -24),
            detail.topAnchor.constraint(equalTo: list.topAnchor), detail.bottomAnchor.constraint(equalTo: list.bottomAnchor),
            destination.leadingAnchor.constraint(equalTo: detail.leadingAnchor), destination.trailingAnchor.constraint(equalTo: detail.trailingAnchor), destination.topAnchor.constraint(equalTo: detail.topAnchor, constant: 8),
            editor.leadingAnchor.constraint(equalTo: detail.leadingAnchor), editor.trailingAnchor.constraint(equalTo: detail.trailingAnchor),
            editor.topAnchor.constraint(equalTo: destination.bottomAnchor, constant: 14), editor.bottomAnchor.constraint(equalTo: note.topAnchor, constant: -14),
            note.leadingAnchor.constraint(equalTo: detail.leadingAnchor), note.trailingAnchor.constraint(equalTo: detail.trailingAnchor), note.bottomAnchor.constraint(equalTo: actions.topAnchor, constant: -20),
            actions.trailingAnchor.constraint(equalTo: detail.trailingAnchor), actions.bottomAnchor.constraint(equalTo: detail.bottomAnchor, constant: -10),
            footer.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 24), footer.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -24), footer.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -14),
            empty.leadingAnchor.constraint(equalTo: detail.leadingAnchor), empty.trailingAnchor.constraint(equalTo: detail.trailingAnchor), empty.centerYAnchor.constraint(equalTo: detail.centerYAnchor)
        ])
        let rule = NativeUI.separator(); rule.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(rule)
        NSLayoutConstraint.activate([rule.leadingAnchor.constraint(equalTo: root.leadingAnchor), rule.trailingAnchor.constraint(equalTo: root.trailingAnchor), rule.bottomAnchor.constraint(equalTo: list.topAnchor)])
        reload([], now: 0)
    }
    func reload(_ values: [RecoveryRecord], now: TimeInterval) {
        let unchanged = records.count == values.count && zip(records, values).allSatisfy { pair in
            pair.0.id == pair.1.id && pair.0.text == pair.1.text && pair.0.createdAt == pair.1.createdAt && String(describing: pair.0.status) == String(describing: pair.1.status)
        }
        if unchanged {
            self.now = now
            if records.isEmpty { showSelected(); return }
            for row in records.indices {
                if let cell = table.view(atColumn: 0, row: row, makeIfNecessary: false),
                   let column = cell.subviews.first as? NSStackView, let label = column.arrangedSubviews.first as? NSTextField {
                    label.stringValue = records[row].status.label + " · " + String(max(0, Int(now - records[row].createdAt))) + " 秒前"
                }
            }
            restore.isEnabled = selected.map { canRestore?($0.id) ?? false } ?? false
            return
        }
        let oldID = selected?.id
        records = values; self.now = now; table.reloadData()
        if !records.isEmpty { table.selectRowIndexes(IndexSet(integer: records.firstIndex { $0.id == oldID } ?? 0), byExtendingSelection: false) }
        showSelected()
    }
    func present() { NativeUI.present(window) }
    func numberOfRows(in tableView: NSTableView) -> Int { records.count }
    func tableView(_ tableView: NSTableView, rowViewForRow row: Int) -> NSTableRowView? { RecoveryRowView() }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard records.indices.contains(row) else { return nil }
        let record = records[row]
        let state = NativeUI.label(record.status.label + " · " + String(max(0, Int(now - record.createdAt))) + " 秒前", size: 10, secondary: true)
        let preview = NativeUI.label(record.text.replacingOccurrences(of: "\n", with: " "), size: 12)
        preview.maximumNumberOfLines = 2; preview.lineBreakMode = .byTruncatingTail
        let destination = NativeUI.label(record.destination.label, size: 10, secondary: true)
        destination.maximumNumberOfLines = 1; destination.lineBreakMode = .byTruncatingMiddle
        let column = NativeUI.column([state, preview, destination], spacing: 6)
        NativeUI.fillWidth(column)
        let view = NSView(); NativeUI.place(column, in: view, inset: 20)
        return view
    }
    func tableViewSelectionDidChange(_ notification: Notification) { showSelected() }
    private func showSelected() {
        guard let record = selected else {
            text.string = ""; destination.stringValue = ""; note.stringValue = ""
            restore.isEnabled = false; copy.isEnabled = false; clear.isEnabled = false; empty.isHidden = false; return
        }
        empty.isHidden = true; copy.isEnabled = true; clear.isEnabled = true
        destination.stringValue = record.destination.label; destination.toolTip = record.destination.label
        text.string = record.text; text.scrollToBeginningOfDocument(nil)
        restore.isEnabled = canRestore?(record.id) ?? false
        switch record.status {
        case .unconfirmed:
            note.stringValue = "上次已发出粘贴。先查看远端，再决定是否重发，以免重复。"; restore.title = "核对并重发…"
        case .failed(let reason): note.stringValue = reason + "。恢复时仍使用原来的目标。"; restore.title = "恢复草稿"
        case .cancelled: note.stringValue = "已取消本次投送。恢复后请确认豆包录音已结束，再核对正文与目标。"; restore.title = "恢复草稿"
        case .queued: note.stringValue = "这条文字仍在等待投送。可复制；取消待发任务请使用菜单栏中的独立操作。"; restore.title = "等待投送"
        }
    }
    private func restoreSelected() {
        guard let record = selected, canRestore?(record.id) == true else { return }
        window.orderOut(nil); onRestore?(record.id)
    }
    private func copySelected() {
        guard let record = selected else { return }
        NSPasteboard.general.clearContents()
        note.stringValue = NSPasteboard.general.setString(record.text, forType: .string) ? "已复制原文，可手动粘贴到所需位置。" : "复制失败，原文仍保留在这里。"
    }
    private func confirmClear() {
        guard !records.isEmpty else { return }
        let alert = NSAlert(); alert.messageText = "清空最近文字？"
        alert.informativeText = "这会删除内存中的最近文字，不会取消正在听写或等待投送的任务。"
        alert.addButton(withTitle: "保留"); alert.addButton(withTitle: "清空最近文字")
        alert.beginSheetModal(for: window) { [weak self] result in
            if result == .alertSecondButtonReturn { self?.onClear?() }
        }
    }
}
