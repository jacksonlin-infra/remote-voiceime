using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

// Explicit Windows-only UI verification. Never creates BridgeApp, an input hook,
// a speech source, clipboard bridge or real remote target.
internal static class UiPreview
{
    private sealed class Target : IRemoteTarget
    {
        public string Label => "UU 远程 · 工作电脑";
        public bool IsValid => true;
        public bool IsForeground => false;
        public bool Activate() => false;
    }
    private static readonly List<string> passed = [];
    private static IEnumerable<Control> Descendants(Control root) => new[] { root }.Concat(root.Controls.Cast<Control>().SelectMany(Descendants));
    private static Button Button(Form form, string title) => Descendants(form).OfType<Button>().First(x => x.Text == title);
    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException("FAIL: " + message);
        passed.Add(message);
    }
    private static void Settle(Form form) { form.PerformLayout(); form.Refresh(); for (var i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(40); } }
    // Settings-style windows: DrawToBitmap paints the whole window, including the title bar.
    private static void Render(Form form, string name, string directory)
    {
        form.Show(); Settle(form);
        Check(form.Controls.Count > 0, name + " contains native controls");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, name + ".png")); form.Hide();
    }
    // Floatbar and notice: captured from the screen as composed (painted placeholder,
    // edit control, rounded region), with the corners outside the region left transparent.
    private static void Capture(Form form, string name, string directory, bool hide = true)
    {
        if (!form.Visible) form.Show();
        Settle(form);
        var bounds = form.Bounds;
        using var screen = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        using var output = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(output))
        {
            g.Clear(Color.Transparent);
            if (form.Region != null) g.SetClip(form.Region, System.Drawing.Drawing2D.CombineMode.Replace);
            g.DrawImageUnscaled(screen, 0, 0);
        }
        output.Save(Path.Combine(directory, name + ".png"));
        if (hide) form.Hide();
    }
    private static T Child<T>(Control root, Func<T, bool> match) where T : Control => Descendants(root).OfType<T>().First(match);
    internal static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var target = new Target();
        Floatbar(directory, target, 1f, "");
        UiStyle.PreviewScale = 1.25f; Floatbar(directory, target, 1.25f, "@125"); Notices(directory, "@125");
        UiStyle.PreviewScale = 1.5f; Floatbar(directory, target, 1.5f, "@150"); Notices(directory, "@150");
        UiStyle.PreviewScale = null;
        Notices(directory, "");
        Checklist(directory);
        SettingsPages(directory);
        Recent(directory, target);
        TrayStates(directory);
        File.WriteAllLines(Path.Combine(directory, "verification.txt"),
            [$"{ProductInfo.Name} {ProductInfo.VersionLabel} native UI preview: {passed.Count} checks passed. No speech, keyboard injection or clipboard access.", .. passed.Select(x => "PASS: " + x)]);
    }
    private static void Floatbar(string directory, Target target, float scale, string suffix)
    {
        using var composer = new ComposerForm { Key = "右 Ctrl" };
        var calls = 0; composer.Primary = () => calls++;
        var receiver = composer.Editor;
        composer.Present("", target.Label, false);
        var handle = receiver.Handle;
        Settle(composer);
        Check(Math.Abs(composer.Width - 600 * scale) <= 2, $"capsule is 600 logical px wide{suffix}");
        Check(receiver.PlaceholderVisible && receiver.Placeholder == "请说话…", $"empty receiver shows the listening placeholder{suffix}");
        Native.SendMessage(receiver.Handle, 0x10d, 0, 0); // WM_IME_STARTCOMPOSITION: Doubao starts streaming
        Check(!receiver.PlaceholderVisible, $"placeholder hides during IME composition, before any committed text{suffix}");
        Native.SendMessage(receiver.Handle, 0x10e, 0, 0);
        Check(receiver.PlaceholderVisible, $"placeholder returns when the composition ends without text{suffix}");
        Capture(composer, "floatbar-listening" + suffix, directory, hide: false);
        receiver.Text = "把客户反馈的三个问题整理进周报"; Settle(composer);
        Check(!receiver.PlaceholderVisible && composer.CompactLines == 1 && Math.Abs(composer.Height - 56 * scale) <= 2, $"short text stays one 56 px line without the placeholder{suffix}");
        Capture(composer, "floatbar-text" + suffix, directory, hide: false);
        var chip = Child<PaintedButton>(composer, x => x.Text.EndsWith("结束"));
        chip.PerformPress(); Check(calls == 1, $"capsule key chip ends listening{suffix}");
        receiver.Text = string.Concat(Enumerable.Repeat("这是一段比较长的口述内容，用来检查浮条会随内容增高。", 12)) + "最新一句在这里。";
        Settle(composer);
        var lines = receiver.LineCount;
        Check(composer.CompactLines == ComposerForm.MaxCompactLines && composer.Height > 56 * scale * 2, $"long text grows the capsule upward to 8 lines{suffix}");
        Check(receiver.WordWrap && (Native.GetWindowLong(receiver.Handle, -16) & 0x00100080) == 0, $"receiver wraps inside the visible width (no horizontal scroll){suffix}"); // WS_HSCROLL | ES_AUTOHSCROLL
        Check(receiver.Left >= 0 && receiver.Right <= chip.Left && receiver.Bottom <= composer.ClientSize.Height && receiver.Top >= 0, $"text area stays inside the capsule and left of the key chip{suffix}");
        var first = (int)Native.SendMessage(receiver.Handle, 0xce, 0, 0); // EM_GETFIRSTVISIBLELINE
        Check(first + ComposerForm.MaxCompactLines >= lines && receiver.Height == ComposerForm.MaxCompactLines * receiver.LineHeight, $"capsule shows exactly 8 whole lines with the newest text in view{suffix}");
        Capture(composer, "floatbar-long" + suffix, directory, hide: false);
        // Doubao streams a whole utterance as one open composition, outside the edit buffer.
        receiver.Text = ""; composer.Relayout();
        var streamed = string.Concat(Enumerable.Repeat("豆包把整句识别结果放在一个输入法组字里，", 5));
        receiver.SimulatedComposition = () => streamed; composer.RefreshContent(); Settle(composer);
        var needed = (int)Math.Ceiling(TextRenderer.MeasureText(streamed, receiver.Font, new Size(receiver.ClientSize.Width - 4, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height / (double)receiver.LineHeight);
        Check(needed > 1 && composer.CompactLines >= Math.Min(needed, ComposerForm.MaxCompactLines) && receiver.Height >= composer.CompactLines * receiver.LineHeight && receiver.Bottom <= composer.ClientSize.Height,
            $"an open multi-line composition grows the capsule before any text is committed{suffix}");
        receiver.SimulatedComposition = () => ""; composer.RefreshContent(); Settle(composer);
        Check(composer.CompactLines == 1, $"the capsule shrinks back when the composition is gone{suffix}");
        receiver.SimulatedComposition = null;
        receiver.Text = string.Concat(Enumerable.Repeat("这是一段比较长的口述内容，用来检查浮条会随内容增高。", 12)) + "最新一句在这里。"; Settle(composer);
        receiver.Select(2, 3);
        composer.SetPhase(CapturePhase.Settling, "等待豆包定稿…"); Settle(composer);
        Check(!chip.Enabled && chip.Text == "整理中…", $"settling disables the chip{suffix}");
        Capture(composer, "floatbar-settling" + suffix, directory, hide: false);
        var review = "1. 接口联调已完成\r\n2. 下周开始灰度";
        receiver.Text = review; receiver.Select(2, 3);
        composer.SetPhase(CapturePhase.Review, "文字含换行或控制字符，请核对。按 右 Ctrl 会原样投送。"); Settle(composer);
        Check(ReferenceEquals(receiver, composer.Editor) && receiver.Handle == handle && receiver.SelectionStart == 2 && receiver.SelectionLength == 3,
            $"pill and card share one IME receiver: same window handle, text and selection{suffix}");
        Check(!Descendants(composer).OfType<CheckBox>().Any() && !Descendants(composer).OfType<Button>().Any(), $"review card has no confirmation checkbox and no clickable send button{suffix}");
        var keyCap = Child<PaintedButton>(composer, x => x.Text == "右 Ctrl 投送");
        calls = 0; receiver.Primary?.Invoke(); keyCap.PerformPress(); chip.PerformPress();
        Check(calls == 0 && keyCap.Visible && !keyCap.Clickable && !receiver.SubmitOnEnter, $"review sends only by the global key: Return edits, the key cap is not clickable{suffix}");
        Check(Descendants(composer).OfType<Label>().Any(x => x.Visible && x.Text.StartsWith("文字含换行")), $"review shows the amber reason note{suffix}");
        receiver.Select(receiver.TextLength, 0);
        Capture(composer, "floatbar-review" + suffix, directory, hide: false);
        var shortCard = composer.Height;
        receiver.Text = string.Concat(Enumerable.Repeat("第一段需要核对的长文字，包含换行。\r\n", 18)); composer.Relayout(); Settle(composer);
        Check(composer.Height > shortCard && receiver.Height <= 320 * scale && receiver.Height % receiver.LineHeight == 0 && receiver.Right <= composer.ClientSize.Width, $"review editor grows with long text up to its limit, shows whole lines and stays inside the card{suffix}");
        Capture(composer, "floatbar-review-long" + suffix, directory, hide: false);
        composer.SetCancelling("已取消本轮。按 右 Ctrl 或在豆包中结束录音，浮条随后自动关闭；之前排队的文字会继续投送。"); Settle(composer);
        Check(ReferenceEquals(receiver, composer.Editor) && receiver.Handle == handle, $"cancelling keeps the same receiver until Doubao stops{suffix}");
        var cancelled = 0; composer.CancelCapture = () => cancelled++;
        Child<PaintedButton>(composer, x => x.Text == "已结束，关闭").PerformPress();
        Check(cancelled == 1, $"cancelling offers an explicit close{suffix}");
        Capture(composer, "floatbar-cancelling" + suffix, directory);
        Check(composer.Icon != null, "native window loads the product icon");
        composer.ClearAndHide();
    }
    private static void Notices(string directory, string suffix)
    {
        using var hud = new StatusHud();
        var scale = UiStyle.PreviewScale ?? 1f;
        var repasted = 0;
        hud.ShowHud("已投送 23 字", kind: HudTone.Success, buttons: [new("再贴一次", () => repasted++)], duration: null); Settle(hud);
        Check(Math.Abs(hud.Width - StatusHud.LogicalWidth * scale) <= 2 && (Native.GetWindowLong(hud.Handle, -20) & 0x08000000) != 0 && Form.ActiveForm != hud, $"notice is 440 px wide and never activates{suffix}");
        Capture(hud, "hud-delivered" + suffix, directory, hide: false);
        Check(hud.Press("再贴一次") && repasted == 1 && !hud.Visible, $"“再贴一次” on the notice is clickable and runs once{suffix}");
        if (suffix.Length == 0)
        {
            hud.ShowHud("上一条正在投送，完成后自动开始听写", duration: null); Capture(hud, "hud-busy", directory);
            hud.ShowHud("没有识别到文字，再按 右 Ctrl 重试", kind: HudTone.Warning, duration: null); Capture(hud, "hud-no-speech", directory);
            hud.ShowHud("没有找到远程窗口", "请先点一下 UU / 远程桌面里的远端输入框，再按 右 Ctrl。", HudTone.Warning, duration: null); Capture(hud, "hud-no-remote", directory);
            hud.ShowHud("切到远端输入框，按 右 Ctrl", kind: HudTone.Test, buttons: [new("取消", () => { })], duration: null); Capture(hud, "hud-test", directory);
            hud.ShowHud("远端出现测试文字了吗？", "“Remote VoiceIME 测试 143022”", HudTone.Test, [new("出现了", () => { }, Primary: true), new("没有 / 不完整", () => { })], duration: null);
            Capture(hud, "hud-test-question", directory);
        }
        hud.ShowHud("本次未投送", "远控窗口未能回到前台，已停止粘贴。文字已保留。", HudTone.Warning, [new("重新投送", () => { }, Primary: true), new("打开核对", () => { })], duration: null);
        Settle(hud);
        Check(hud.ActionTitles.SequenceEqual(["重新投送", "打开核对"]), $"failed delivery offers re-delivery and review{suffix}");
        Capture(hud, "hud-failed" + suffix, directory);
        hud.ShowHud("没出现？按看到的现象调整后重试", "调整后会立即用同一个远端窗口再测一次。", HudTone.Warning, [new("关闭", () => { })],
        [
            new("贴出了别的旧内容", "剪贴板同步慢于粘贴", new("改用兼容速度并重试", () => { })),
            new("出现一个 v，或什么都没有", "Mac 远端通常需要 Win + V", new("改用 Win + V 并重试", () => { })),
            new("手动复制粘贴也不行", "UU 远程 的剪贴板同步没有打开", new("查看开启方法", () => { }))
        ], duration: null);
        Settle(hud);
        Check(hud.ActionTitles.Count() == 4 && hud.Height > 200 * scale, $"try-once fixes list one action per symptom{suffix}");
        Capture(hud, "hud-test-fix" + suffix, directory);
    }
    private static void Checklist(string directory)
    {
        using var checklist = new SetupForm(); checklist.SetKey("右 Ctrl");
        var pending = new SetupStatus(true, true, SharedKey: true, VoiceKeyConfirmed: false, ["UU 远程"], ClipboardConfirmed: false, null);
        checklist.Update(pending); checklist.Show(); Settle(checklist);
        Check(!Button(checklist, "开始使用").Enabled && pending.RequiredDone == 1, "checklist cannot start until required items are done");
        Render(checklist, "setup-checklist", directory); checklist.Show();
        var ticked = new List<bool>(); checklist.ClipboardConfirmed = ticked.Add;
        Child<CheckBox>(checklist, x => x.Text.Contains("剪贴板同步")).Checked = true; Settle(checklist);
        Check(ticked.SequenceEqual([true]), "clipboard confirmation is reported by the checklist");
        checklist.Update(pending with { VoiceKeyConfirmed = true, ClipboardConfirmed = true, Calibration = "UU · 工作电脑 已通过测试" }); checklist.Show(); Settle(checklist);
        Check(Button(checklist, "开始使用").Enabled, "all required items enable 开始使用 without a test");
        Render(checklist, "setup-ready", directory);
        checklist.Update(pending with { SharedKey = false, ClipboardConfirmed = true }); checklist.SetKey("F8"); checklist.Show(); Settle(checklist);
        Check(Button(checklist, "开始使用").Enabled && Descendants(checklist).OfType<Button>().Any(x => x.Text == "改用右 Ctrl"), "F8 needs no Doubao change and offers the shared key");
        Render(checklist, "setup-f8", directory);
    }
    private static void SettingsPages(string directory)
    {
        var values = new AppSettings { ToolShortcut = ToolShortcut.SharedRightCtrl, AutoDeliver = true, SyncSpeed = SyncSpeed.Standard };
        using var settings = new SettingsForm(values);
        settings.SetTarget(RemoteClientKind.UU, null);
        foreach (var (section, index) in SettingsForm.Sections.Select((x, i) => (x, i)))
        {
            settings.Show(); settings.SelectSection(index); Render(settings, "settings-" + new[] { "general", "remote", "diagnostics" }[index], directory);
        }
        settings.Show(); settings.SelectSection(1); Settle(settings);
        Check(Descendants(settings).OfType<ComboBox>().Any(x => x.AccessibleName == "同步速度" && x.Items.Count == 3), "remote page offers fast / standard / compatible");
        settings.SelectSection(0); Settle(settings);
        Check(Descendants(settings).OfType<ComboBox>().Any(x => x.AccessibleName == "投送后的剪贴板"), "general page offers the clipboard-after-delivery choice");
        settings.Hide();
    }
    private static void Recent(string directory, Target target)
    {
        using var recent = new RecoveryForm(); recent.CanRestore = _ => true;
        var payload = "  中文 é 👩🏽‍💻\r\n第二行  ";
        var records = new[] { new RecoveryRecord(Guid.NewGuid(), payload, target, 10, RecoveryStatus.Failed, "远控窗口未能回到前台，已停止粘贴"), new RecoveryRecord(Guid.NewGuid(), "这版保留轻量浮条，设置放在系统托盘里。", target, 9, RecoveryStatus.Unconfirmed, "远端未确认") };
        recent.RefreshRecords(records, 11); Render(recent, "recent-text", directory);
        recent.RefreshRecords([], 400); Check(!Descendants(recent).OfType<TextBox>().Any(x => x.Text.Contains(payload)), "hidden recovery discards expired text");
    }
    private static void TrayStates(string directory)
    {
        var states = Enum.GetValues<UiStyle.TrayState>();
        using var strip = new Bitmap(states.Length * 40, 40);
        using (var g = Graphics.FromImage(strip))
        {
            g.Clear(UiStyle.Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240));
            for (var i = 0; i < states.Length; i++) { using var icon = UiStyle.CreateTrayIcon(states[i]); g.DrawIcon(icon, new Rectangle(i * 40 + 4, 4, 32, 32)); }
        }
        strip.Save(Path.Combine(directory, "tray-states.png"));
        Check(states.Length == 5, "tray icon distinguishes ready / listening / delivering / attention / paused");
    }
}
