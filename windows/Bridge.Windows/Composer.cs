using System.Drawing.Drawing2D;
using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

internal sealed class CaptureTextBox : TextBox
{
    private bool composing;
    private bool suppressEnterCharacter;
    internal Action? Primary;
    internal Action? CancelCapture;
    // Every content change, committed text or IME composition, lands here: Doubao
    // streams recognition as a composition, which raises no TextChanged.
    internal Action? ContentChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool SubmitOnEnter { get; set; } = true;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Placeholder { get; set; } = "";
    internal bool IsComposing
    {
        get
        {
            if (composing) return true;
            if (!IsHandleCreated) return false;
            var context = Native.ImmGetContext(Handle);
            if (context == 0) return false;
            try { return Native.ImmGetCompositionString(context, 8, 0, 0) > 0; }
            finally { Native.ImmReleaseContext(Handle, context); }
        }
    }
    // The hint is painted inside the edit control and never while text or an
    // IME composition exists, so it cannot overlap recognized text.
    internal bool PlaceholderVisible => Placeholder.Length > 0 && TextLength == 0 && !IsComposing;
    // The edit control lays lines out at the GDI text height, which can differ from Font.Height.
    internal int LineHeight => TextRenderer.MeasureText("中Ag", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
    internal int LineCount => IsHandleCreated ? Math.Max(1, (int)Native.SendMessage(Handle, 0xba, 0, 0)) : 1; // EM_GETLINECOUNT, wrapped lines
    // Doubao keeps one IME composition open for a whole utterance; that text is
    // not in the edit buffer yet. Read only to size the floatbar, never logged.
    // Preview only: stands in for an IME composition that cannot be produced offline.
    internal Func<string>? SimulatedComposition;
    internal string CompositionText
    {
        get
        {
            if (SimulatedComposition != null) return SimulatedComposition();
            if (!IsHandleCreated) return "";
            var context = Native.ImmGetContext(Handle);
            if (context == 0) return "";
            try
            {
                var bytes = Native.ImmGetCompositionString(context, 8, 0, 0); // GCS_COMPSTR
                if (bytes <= 0) return "";
                var buffer = new byte[bytes];
                return Native.ImmGetCompositionText(context, 8, buffer, (uint)bytes) > 0 ? System.Text.Encoding.Unicode.GetString(buffer) : "";
            }
            finally { Native.ImmReleaseContext(Handle, context); }
        }
    }
    // Lines needed for committed text plus the open composition, at the edit width.
    internal int NeededLines()
    {
        if (!IsHandleCreated) return 1;
        var composition = CompositionText;
        var lines = TextLength == 0 ? 1 : LineCount;
        if (composition.Length == 0) return lines;
        var text = Text;
        // Inline (CUAS) composition may already sit in the buffer; never count it twice.
        var full = text.Contains(composition, StringComparison.Ordinal) ? text : text.Insert(Math.Min(SelectionStart, text.Length), composition);
        Native.SendRect(Handle, 0xb2, 0, out var area); // EM_GETRECT: the formatting rectangle
        var width = Math.Max(1, area.Right - area.Left);
        var height = TextRenderer.MeasureText(full, Font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
        return Math.Max(lines, (int)Math.Ceiling(height / (double)Math.Max(1, LineHeight)));
    }
    // After the receiver grows, give the IME the new area so a wrapping composition
    // stays inside the floatbar instead of the old one-line rectangle.
    internal void UpdateCompositionArea()
    {
        if (!IsHandleCreated || !IsComposing) return;
        var context = Native.ImmGetContext(Handle);
        if (context == 0) return;
        try
        {
            Native.SendRect(Handle, 0xb2, 0, out var area);
            Native.GetCaretPos(out var caret);
            var form = new Native.CompositionForm { Style = 1, Position = caret, Area = area }; // CFS_RECT
            Native.ImmSetCompositionWindow(context, ref form);
        }
        finally { Native.ImmReleaseContext(Handle, context); }
    }
    internal void ScrollToEnd() { if (IsHandleCreated) Native.SendMessage(Handle, 0x115, 7, 0); } // WM_VSCROLL SB_BOTTOM, caret untouched
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); ContentChanged?.Invoke(); }
    protected override bool IsInputKey(Keys keyData)
    {
        if (SubmitOnEnter && (keyData & Keys.KeyCode) == Keys.Enter && (keyData & Keys.Shift) == 0 &&
            Native.GetMessageExtraInfo() != Native.Marker && !IsComposing) return false;
        return base.IsInputKey(keyData);
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (SubmitOnEnter && (keyData & Keys.KeyCode) == Keys.Enter && (keyData & Keys.Shift) == 0 &&
            Native.GetMessageExtraInfo() != Native.Marker && !IsComposing)
        { Primary?.Invoke(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }
    private static void TraceInput(Message message)
    {
        // Metadata only: message kind, key code for Alt/Enter/Esc, peer process.
        switch (message.Msg)
        {
            case 0x7: Diagnostics.Write("receiver: focus gained"); break;
            case 0x8: Diagnostics.Write("receiver: focus lost to " + Native.ProcessOf(message.WParam)); break;
            case 0x10d: Diagnostics.Write("receiver: IME composition start"); break;
            case 0x10e: Diagnostics.Write("receiver: IME composition end"); break;
            case 0x100 or 0x101 or 0x104 or 0x105 when (int)message.WParam is 0x11 or 0x12 or 0x0d or 0x1b:
                Diagnostics.Write($"receiver: key 0x{(int)message.WParam:x2} {(message.Msg is 0x100 or 0x104 ? "down" : "up")}{(message.Msg >= 0x104 ? " sys" : "")} scan=0x{((long)message.LParam >> 16) & 0xff:x2} ext={((long)message.LParam >> 24) & 1} injected={Native.GetMessageExtraInfo() == Native.Marker}"); break;
        }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x10d) composing = true; // WM_IME_STARTCOMPOSITION
        if (message.Msg == 0x10e) composing = false; // WM_IME_ENDCOMPOSITION, not an ASR final signal
        if (Visible) TraceInput(message);
        // Releasing Alt (Doubao's voice key) must never enter keyboard menu mode,
        // which would take focus from the IME receiver and end recording.
        if (message.Msg == 0x112 && ((int)message.WParam & 0xfff0) == 0xf100) { Diagnostics.Write("receiver: blocked Alt menu mode"); return; }
        var bridgeKey = Native.GetMessageExtraInfo() == Native.Marker;
        if (message.Msg == 0x102 && message.WParam == 13 && (bridgeKey || suppressEnterCharacter))
        { suppressEnterCharacter = false; return; }
        if (message.Msg == 0x101 && message.WParam == 13) suppressEnterCharacter = false;
        if (message.Msg == 0x100 && !bridgeKey && !IsComposing)
        {
            if (SubmitOnEnter && message.WParam == 13 && (ModifierKeys & Keys.Shift) == 0)
            { suppressEnterCharacter = true; Primary?.Invoke(); return; }
            if (message.WParam == 27) { CancelCapture?.Invoke(); return; }
        }
        base.WndProc(ref message);
        if (message.Msg is 0x10d or 0x10e or 0x10f) { Invalidate(); ContentChanged?.Invoke(); } // start / end / WM_IME_COMPOSITION
        if (message.Msg == 0x0f && PlaceholderVisible) PaintPlaceholder();
    }
    private void PaintPlaceholder()
    {
        using var g = Graphics.FromHwnd(Handle);
        var rect = ClientRectangle; rect.Inflate(-2, 0);
        TextRenderer.DrawText(g, Placeholder, Font, rect, UiStyle.Secondary, BackColor,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

// Painted, never focusable: clicking it leaves keyboard focus in the IME receiver.
internal sealed class PaintedButton : Control
{
    internal enum Look { Tinted, Subtle, Primary, KeyCap }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] internal Look Appearance { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] internal bool Clickable { get; set; } = true;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] internal float Radius { get; set; } = -1;
    internal event Action? Pressed;
    private bool hover;
    internal PaintedButton(string text, Look look)
    {
        Text = text; Appearance = look; AccessibleName = text;
        AccessibleRole = look == Look.KeyCap ? AccessibleRole.StaticText : AccessibleRole.PushButton;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = Clickable; Cursor = Clickable && Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (Clickable && Enabled && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Pressed?.Invoke();
    }
    internal void PerformPress() { if (Clickable && Enabled && Visible) Pressed?.Invoke(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var parent = Parent?.BackColor ?? UiStyle.Surface;
        g.Clear(parent);
        var (fill, ink) = Appearance switch
        {
            Look.Tinted => (UiStyle.AccentSoft, UiStyle.Accent),
            Look.Primary or Look.KeyCap => (UiStyle.Accent, UiStyle.AccentInk),
            _ => (parent, UiStyle.Secondary)
        };
        if (!Enabled) { fill = UiStyle.Blend(fill, parent, .5f); ink = UiStyle.Blend(ink, fill, .55f); }
        if (hover && Enabled) fill = UiStyle.Blend(UiStyle.Ink, fill, .07f);
        var radius = Radius >= 0 ? Radius : Height / 2f;
        if (Appearance != Look.Subtle || hover)
        {
            using var path = DesignGroup.Round(new RectangleF(0, 0, Width - 1, Height - 1), radius);
            using var brush = new SolidBrush(fill); g.FillPath(brush, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

// One window, two forms around the same visible IME receiver: a one-line capsule
// while dictating (grows upward to 8 lines, newest text in view) and a review
// card. Switching never replaces or reparents the CaptureTextBox.
internal sealed class ComposerForm : Form
{
    internal const int LogicalWidth = 600, MaxCompactLines = 8;
    internal readonly CaptureTextBox Editor = new() { Multiline = true, WordWrap = true, AcceptsReturn = true, AcceptsTab = true,
        ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None, AccessibleName = "本地听写文字", MaxLength = 1_000_000 };
    private readonly PaintedButton chip = new("右 Ctrl 结束", PaintedButton.Look.Tinted);
    private readonly PaintedButton cancel = new("×", PaintedButton.Look.Subtle) { AccessibleName = "取消本次投送" };
    private readonly PaintedButton collapse = new("收起", PaintedButton.Look.Subtle) { AccessibleName = "展开或收起编辑区" };
    private readonly PaintedButton copy = new("复制原文", PaintedButton.Look.Subtle);
    private readonly PaintedButton keyCap = new("右 Ctrl 投送", PaintedButton.Look.KeyCap) { Clickable = false };
    private readonly PaintedButton closeCancelled = new("已结束，关闭", PaintedButton.Look.Primary);
    private readonly Label title = new() { AutoSize = false, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label target = new() { AutoSize = false, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Tag = "secondary" };
    private readonly Label note = new() { AutoSize = false, UseMnemonic = false, AutoEllipsis = true };
    private readonly Label hint = new() { AutoSize = false, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Tag = "secondary" };
    private RectangleF editorSurface, noteSurface, iconBounds;
    private bool expanded = true, cancelling;
    private string message = "", chipOverride = "";
    private CapturePhase phase;
    private int compactLines = 1;
    internal Action? Primary;
    internal Action? CancelCapture;
    internal Action? Changed;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Key { get; set; } = "F8";
    internal bool IsCard => phase == CapturePhase.Review || cancelling;
    internal int CompactLines => compactLines;
    internal string Message => message;
    internal bool OwnsInput => Visible && Native.IsOwnForeground && Native.GetFocus() == Editor.Handle;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ClassStyle |= 0x00020000; return p; } }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x112 && ((int)message.WParam & 0xfff0) == 0xf100) { Diagnostics.Write("floatbar: blocked Alt menu mode"); return; }
        if (Visible && message.Msg == 0x6) Diagnostics.Write(((int)message.WParam & 0xffff) == 0 ? "floatbar: deactivated, now " + Native.ProcessOf(message.LParam) : "floatbar: activated");
        if (Visible && message.Msg == 0x211) Diagnostics.Write("floatbar: entered menu loop");
        base.WndProc(ref message);
    }

    internal ComposerForm()
    {
        Text = $"{ProductInfo.Name} · 浮条"; AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; ClientSize = new Size(LogicalWidth, 56);
        Controls.AddRange([Editor, chip, cancel, collapse, copy, keyCap, closeCancelled, title, target, note, hint]);
        Editor.Primary = PerformPrimary; Editor.CancelCapture = () => CancelCapture?.Invoke();
        Editor.ContentChanged = ContentChanged;
        chip.Pressed += PerformPrimary;
        cancel.Pressed += () => CancelCapture?.Invoke();
        closeCancelled.Pressed += () => CancelCapture?.Invoke();
        collapse.Pressed += () => { SetExpanded(!expanded); FocusInput(); };
        copy.Pressed += CopyText;
        foreach (var handle in new Control[] { this, title, target })
            handle.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xa1, 2, 0); } };
        FormClosing += (_, e) => { e.Cancel = true; CancelCapture?.Invoke(); };
        UiStyle.Watch(this);
        SetPhase(CapturePhase.Listening, "");
    }
    private float UiScale => UiStyle.ScaleOf(this);
    private void ApplyFonts()
    {
        var s = UiScale;
        Editor.Font = UiStyle.PixelFont(14.5f, s);
        chip.Font = keyCap.Font = closeCancelled.Font = UiStyle.PixelFont(12, s, FontStyle.Bold);
        copy.Font = hint.Font = target.Font = UiStyle.PixelFont(11.5f, s);
        title.Font = UiStyle.PixelFont(13, s, FontStyle.Bold);
        note.Font = UiStyle.PixelFont(11.5f, s);
        cancel.Font = UiStyle.PixelFont(17, s); collapse.Font = UiStyle.PixelFont(11.5f, s);
    }
    private void PerformPrimary()
    {
        // Review submission belongs to the global key only: a mouse click or Return
        // must not steal the remote client's focus right before the paste.
        if (IsCard) return;
        Primary?.Invoke();
    }
    private void CopyText()
    {
        if (TextPolicy.IsEmpty(Editor.Text)) return;
        try { Clipboard.SetText(Editor.Text); SetMessage("已复制原文，可手动粘贴到所需位置；不会自动投送。"); }
        catch (System.Runtime.InteropServices.ExternalException) { SetMessage("剪贴板暂时被占用，原文仍保留，请稍后再复制。"); }
        if (Native.IsOwnForeground) Editor.Focus();
    }
    private void ContentChanged()
    {
        Fit();
        Changed?.Invoke();
    }
    // The full layout runs only when the capsule needs a different height.
    private void Fit()
    {
        if (IsCard) { Editor.Invalidate(); return; }
        var lines = Math.Min(MaxCompactLines, Editor.NeededLines());
        if (lines != compactLines) { Diagnostics.Write($"floatbar: lines {compactLines} -> {lines} composing={Editor.IsComposing}"); Relayout(); }
        else if (!Editor.IsComposing) Editor.ScrollToEnd();
    }
    // Called by the app poll while composing: not every IME update sends a message.
    internal void RefreshContent() { if (Visible) Fit(); }
    internal void Present(string text, string destination, bool review)
    {
        cancelling = false; message = ""; chipOverride = "";
        Editor.ReadOnly = false; Editor.Text = text; target.Text = "→ " + destination;
        expanded = true; compactLines = 1;
        SetPhase(review ? CapturePhase.Review : CapturePhase.Listening, review ? $"核对正文与目标后，按 {Key} 投送。" : "");
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Bottom - Height - (int)(72 * UiScale));
        Show(); FocusInput();
    }
    internal void FocusInput()
    {
        if (!Native.BringToForeground(Handle)) Activate();
        Editor.Focus(); if (!Editor.IsComposing) Editor.SelectionStart = Editor.TextLength;
    }
    internal void SetExpanded(bool value) { expanded = value; Relayout(); }
    // Pill: the message is the placeholder of an empty receiver. Card: the amber note.
    internal void SetMessage(string value) { message = value; Relayout(); }
    // Replaces the capsule key chip text, e.g. "再按 右 Ctrl 结束".
    internal void SetChip(string value) { chipOverride = value; Relayout(); }
    internal void SetPhase(CapturePhase value, string text)
    {
        phase = value; message = text; chipOverride = ""; cancelling = false;
        if (value == CapturePhase.Review) expanded = true;
        Relayout();
    }
    internal void SetCancelling(string text)
    {
        cancelling = true; message = text; expanded = true;
        Relayout();
    }
    internal void ClearAndHide() { Hide(); Editor.Clear(); cancelling = false; message = ""; chipOverride = ""; compactLines = 1; }
    internal void Relayout()
    {
        var s = UiScale; ApplyFonts();
        var card = IsCard;
        var width = (int)Math.Round(LogicalWidth * s);
        var bottom = Bottom;
        Editor.SubmitOnEnter = !card;
        Editor.Placeholder = card ? "" : message.Length > 0 ? message : phase == CapturePhase.Listening ? "请说话…" : "";
        chip.Visible = cancel.Visible = !card;
        title.Visible = target.Visible = collapse.Visible = hint.Visible = card;
        keyCap.Visible = copy.Visible = card && !cancelling;
        closeCancelled.Visible = card && cancelling;
        note.Visible = card && message.Length > 0;
        var line = Editor.LineHeight;
        int height;
        if (!card)
        {
            chip.Text = chipOverride.Length > 0 ? chipOverride : phase == CapturePhase.Settling ? "整理中…" : $"{Key} 结束";
            chip.Enabled = phase == CapturePhase.Listening;
            chip.Size = new Size(TextRenderer.MeasureText(chip.Text, chip.Font).Width + (int)(22 * s), (int)(34 * s));
            cancel.Size = new Size((int)(26 * s), (int)(26 * s));
            var cancelX = width - (int)(10 * s) - cancel.Width;
            var chipX = cancelX - (int)(10 * s) - chip.Width;
            var textX = (int)((18 + 22 + 11) * s);
            var textWidth = Math.Max((int)(80 * s), chipX - (int)(12 * s) - textX);
            Editor.BackColor = UiStyle.Surface;
            Editor.SetBounds(textX, Editor.Top, textWidth, Math.Max(Editor.Height, line));
            compactLines = Math.Min(MaxCompactLines, Editor.NeededLines());
            var textHeight = compactLines * line;
            height = Math.Max((int)(56 * s), textHeight + (int)(22 * s));
            Editor.SetBounds(textX, (height - textHeight) / 2, textWidth, textHeight);
            var multi = compactLines > 1;
            var controlsCenter = multi ? height - (int)(28 * s) : height / 2;
            chip.Location = new Point(chipX, controlsCenter - chip.Height / 2);
            cancel.Location = new Point(cancelX, controlsCenter - cancel.Height / 2);
            iconBounds = new RectangleF(18 * s, controlsCenter - 11 * s, 22 * s, 22 * s);
            Native.ShowScrollBar(Editor.Handle, 1, false);
            if (!Editor.IsComposing) Editor.ScrollToEnd();
            Editor.UpdateCompositionArea();
        }
        else
        {
            var pad = (int)(16 * s);
            title.Text = cancelling ? "已取消 · 等待豆包结束录音" : "核对后投送";
            title.Width = TextRenderer.MeasureText(title.Text, title.Font).Width + (int)(4 * s);
            title.SetBounds(pad, (int)(12 * s), title.Width, (int)(26 * s));
            collapse.Text = expanded ? "收起" : "展开";
            collapse.SetBounds(width - pad - (int)(44 * s), (int)(13 * s), (int)(44 * s), (int)(24 * s));
            target.SetBounds(title.Right + (int)(6 * s), (int)(12 * s), Math.Max(0, collapse.Left - title.Right - (int)(12 * s)), (int)(26 * s));
            var surfaceTop = (int)(48 * s);
            var innerX = pad + (int)(10 * s); var innerWidth = width - pad * 2 - (int)(20 * s);
            Editor.BackColor = UiStyle.Soft;
            Editor.SetBounds(innerX, surfaceTop + (int)(8 * s), innerWidth, Math.Max(Editor.Height, line));
            var lines = Editor.LineCount;
            // Whole lines only, so the last visible line is never cut in half.
            var inner = expanded ? Math.Clamp(lines * line, (int)(124 * s), (int)(304 * s)) / line * line : line;
            var surfaceHeight = inner + (int)(16 * s);
            editorSurface = new RectangleF(pad, surfaceTop, width - pad * 2, surfaceHeight);
            Editor.SetBounds(innerX, surfaceTop + (int)(8 * s), innerWidth, surfaceHeight - (int)(16 * s));
            // Changing ScrollBars would recreate the edit window (and the IME context): toggle the bar instead.
            Native.ShowScrollBar(Editor.Handle, 1, lines * line > Editor.Height);
            var y = surfaceTop + surfaceHeight + (int)(10 * s);
            if (note.Visible)
            {
                note.Text = message;
                var noteWidth = width - pad * 2 - (int)(18 * s);
                var measured = TextRenderer.MeasureText(message, note.Font, new Size(noteWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                var noteHeight = Math.Min(measured, note.Font.Height * 4);
                note.SetBounds(pad + (int)(9 * s), y + (int)(9 * s), noteWidth, noteHeight);
                noteSurface = new RectangleF(pad, y, width - pad * 2, noteHeight + 18 * s);
                y += (int)noteSurface.Height + (int)(10 * s);
            }
            else noteSurface = RectangleF.Empty;
            var footer = (int)(30 * s);
            var action = cancelling ? closeCancelled : keyCap;
            action.Text = cancelling ? "已结束，关闭" : $"{Key} 投送";
            action.Size = new Size(TextRenderer.MeasureText(action.Text, action.Font).Width + (int)(26 * s), footer);
            action.Location = new Point(width - pad - action.Width, y);
            copy.Size = new Size(TextRenderer.MeasureText(copy.Text, copy.Font).Width + (int)(20 * s), footer);
            copy.Location = new Point(action.Left - (int)(8 * s) - copy.Width, y);
            hint.Text = cancelling ? "Esc 立即关闭" : "Return 换行 · Esc 取消";
            hint.SetBounds(pad, y, Math.Max(0, (cancelling ? action.Left : copy.Left) - pad - (int)(8 * s)), footer);
            height = y + footer + pad;
        }
        note.BackColor = UiStyle.AmberSoft; note.ForeColor = UiStyle.Amber;
        ClientSize = new Size(width, height);
        if (Visible && bottom > 0) Top = bottom - Height;
        var radius = (card ? 16 : compactLines > 1 ? 20 : 28) * s;
        using (var path = DesignGroup.Round(new RectangleF(0, 0, Width, Height), radius)) Region = new Region(path);
        Invalidate(); Editor.Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var s = UiScale; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var card = IsCard;
        if (card)
        {
            using (var path = DesignGroup.Round(editorSurface, 8 * s)) using (var brush = new SolidBrush(UiStyle.Soft)) g.FillPath(brush, path);
            if (!noteSurface.IsEmpty) { using var path = DesignGroup.Round(noteSurface, 7 * s); using var brush = new SolidBrush(UiStyle.AmberSoft); g.FillPath(brush, path); }
        }
        else PaintIcon(g, s);
        using var border = DesignGroup.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), (card ? 16 : compactLines > 1 ? 20 : 28) * s);
        using var pen = new Pen(UiStyle.StrongLine, Math.Max(1, s)); g.DrawPath(pen, border);
    }
    private void PaintIcon(Graphics g, float s)
    {
        var r = iconBounds;
        var color = phase == CapturePhase.Listening ? UiStyle.Accent : UiStyle.Secondary;
        using var pen = new Pen(color, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (phase == CapturePhase.Settling)
        {
            for (var i = 0; i < 3; i++) { using var brush = new SolidBrush(color); g.FillEllipse(brush, r.X + (3 + i * 6) * s, r.Y + 9.5f * s, 3.2f * s, 3.2f * s); }
            return;
        }
        float[] bars = [5, 11, 16, 9, 13, 6];
        for (var i = 0; i < bars.Length; i++)
        {
            var x = r.X + (1.5f + i * 3.6f) * s; var h = bars[i] * s;
            g.DrawLine(pen, x, r.Y + r.Height / 2 - h / 2, x, r.Y + r.Height / 2 + h / 2);
        }
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); }
}
