using System.Drawing.Drawing2D;
using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

// One page instead of the four-step guide: every row is computed from the live
// state (Doubao, remote clients, key mode) or from a confirmation the user
// ticked, so nothing has to be resumed and finished rows are never asked again.
internal sealed class SetupForm : Form
{
    internal Action<bool>? VoiceKeyConfirmed;
    internal Action<bool>? ClipboardConfirmed;
    internal Action? UseSharedKey;
    internal Action? TestDelivery;
    internal Action? Start;
    internal Action? Dismissed;
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new Padding(32, 22, 32, 0), AutoScroll = true };
    private readonly Label progress = UiStyle.Label("", secondary: true);
    private readonly Button start, later;
    private SetupStatus status = new(false, false, false, false, [], false, null);
    private string key = "F8";
    internal SetupStatus Status => status;
    internal SetupForm()
    {
        Text = $"准备 {ProductInfo.Name}"; Font = UiStyle.Body; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 560); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        start = UiStyle.Button("开始使用", () => { if (status.Ready) { Hide(); Start?.Invoke(); } }, primary: true);
        later = UiStyle.Button("稍后", Hide); later.Tag = "subtle";
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 70, ColumnCount = 2, Padding = new Padding(32, 14, 32, 14) };
        footer.Paint += (_, e) => { using var pen = new Pen(UiStyle.Line); var inset = 32 * DeviceDpi / 96; e.Graphics.DrawLine(pen, inset, 0, footer.Width - inset, 0); };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        var actions = UiStyle.Actions(later, start); actions.Margin = Padding.Empty; actions.WrapContents = false;
        progress.Anchor = AnchorStyles.Left; progress.Font = UiStyle.Small;
        footer.Controls.Add(progress); footer.Controls.Add(actions);
        Controls.Add(content); Controls.Add(footer);
        FormClosing += (_, e) => { e.Cancel = true; Hide(); Dismissed?.Invoke(); };
        UiStyle.Watch(this); UiStyle.FitToScreen(this); Render();
    }
    internal void SetKey(string value) { key = value; if (Visible) Render(); }
    internal void Update(SetupStatus value)
    {
        // Polled every second: re-render only on a real change so a focused checkbox survives.
        if (value with { RunningClients = status.RunningClients } == status && value.RunningClients.SequenceEqual(status.RunningClients)) return;
        status = value; Render();
    }
    private void Render()
    {
        var scroll = content.AutoScrollPosition;
        UiStyle.Clear(content);
        var column = UiStyle.Column(); column.ColumnStyles.Add(new(SizeType.Percent, 100)); content.Controls.Add(column);
        void Add(Control control) { control.Dock = DockStyle.Top; column.Controls.Add(control); }
        var missing = SetupStatus.RequiredCount - status.RequiredDone;
        var heading = UiStyle.Label(status.Ready ? "准备就绪" : $"还差 {missing} 步就能用", heading: true);
        heading.Font = UiStyle.Title; Add(heading);
        Add(UiStyle.Label("能检测的项目会自动打勾。随时可以关掉，重开工具后从这里继续。", secondary: true));
        var doubao = status.DoubaoDetected
            ? (status.DoubaoRunning ? "已检测到并在运行" : "已安装，尚未运行：请先启动豆包输入法")
            : "未检测到豆包输入法：请先安装并在本机文本框中试一次听写";
        var shared = status.SharedKey;
        var keyRow = shared
            ? Row(status.VoiceKeyReady ? Mark.Done : Mark.Todo, "语音键 · 右 Ctrl 与豆包同键", "按一次打开浮条，再按开始录音，说完再按投送；豆包需要使用右 Ctrl、免按模式。",
                UiStyle.Label("需确认", secondary: true), Check("我已在豆包中把语音键设为右 Ctrl、免按（按一次开始、再按结束）", status.VoiceKeyConfirmed, x => VoiceKeyConfirmed?.Invoke(x)))
            : Row(Mark.Done, $"语音键 · {key}", $"{key} 打开浮条，豆包用它自己的语音键录音。推荐改为与豆包共用右 Ctrl，全程只用一个键。",
                UiStyle.Button("改用右 Ctrl", () => BeginInvoke(() => UseSharedKey?.Invoke())));
        var clients = status.RunningClients.Count == 0 ? "未检测到 UU 远程或远程桌面在运行（可以稍后打开）" : $"检测到 {string.Join("、", status.RunningClients)} 正在运行";
        var trial = status.Calibration ?? "在真实的远端输入框里投送一句测试文字，不启动录音";
        Add(Group(
            Row(status.DoubaoDetected ? Mark.Done : Mark.Todo, "豆包输入法", doubao + "；浮条会自动切换到豆包，无需改系统默认输入法", UiStyle.Label("自动检测", secondary: true)),
            keyRow,
            Row(status.ClipboardConfirmed ? Mark.Done : Mark.Todo, "远程客户端", clients, null,
                Check("我已在远控软件中开启“本地 → 远端”剪贴板同步", status.ClipboardConfirmed, x => ClipboardConfirmed?.Invoke(x))),
            Row(status.Calibration != null ? Mark.Done : Mark.Optional, "试一次 · 可选", trial, UiStyle.Button("试一次", () => BeginInvoke(() => TestDelivery?.Invoke())))));
        progress.Text = $"必需项 {status.RequiredDone} / {SetupStatus.RequiredCount} · 完成后驻留系统托盘";
        start.Enabled = status.Ready;
        UiStyle.Apply(this);
        content.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }
    private static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { AutoSize = true, Text = text, Checked = value, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(470, 0), AccessibleName = text };
        // Deferred: the callback re-renders the page, which disposes this box.
        box.CheckedChanged += (_, _) => { var value = box.Checked; box.FindForm()?.BeginInvoke(() => changed(value)); }; return box;
    }
    internal enum Mark { Done, Todo, Optional }
    private static Control Group(params Control[] rows)
    {
        var group = UiStyle.Group(rows); group.Margin = new Padding(0, 14, 0, 10); return group;
    }
    private static Control Row(Mark mark, string title, string detail, Control? value, Control? extra = null)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Padding = new Padding(12, 14, 14, 14), Margin = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Absolute, 40)); row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.AutoSize));
        var labels = UiStyle.Column();
        var name = UiStyle.Label(title); name.Font = UiStyle.Strong; name.MaximumSize = new Size(470, 0); name.Margin = new Padding(0, 0, 0, 3);
        var note = UiStyle.Label(detail, secondary: true); note.Font = UiStyle.Small; note.MaximumSize = new Size(470, 0); note.Margin = Padding.Empty;
        labels.Controls.Add(name); labels.Controls.Add(note);
        if (extra != null) labels.Controls.Add(extra);
        var indicator = new StatusMark(mark) { Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.Add(indicator, 0, 0); row.Controls.Add(labels, 1, 0);
        if (value != null) { value.Anchor = AnchorStyles.Right; row.Controls.Add(value, 2, 0); }
        return row;
    }
    private sealed class StatusMark : Control
    {
        private readonly Mark mark;
        internal StatusMark(Mark value)
        {
            mark = value; Size = new Size(24, 24); SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AccessibleRole = AccessibleRole.Graphic; AccessibleName = value switch { Mark.Done => "已完成", Mark.Todo => "待完成", _ => "可选" };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? UiStyle.Surface); g.SmoothingMode = SmoothingMode.AntiAlias;
            var s = DeviceDpi / 96f; var r = new RectangleF(1.5f * s, 1.5f * s, 20 * s, 20 * s);
            if (mark == Mark.Done)
            {
                using (var brush = new SolidBrush(UiStyle.Green)) g.FillEllipse(brush, r);
                using var pen = new Pen(Color.White, 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, [new PointF(r.X + 5.5f * s, r.Y + 10.5f * s), new PointF(r.X + 9 * s, r.Y + 14 * s), new PointF(r.X + 15 * s, r.Y + 7 * s)]);
                return;
            }
            using var ring = new Pen(UiStyle.StrongLine, 1.6f * s);
            if (mark == Mark.Optional) ring.DashStyle = DashStyle.Dash;
            g.DrawEllipse(ring, r);
        }
    }
}
