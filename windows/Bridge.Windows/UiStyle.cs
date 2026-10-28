using Microsoft.Win32;
using System.Drawing.Drawing2D;

namespace RemoteVoiceInput.Windows;

internal static class UiStyle
{
    internal static Icon CreateAppIcon(int size = 32)
    {
        using var stream = typeof(UiStyle).Assembly.GetManifestResourceStream("AppIcon.ico")
            ?? throw new InvalidOperationException("Missing application icon resource");
        using var icon = new Icon(stream, new Size(size, size));
        return (Icon)icon.Clone();
    }
    internal enum TrayState { Ready, Listening, Delivering, Attention, Paused }
    // The app icon with a small state badge; paused is also desaturated.
    internal static Icon CreateTrayIcon(TrayState state = TrayState.Ready)
    {
        var size = SystemInformation.SmallIconSize.Width * 2;
        using var app = CreateAppIcon(size);
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (state == TrayState.Paused)
            {
                using var source = app.ToBitmap();
                var matrix = new System.Drawing.Imaging.ColorMatrix([[.3f, .3f, .3f, 0, 0], [.59f, .59f, .59f, 0, 0], [.11f, .11f, .11f, 0, 0], [0, 0, 0, .7f, 0], [0, 0, 0, 0, 1]]);
                using var attributes = new System.Drawing.Imaging.ImageAttributes(); attributes.SetColorMatrix(matrix);
                g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel, attributes);
            }
            else g.DrawIcon(app, new Rectangle(0, 0, size, size));
            if (state != TrayState.Ready)
            {
                var badge = new RectangleF(size * .5f, size * .5f, size * .5f - 1, size * .5f - 1);
                var color = state switch { TrayState.Listening => Color.FromArgb(18, 99, 214), TrayState.Delivering => Color.FromArgb(30, 140, 80), TrayState.Attention => Color.FromArgb(214, 120, 0), _ => Color.FromArgb(110, 116, 128) };
                using (var brush = new SolidBrush(color)) g.FillEllipse(brush, badge);
                using (var ring = new Pen(Color.White, size / 16f)) g.DrawEllipse(ring, badge);
                using var mark = new Pen(Color.White, size / 14f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var c = new PointF(badge.X + badge.Width / 2, badge.Y + badge.Height / 2); var u = badge.Width / 6;
                switch (state)
                {
                    case TrayState.Listening: for (var i = -1; i <= 1; i++) g.DrawLine(mark, c.X + i * u * 1.4f, c.Y - u * (i == 0 ? 1.6f : 1f), c.X + i * u * 1.4f, c.Y + u * (i == 0 ? 1.6f : 1f)); break;
                    case TrayState.Delivering: g.DrawLine(mark, c.X - u * 1.3f, c.Y + u * 1.3f, c.X + u * 1.3f, c.Y - u * 1.3f); g.DrawLines(mark, [new PointF(c.X - u * .2f, c.Y - u * 1.3f), new PointF(c.X + u * 1.3f, c.Y - u * 1.3f), new PointF(c.X + u * 1.3f, c.Y + u * .2f)]); break;
                    case TrayState.Attention: g.DrawLine(mark, c.X, c.Y - u * 1.6f, c.X, c.Y + u * .3f); g.DrawLine(mark, c.X, c.Y + u * 1.5f, c.X, c.Y + u * 1.6f); break;
                    default: g.DrawLine(mark, c.X - u * .8f, c.Y - u * 1.3f, c.X - u * .8f, c.Y + u * 1.3f); g.DrawLine(mark, c.X + u * .8f, c.Y - u * 1.3f, c.X + u * .8f, c.Y + u * 1.3f); break;
                }
            }
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { Native.DestroyIcon(handle); }
    }
    // Preview-only DPI simulation for the hand-laid floatbar and notice; the app
    // itself always uses the window's real DPI.
    internal static float? PreviewScale;
    internal static float ScaleOf(Control control) => PreviewScale ?? control.DeviceDpi / 96f;
    private static readonly Dictionary<(float, FontStyle), Font> pixelFonts = [];
    // Pixel-sized fonts for the hand-laid surfaces, cached so painting never leaks GDI handles.
    internal static Font PixelFont(float logicalPixels, float scale, FontStyle style = FontStyle.Regular)
    {
        var key = ((float)Math.Round(logicalPixels * scale, 2), style);
        if (!pixelFonts.TryGetValue(key, out var font)) pixelFonts[key] = font = new Font(Family, key.Item1, style, GraphicsUnit.Pixel);
        return font;
    }
    internal static Color Blend(Color a, Color b, float alpha) => Color.FromArgb((int)(a.R * alpha + b.R * (1 - alpha)), (int)(a.G * alpha + b.G * (1 - alpha)), (int)(a.B * alpha + b.B * (1 - alpha)));
    // Segoe UI has no CJK glyphs: Windows then borrows each Chinese character from
    // linked fonts of other weights and hinting, which looks jagged. The Chinese
    // system UI font renders Latin and Chinese text consistently with ClearType.
    internal const string Family = "Microsoft YaHei UI";
    internal static readonly Font Body = new(Family, 9f);
    internal static readonly Font Small = new(Family, 8.25f);
    internal static readonly Font Strong = new(Family, 9f, FontStyle.Bold);
    internal static readonly Font Heading = new(Family, 15f, FontStyle.Bold);
    internal static readonly Font Title = new(Family, 17f, FontStyle.Bold);
    // Preview-only theme override; the app itself always follows the system.
    internal static bool? ForcedDark;
    internal static bool Dark
    {
        get
        {
            if (ForcedDark is bool forced) return forced;
            if (SystemInformation.HighContrast) return SystemColors.Window.GetBrightness() < .5;
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }
    internal static Color Surface => SystemInformation.HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(36, 39, 45) : Color.White;
    internal static Color Soft => SystemInformation.HighContrast ? SystemColors.Control : Dark ? Color.FromArgb(44, 48, 55) : Color.FromArgb(244, 245, 247);
    internal static Color Ink => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(240, 242, 246) : Color.FromArgb(32, 36, 43);
    internal static Color Secondary => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(180, 188, 200) : Color.FromArgb(96, 104, 117);
    internal static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(134, 181, 255) : Color.FromArgb(18, 99, 214);
    internal static Color Line => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(59, 64, 74) : Color.FromArgb(225, 228, 233);
    internal static Color StrongLine => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(95, 104, 117) : Color.FromArgb(198, 204, 213);
    internal static Color AccentSoft => SystemInformation.HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(40, 58, 85) : Color.FromArgb(234, 242, 255);
    internal static Color AccentInk => SystemInformation.HighContrast ? SystemColors.HighlightText : Dark ? Color.FromArgb(18, 33, 58) : Color.White;
    internal static Color Amber => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(240, 190, 110) : Color.FromArgb(150, 85, 0);
    internal static Color AmberSoft => SystemInformation.HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(62, 50, 30) : Color.FromArgb(255, 245, 228);
    internal static Color AmberLine => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(120, 92, 45) : Color.FromArgb(236, 205, 150);
    internal static Color Green => SystemInformation.HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(90, 190, 130) : Color.FromArgb(36, 120, 72);

    internal static Label Label(string text, bool secondary = false, bool heading = false) => new()
    {
        Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(540, 0),
        Font = heading ? Heading : Body, Margin = new Padding(0, 0, 0, heading ? 12 : 8), Tag = secondary ? "secondary" : null
    };
    internal static Button Button(string title, Action action, bool primary = false)
    {
        var button = new DesignButton { Text = title, AutoSize = true, MinimumSize = new Size(60, 32), Padding = new Padding(12, 5, 12, 5), Font = Body, Tag = primary ? "primary" : null };
        button.Click += (_, _) => action(); return button;
    }
    internal static TableLayoutPanel Column() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top, Margin = Padding.Empty };
    internal static Control Row(string title, string detail, Control value)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(14), Margin = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 174));
        var labels = Column();
        var name = Label(title); name.Font = Strong; name.MaximumSize = new Size(340, 0);
        var note = Label(detail, secondary: true); note.Font = Small; note.MaximumSize = new Size(340, 0); note.Margin = Padding.Empty;
        labels.Controls.Add(name); labels.Controls.Add(note);
        value.Anchor = AnchorStyles.Right; row.Controls.Add(labels, 0, 0); row.Controls.Add(value, 1, 0);
        return row;
    }
    internal static Control Group(params Control[] rows)
    {
        var group = new DesignGroup { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(1) }; group.Margin = new Padding(0, 8, 0, 10);
        foreach (var row in rows) group.Controls.Add(row);
        return group;
    }
    internal static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
        row.Controls.AddRange(controls); return row;
    }
    internal static void Apply(Control root)
    {
        var inheritedSoft = root.Parent?.Tag as string == "soft";
        root.BackColor = root.Tag as string == "soft" || inheritedSoft ? Soft : root.Tag as string == "choice" ? AccentSoft : root.Parent?.BackColor ?? Surface;
        root.ForeColor = root.Tag as string == "secondary" ? Secondary : Ink;
        if (root is Button button)
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = StrongLine;
            if (button.Tag as string == "primary") { button.BackColor = Accent; button.ForeColor = AccentInk; }
            else if (button.Tag as string == "selected") { button.BackColor = AccentSoft; button.ForeColor = Accent; }
        }
        foreach (Control child in root.Controls) Apply(child);
        root.Invalidate();
    }
    internal static void Watch(Form form)
    {
        var icon = CreateAppIcon(); form.Icon = icon;
        form.Disposed += (_, _) => icon.Dispose();
        Apply(form);
        UserPreferenceChangedEventHandler changed = (_, _) =>
        {
            if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(() => { if (!form.IsDisposed) Apply(form); });
        };
        SystemEvents.UserPreferenceChanged += changed;
        form.Disposed += (_, _) => SystemEvents.UserPreferenceChanged -= changed;
    }
    internal static void FitToScreen(Form form)
    {
        form.Load += (_, _) =>
        {
            var area = Screen.FromControl(form).WorkingArea;
            var limit = new Size(Math.Max(320, area.Width - 24), Math.Max(240, area.Height - 24));
            form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, limit.Width), Math.Min(form.MinimumSize.Height, limit.Height));
            form.Size = new Size(Math.Min(form.Width, limit.Width), Math.Min(form.Height, limit.Height));
            form.Location = new Point(Math.Clamp(form.Left, area.Left, Math.Max(area.Left, area.Right - form.Width)), Math.Clamp(form.Top, area.Top, Math.Max(area.Top, area.Bottom - form.Height)));
        };
    }
    internal static void Clear(Control control)
    {
        foreach (Control child in control.Controls.Cast<Control>().ToArray()) { control.Controls.Remove(child); child.Dispose(); }
    }
}

