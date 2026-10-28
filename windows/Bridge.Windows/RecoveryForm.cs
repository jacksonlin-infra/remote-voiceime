using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

internal sealed class RecoveryForm : Form
{
    internal Action<Guid>? RestoreRecord;
    internal Action<Guid>? RedeliverRecord;
    internal Action? ClearRecords;
    internal Func<Guid, bool>? CanRestore;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, BorderStyle = BorderStyle.None, AccessibleName = "最近文字列表" };
    private readonly TextBox text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, AccessibleName = "最近文字原文" };
    private readonly Label destination = UiStyle.Label("", secondary: true);
    private readonly Label note = UiStyle.Label("");
    private readonly Button restore, redeliver, copy, clear;
    private IReadOnlyList<RecoveryRecord> records = [];
    private double now;
    private string key = "F8";
    private RecoveryRecord? Selected => list.SelectedIndex >= 0 && list.SelectedIndex < records.Count ? records[list.SelectedIndex] : null;
    internal RecoveryForm()
    {
        Text = "最近文字"; Font = UiStyle.Body; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(800, 510); MinimumSize = new Size(720, 480); StartPosition = FormStartPosition.CenterScreen;
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 88, ColumnCount = 2, Padding = new Padding(24, 18, 24, 10) };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        var headings = UiStyle.Column(); headings.Controls.Add(UiStyle.Label("最近文字", heading: true)); headings.Controls.Add(UiStyle.Label("最近 5 条 · 保留 5 分钟 · 退出即清空", secondary: true));
        clear = UiStyle.Button("清空", () =>
        {
            if (MessageBox.Show(this, "清空内存中的最近文字？这不会取消正在听写或等待投送的任务。", "清空最近文字", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK)
                ClearRecords?.Invoke();
        });
        header.Controls.Add(headings); header.Controls.Add(clear);
        var footer = UiStyle.Label("仅保存在本机内存。本工具不写入正文历史；豆包与远控软件的数据处理由各自负责。", secondary: true);
        footer.Dock = DockStyle.Bottom; footer.AutoSize = false; footer.Height = 40; footer.Padding = new Padding(24, 10, 24, 0); footer.Font = UiStyle.Small;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        body.ColumnStyles.Add(new(SizeType.Absolute, 250)); body.ColumnStyles.Add(new(SizeType.Percent, 100));
        list.ItemHeight = 106; body.Controls.Add(list);
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 12), RowCount = 4, ColumnCount = 1 };
        detail.ColumnStyles.Add(new(SizeType.Percent, 100));
        detail.RowStyles.Add(new(SizeType.AutoSize)); detail.RowStyles.Add(new(SizeType.Percent, 100));
        detail.RowStyles.Add(new(SizeType.AutoSize)); detail.RowStyles.Add(new(SizeType.Absolute, 40));
        text.Font = UiStyle.Body;
        copy = UiStyle.Button("复制原文", CopySelected);
        restore = UiStyle.Button("打开核对…", () =>
        {
            if (Selected is not { } record || CanRestore?.Invoke(record.Id) != true) return;
            Hide(); RestoreRecord?.Invoke(record.Id);
        });
        redeliver = UiStyle.Button("再投送到原窗口", () =>
        {
            if (Selected is not { } record || CanRestore?.Invoke(record.Id) != true) return;
            Hide(); RedeliverRecord?.Invoke(record.Id);
        }, primary: true);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.Add(redeliver); actions.Controls.Add(restore); actions.Controls.Add(copy);
        detail.Controls.Add(destination, 0, 0); detail.Controls.Add(text, 0, 1); detail.Controls.Add(note, 0, 2); detail.Controls.Add(actions, 0, 3);
        body.Controls.Add(detail);
        Controls.Add(body); Controls.Add(footer); Controls.Add(header);
        list.SelectedIndexChanged += (_, _) => ShowSelected();
        list.DrawItem += DrawRecord;
        Shown += (_, _) => list.ItemHeight = (int)(106 * DeviceDpi / 96f);
        DpiChanged += (_, _) => list.ItemHeight = (int)(106 * DeviceDpi / 96f);
        FormClosing += (_, e) => { e.Cancel = true; Hide(); };
        UiStyle.Watch(this); UiStyle.FitToScreen(this); ShowSelected();
    }
    internal void SetKey(string value) { key = value; if (Selected == null) ShowSelected(); }
    internal void RefreshRecords(IReadOnlyList<RecoveryRecord> values, double time)
    {
        var selected = Selected?.Id; now = time;
        // Keep selection and scroll stable while the one-second refresh updates ages.
        if (!values.SequenceEqual(records))
        {
            records = values.ToArray(); list.BeginUpdate(); list.Items.Clear();
            list.Items.AddRange(records.Select(x => (object)x.Id).ToArray());
            if (records.Count > 0) list.SelectedIndex = Math.Max(0, records.ToList().FindIndex(x => x.Id == selected));
            list.EndUpdate(); ShowSelected();
        }
        restore.Enabled = redeliver.Enabled = Selected is { } record && CanRestore?.Invoke(record.Id) == true;
        clear.Enabled = records.Count > 0; list.Invalidate();
    }
    private void ShowSelected()
    {
        if (Selected is not { } record)
        {
            destination.Text = "暂时没有最近文字"; text.Text = $"回到远端普通文本框，按 {key} 开始下一句。";
            note.Text = "听写结束、取消或遇到问题时，文字会暂存在这里。"; copy.Enabled = restore.Enabled = redeliver.Enabled = clear.Enabled = false; return;
        }
        destination.Text = record.Target.Label; text.Text = record.Text; copy.Enabled = true; clear.Enabled = true;
        restore.Enabled = redeliver.Enabled = CanRestore?.Invoke(record.Id) == true;
        note.Text = record.Status switch
        {
            RecoveryStatus.Unconfirmed => "上次已发出粘贴。远端确实没出现时再投送，以免重复。",
            RecoveryStatus.Queued => "这条文字仍在等待投送。取消待发任务请使用托盘中的独立操作。",
            RecoveryStatus.Cancelled => "已取消投送，原文保留。恢复后请核对原来的目标。",
            _ => record.Reason + "；恢复时仍使用原来的目标。"
        };
    }
    private void CopySelected()
    {
        if (Selected is not { } record) return;
        try { Clipboard.SetText(record.Text); note.Text = "已复制原文，可手动粘贴到所需位置。"; }
        catch (System.Runtime.InteropServices.ExternalException) { note.Text = "剪贴板暂时被占用，原文仍保留，请稍后再复制。"; }
    }
    private void DrawRecord(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= records.Count) return;
        var record = records[e.Index]; var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? UiStyle.Surface : UiStyle.Soft);
        e.Graphics.FillRectangle(background, e.Bounds);
        var inset = (int)(12 * DeviceDpi / 96f); var line = (int)(20 * DeviceDpi / 96f);
        var bounds = new Rectangle(e.Bounds.X + inset, e.Bounds.Y + inset, e.Bounds.Width - inset * 2, line);
        var status = record.Status switch { RecoveryStatus.Unconfirmed => "已发出 · 未确认", RecoveryStatus.Failed => "未投送", RecoveryStatus.Cancelled => "已取消", _ => "等待投送" };
        TextRenderer.DrawText(e.Graphics, status + $" · {(int)Math.Max(0, now - record.Created)} 秒前", UiStyle.Small, bounds, UiStyle.Secondary, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        bounds.Y += line; bounds.Height = line * 2;
        TextRenderer.DrawText(e.Graphics, record.Text.Replace('\n', ' '), UiStyle.Body, bounds, UiStyle.Ink, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        bounds.Y += line * 2; bounds.Height = line;
        TextRenderer.DrawText(e.Graphics, record.Target.Label, UiStyle.Small, bounds, UiStyle.Secondary, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }
}
