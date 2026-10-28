using System.Drawing.Drawing2D;

namespace RemoteVoiceInput.Windows;

internal enum HudTone { Info, Success, Warning, Test }
internal sealed record HudAction(string Title, Action Run, bool Primary = false);
internal sealed record HudOption(string Title, string Detail, HudAction Action);

// Bottom notice that never takes focus: WS_EX_NOACTIVATE plus MA_NOACTIVATE, and
// painted buttons instead of focusable controls, so clicking "再贴一次" leaves
// the remote client in front. Every press that cannot start or deliver ends here.
internal sealed class StatusHud : Form
{
    internal const int LogicalWidth = 440;
    private readonly System.Windows.Forms.Timer dismiss = new();
    private readonly List<(RectangleF Bounds, HudAction Action)> hits = [];
    private readonly List<(RectangleF Bounds, string Title, string Detail)> optionBoxes = [];
    private string heading = "", detail = "";
    private HudTone tone;
    private HudAction[] actions = [];
    private HudOption[] options = [];
    private RectangleF iconBounds, textBounds;
    private float titleHeight;
    private int hover = -1;
    internal string Heading => heading;
    internal string Detail => detail;
    internal bool IsSticky { get; private set; }
    internal IEnumerable<string> ActionTitles => hits.Select(x => x.Action.Title);
    // A visible floatbar or review card to stay clear of (the notice goes above it).
    internal Func<Rectangle?> Avoid = () => null;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; p.ClassStyle |= 0x00020000; return p; } // NOACTIVATE | TOOLWINDOW | TOPMOST; drop shadow
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x21) { message.Result = 3; return; } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        base.WndProc(ref message);
    }
    internal StatusHud()
    {
        Text = $"{ProductInfo.Name} · 状态"; AccessibleName = "远程语音输入状态";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true; ClientSize = new Size(LogicalWidth, 56);
        dismiss.Tick += (_, _) => HideHud();
        UiStyle.Watch(this);
    }
    private float UiScale => UiStyle.ScaleOf(this);
    private Font TitleFont(bool strong) => UiStyle.PixelFont(13, UiScale, strong ? FontStyle.Bold : FontStyle.Regular);
    private Font DetailFont => UiStyle.PixelFont(11.5f, UiScale);
    private Font ButtonFont => UiStyle.PixelFont(12, UiScale);
    private bool Pill => detail.Length == 0 && options.Length == 0;
    // duration null keeps the notice until an action, a new notice or HideHud().
    internal void ShowHud(string title, string? message = null, HudTone kind = HudTone.Info, HudAction[]? buttons = null, HudOption[]? choices = null, double? duration = 3)
    {
        dismiss.Stop();
        heading = title; detail = message ?? ""; tone = kind; actions = buttons ?? []; options = choices ?? [];
        AccessibleDescription = detail; hover = -1;
        Arrange();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var top = area.Bottom - Height - (int)(72 * UiScale);
        if (Avoid() is { } occupied && occupied.Top < top + Height) top = Math.Max(area.Top, occupied.Top - Height - (int)(10 * UiScale));
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), top);
        if (!Visible) Show(); else Invalidate();
        Native.SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // keep above the floatbar's TopMost peers, no activation
        IsSticky = duration == null;
        if (duration is double seconds) { dismiss.Interval = Math.Max(1, (int)(seconds * 1000)); dismiss.Start(); }
    }
    internal void HideHud() { dismiss.Stop(); IsSticky = false; Hide(); }
    // Preview / accessibility: run a visible button by its title, like a click.
    internal bool Press(string title)
    {
        var hit = hits.FirstOrDefault(x => x.Action.Title == title);
        if (hit.Action == null || !ClientRectangle.Contains(Rectangle.Round(hit.Bounds))) return false;
        HideHud(); hit.Action.Run(); return true;
    }
    private float ButtonWidth(Graphics g, HudAction action) => TextRenderer.MeasureText(g, action.Title, ButtonFont, Size.Empty, TextFormatFlags.NoPadding).Width + 28 * UiScale;
    private void Arrange()
    {
        var s = UiScale; var width = LogicalWidth * s;
        hits.Clear(); optionBoxes.Clear();
        using var g = CreateGraphics();
        var pill = Pill;
        float inset = pill ? 0 : 14 * s, left = (pill ? 18 : 16) * s, right = (pill ? 10 : 14) * s, button = 32 * s, gap = 8 * s;
        var buttonsWidth = actions.Sum(x => ButtonWidth(g, x) + gap);
        var leading = (tone == HudTone.Test ? 38 : 20) * s;
        var textLeft = left + leading + 11 * s;
        var textWidth = Math.Max(80 * s, width - textLeft - right - buttonsWidth);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        titleHeight = pill ? TitleFont(false).Height : TextRenderer.MeasureText(g, heading, TitleFont(true), new Size((int)textWidth, 0), flags).Height;
        var detailHeight = detail.Length == 0 ? 0 : TextRenderer.MeasureText(g, detail, DetailFont, new Size((int)textWidth, 0), flags).Height + 3 * s;
        var block = Math.Max(titleHeight + detailHeight, actions.Length > 0 ? button : 0);
        var top = pill ? 0 : inset;
        var rowHeight = pill ? 56 * s : block;
        textBounds = new RectangleF(textLeft, top + (rowHeight - titleHeight - detailHeight) / 2, textWidth, titleHeight + detailHeight);
        iconBounds = new RectangleF(left, top + (pill ? rowHeight / 2 : titleHeight / 2 + textBounds.Top - top) - (tone == HudTone.Test ? 10 : 10) * s, leading, 20 * s);
        var x = width - right;
        foreach (var action in actions.Reverse())
        {
            var w = ButtonWidth(g, action); x -= w;
            hits.Insert(0, (new RectangleF(x, top + (rowHeight - button) / 2, w, button), action)); x -= gap;
        }
        var y = top + rowHeight;
        foreach (var option in options)
        {
            y += 10 * s;
            var bw = ButtonWidth(g, option.Action);
            var innerWidth = width - inset * 2 - 20 * s - bw - 10 * s;
            var t = TextRenderer.MeasureText(g, option.Title, UiStyle.PixelFont(12, s, FontStyle.Bold), new Size((int)innerWidth, 0), flags).Height;
            var d = TextRenderer.MeasureText(g, option.Detail, DetailFont, new Size((int)innerWidth, 0), flags).Height;
            var h = Math.Max(t + d + 2 * s, button) + 20 * s;
            var box = new RectangleF(inset, y, width - inset * 2, h);
            optionBoxes.Add((box, option.Title, option.Detail));
            hits.Add((new RectangleF(box.Right - 10 * s - bw, box.Top + (h - button) / 2, bw, button), option.Action));
            y += h;
        }
        var height = pill ? 56 * s : y + inset;
        ClientSize = new Size((int)Math.Ceiling(width), (int)Math.Ceiling(height));
        using var path = DesignGroup.Round(new RectangleF(0, 0, Width, Height), (pill ? 28 : 16) * s);
        Region = new Region(path);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var s = UiScale; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = tone == HudTone.Warning ? UiStyle.AmberSoft : UiStyle.Surface;
        var stroke = tone == HudTone.Warning ? UiStyle.AmberLine : tone == HudTone.Test ? UiStyle.Accent : UiStyle.StrongLine;
        g.Clear(fill);
        using (var path = DesignGroup.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), (Pill ? 28 : 16) * s))
        using (var pen = new Pen(stroke, Math.Max(1, s))) g.DrawPath(pen, path);
        PaintIcon(g, s);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | (Pill ? TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter : 0);
        var titleRect = Rectangle.Round(new RectangleF(textBounds.X, textBounds.Y, textBounds.Width, titleHeight));
        TextRenderer.DrawText(g, heading, TitleFont(!Pill), titleRect, tone == HudTone.Warning ? UiStyle.Amber : UiStyle.Ink, flags);
        if (detail.Length > 0)
            TextRenderer.DrawText(g, detail, DetailFont, Rectangle.Round(new RectangleF(textBounds.X, textBounds.Y + titleHeight + 3 * s, textBounds.Width, textBounds.Height - titleHeight)), UiStyle.Secondary, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        foreach (var (box, title, text) in optionBoxes)
        {
            using (var path = DesignGroup.Round(box, 9 * s)) using (var brush = new SolidBrush(UiStyle.Surface)) g.FillPath(brush, path);
            var bw = hits.First(x => x.Bounds.Top >= box.Top && x.Bounds.Bottom <= box.Bottom).Bounds.Width;
            var inner = new RectangleF(box.X + 10 * s, box.Y + 10 * s, box.Width - 30 * s - bw, box.Height - 20 * s);
            var bold = UiStyle.PixelFont(12, s, FontStyle.Bold);
            var th = TextRenderer.MeasureText(g, title, bold, new Size((int)inner.Width, 0), TextFormatFlags.WordBreak).Height;
            var dh = TextRenderer.MeasureText(g, text, DetailFont, new Size((int)inner.Width, 0), TextFormatFlags.WordBreak).Height;
            var y = inner.Y + (inner.Height - th - dh - 2 * s) / 2;
            TextRenderer.DrawText(g, title, bold, Rectangle.Round(new RectangleF(inner.X, y, inner.Width, th)), UiStyle.Ink, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, text, DetailFont, Rectangle.Round(new RectangleF(inner.X, y + th + 2 * s, inner.Width, dh)), UiStyle.Secondary, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        for (var i = 0; i < hits.Count; i++)
        {
            var (bounds, action) = hits[i];
            var back = action.Primary ? UiStyle.Accent : UiStyle.Surface;
            if (i == hover) back = UiStyle.Blend(UiStyle.Ink, back, .07f);
            using var path = DesignGroup.Round(bounds, bounds.Height / 2);
            using (var brush = new SolidBrush(back)) g.FillPath(brush, path);
            if (!action.Primary) { using var pen = new Pen(UiStyle.StrongLine, Math.Max(1, s)); g.DrawPath(pen, path); }
            TextRenderer.DrawText(g, action.Title, ButtonFont, Rectangle.Round(bounds), action.Primary ? UiStyle.AccentInk : UiStyle.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
    private void PaintIcon(Graphics g, float s)
    {
        var r = iconBounds;
        if (tone == HudTone.Test)
        {
            var chip = new RectangleF(r.X, r.Y, 38 * s, 20 * s);
            using (var path = DesignGroup.Round(chip, 9 * s)) using (var brush = new SolidBrush(UiStyle.Accent)) g.FillPath(brush, path);
            TextRenderer.DrawText(g, "测试", UiStyle.PixelFont(11, s, FontStyle.Bold), Rectangle.Round(chip), UiStyle.AccentInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return;
        }
        var circle = new RectangleF(r.X + 1 * s, r.Y + 1 * s, 18 * s, 18 * s);
        var color = tone == HudTone.Success ? UiStyle.Green : tone == HudTone.Warning ? UiStyle.Amber : UiStyle.Accent;
        if (tone == HudTone.Success)
        {
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, circle);
            using var pen = new Pen(Color.White, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(circle.X + 5 * s, circle.Y + 9.5f * s), new PointF(circle.X + 8 * s, circle.Y + 12.5f * s), new PointF(circle.X + 13.5f * s, circle.Y + 6 * s)]);
            return;
        }
        using (var pen = new Pen(color, 1.6f * s)) g.DrawEllipse(pen, circle);
        using var mark = new SolidBrush(color);
        if (tone == HudTone.Warning)
        {
            g.FillRectangle(mark, circle.X + 8.2f * s, circle.Y + 4 * s, 1.6f * s, 6.5f * s);
            g.FillEllipse(mark, circle.X + 8 * s, circle.Y + 12 * s, 2 * s, 2 * s);
        }
        else
        {
            g.FillEllipse(mark, circle.X + 8 * s, circle.Y + 4 * s, 2 * s, 2 * s);
            g.FillRectangle(mark, circle.X + 8.2f * s, circle.Y + 7.5f * s, 1.6f * s, 6.5f * s);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = hits.FindIndex(x => x.Bounds.Contains(e.Location));
        if (index != hover) { hover = index; Cursor = index >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = hits.FirstOrDefault(x => x.Bounds.Contains(e.Location));
        if (hit.Action == null) return;
        HideHud(); hit.Action.Run();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); if (Visible) Arrange(); }
    protected override void Dispose(bool disposing) { if (disposing) dismiss.Dispose(); base.Dispose(disposing); }
}