// These controls retain WinForms button keyboard/accessibility semantics while
// painting the same border, fill and corner tokens as the approved HTML draft.
internal sealed class DesignButton : Button
{
    private bool hover, pressed;
    protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; base.OnMouseDown(e); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; base.OnMouseUp(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiStyle.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var tag = Tag as string;
        var fill = tag == "primary" ? UiStyle.Accent : tag == "selected" ? UiStyle.AccentSoft : tag is "navigation" or "subtle" ? Parent?.BackColor ?? UiStyle.Surface : UiStyle.Surface;
        var ink = tag == "primary" ? UiStyle.AccentInk : tag == "selected" ? UiStyle.Accent : tag is "navigation" or "subtle" ? UiStyle.Secondary : UiStyle.Ink;
        if (!Enabled) { fill = Blend(fill, Parent?.BackColor ?? UiStyle.Surface, .55f); ink = Blend(ink, fill, .55f); }
        if (Enabled && (hover || pressed)) fill = Blend(UiStyle.Ink, fill, pressed ? .10f : .05f);
        using var path = DesignGroup.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 6 * DeviceDpi / 96f);
        using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
        if (tag is not ("primary" or "selected" or "navigation" or "subtle")) { using var pen = new Pen(UiStyle.StrongLine); e.Graphics.DrawPath(pen, path); }
        // Compact icon buttons (×, 展开) cannot afford the full 12 px text inset.
        var rect = ClientRectangle; rect.Inflate(-Math.Min(12 * DeviceDpi / 96, Math.Max(0, (Width - TextRenderer.MeasureText(Text, Font).Width) / 2)), 0);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
        TextRenderer.DrawText(e.Graphics, Text, Font, rect, ink, flags);
        if (Focused && ShowFocusCues) { rect = ClientRectangle; rect.Inflate(-3, -3); ControlPaint.DrawFocusRectangle(e.Graphics, rect, ink, fill); }
    }
    private static Color Blend(Color a, Color b, float alpha) => Color.FromArgb((int)(a.R * alpha + b.R * (1 - alpha)), (int)(a.G * alpha + b.G * (1 - alpha)), (int)(a.B * alpha + b.B * (1 - alpha)));
}

internal sealed class DesignGroup : TableLayoutPanel
{
    internal static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath(); var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * DeviceDpi / 96f);
        using var pen = new Pen(Tag as string == "choice" ? UiStyle.Accent : UiStyle.Line);
        e.Graphics.DrawPath(pen, path);
        foreach (Control child in Controls.Cast<Control>().Skip(1)) e.Graphics.DrawLine(pen, 1, child.Top, Width - 2, child.Top);
    }
}
