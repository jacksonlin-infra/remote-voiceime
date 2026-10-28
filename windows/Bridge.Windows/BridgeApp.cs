using System.Diagnostics;
using System.Globalization;
using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

internal sealed class UiClock : IClock, IDisposable
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly HashSet<System.Windows.Forms.Timer> timers = [];
    public double Now => watch.Elapsed.TotalSeconds;
    public void After(double seconds, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)(seconds * 1000)) };
        timers.Add(timer);
        timer.Tick += (_, _) => { timer.Stop(); timers.Remove(timer); timer.Dispose(); action(); };
        timer.Start();
    }
    public void Dispose() { foreach (var timer in timers) timer.Dispose(); timers.Clear(); }
}

internal sealed class BridgeApp : ApplicationContext
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly UiClock clock = new();
    private readonly ComposerForm composer = new();
    private readonly StatusHud hud = new();
    private readonly RecoveryStore recovery = new();
    private readonly IRemoteClientAdapter remote;
    private readonly IVoiceSourceAdapter source;
    private readonly DeliveryQueue deliveries;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly Dictionary<UiStyle.TrayState, Icon> trayIcons = [];
    private UiStyle.TrayState trayState = UiStyle.TrayState.Ready;
    private readonly KeyboardHook hook;
    private readonly MouseActivityHook? mouse;
    private readonly System.Windows.Forms.Timer polling = new() { Interval = 150 };
    private SettingsForm? setup;
    private RecoveryForm? recoveryWindow;
    private SetupForm? checklist;
    private double lastUiRefresh, lastHookRefresh, lastUserInput = -1;
    private double? lastPasteAt;
    private bool IsManagingUi => Native.IsOwnForeground && (setup?.Visible == true || recoveryWindow?.Visible == true || checklist?.Visible == true);
    private CaptureSession? capture;
    private IRemoteTarget? destination;
    private WindowTarget? lastRemote;
    private WindowTarget? eligibleRemote;
    private nint previousForeground;
    private bool enabled = true, cancelling, quitting, attention;
    private bool sourceStartPending, finishRequested, stopPending;
    private Guid? deliverAfterCompose;
    private SharedPress? pendingShared;
    // Decided at key-down: whether the opening press also reached Doubao.
    private bool openingPassed;
    private bool doubaoRecording, sawRecording, stoppedBySource, lastComposing;
    private double? recordingHiddenSince;
    private double stillRecordingNoticeAt, cancelledAt;
    private Guid? cancelRecord;
    private bool cancelWaitNotified;
    // A press that arrived while the previous paste was still being sent.
    private (nint Window, double At)? pendingActivation;
    // Try once: the next press in a remote window pastes fixed text.
    private bool testArmed, testFromSetup;
    private Guid? testJobId;
    private (RemoteClientKind Kind, string? Device, string Profile, string Label)? testProfile;
    private string message = "";
    // UU devices already offered the other paste key in this run.
    private readonly HashSet<string> hintedDevices = [];
    private bool Shared => ToolShortcut.SharesSourceKey(settings.ToolShortcut);
    private string Key => ToolShortcut.Label(settings.ToolShortcut);
    private static int Characters(string text) => new StringInfo(text).LengthInTextElements;

    internal BridgeApp()
    {
        Diagnostics.Start();
        remote = new RemoteClientAdapter(settings); source = new DoubaoShortcutSource(settings);
        _ = composer.Handle; _ = composer.Editor.Handle; _ = hud.Handle;
        deliveries = new DeliveryQueue(clock, new ClipboardBridge(composer.Handle), new PasteEmitter(target => RemoteClients.Paste(target is WindowTarget window ? window.Kind : RemoteClientKind.UU, settings.PasteKeyFor((target as WindowTarget)?.Device))))
        {
            CanDrain = () => capture == null && !IsManagingUi,
            MayActivate = job => enabled && capture == null && (job.FocusTarget || job.Target.IsForeground || Native.IsOwnForeground),
            ModifiersClear = () => Native.ModifiersClear,
            // Without the mouse signal a clipboard change is never assumed to be an echo.
            UserActedSince = time => mouse == null || lastUserInput >= time,
            Outcome = (job, outcome) => Received(job, outcome.Attempted || !outcome.Message.Contains("剪贴板") || ClipboardBridge.SyncToolOwner() is not { } tool ? outcome :
                outcome with { Message = $"{outcome.Message}（剪贴板被 {tool} 改写，请关闭其剪贴板共享后重试）" }),
            Notice = SetStatus, Trace = Diagnostics.Write,
            Changed = () => { UpdateTray(); if (composer.IsHandleCreated) composer.BeginInvoke(ResumePendingActivation); }
        };
        composer.Primary = Primary;
        composer.CancelCapture = CancelCapture;
        composer.Changed = () => capture?.Changed(clock.Now);
        composer.Key = Key;
        hud.Avoid = () => composer.Visible ? composer.Bounds : null;
        foreach (var state in Enum.GetValues<UiStyle.TrayState>()) trayIcons[state] = UiStyle.CreateTrayIcon(state);
        menu.Opening += (_, _) => BuildMenu();
        tray = new NotifyIcon { Text = $"{ProductInfo.Name} · {Key}", Icon = trayIcons[UiStyle.TrayState.Ready], ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowSettings();
        hook = new KeyboardHook(() => ToolShortcut.VirtualKey(settings.ToolShortcut), IsEligible,
            PassToSource, origin => composer.BeginInvoke(() =>
        {
            if (Native.GetForegroundWindow() != origin) { Diagnostics.Write("hotkey ignored: foreground changed before handling"); return; }
            var action = ShortcutAction(origin, validateTarget: true);
            Diagnostics.Write($"hotkey key={Key} action={action} capture={(capture == null ? "none" : capture.Phase.ToString())} receiver={origin == composer.Handle} cancelling={cancelling} test={testArmed}");
            switch (action)
            {
                case CaptureShortcutAction.Begin: StartCapture(origin, openingPassed); break;
                case CaptureShortcutAction.Advance: Advance(); break;
                case CaptureShortcutAction.Resume: if (cancelling) ResumeCancelled(origin); else composer.FocusInput(); break;
            }
        }));
        hook.Ignored = window => composer.BeginInvoke(() =>
            Diagnostics.Write($"hotkey ignored: foreground={Native.ProcessOf(window)} class={Native.ClassName(window)} capture={(capture == null ? "none" : capture.Phase.ToString())}"));
        hook.Activity = () => lastUserInput = clock.Now;
        try { mouse = new MouseActivityHook(() => lastUserInput = clock.Now); }
        catch (InvalidOperationException) { Diagnostics.Write("mouse activity hook unavailable; clipboard echoes are not rewritten"); }
        SetStatus($"已就绪：在 UU / 远程桌面中按 {Key} 开始听写");
        polling.Tick += (_, _) => Poll(); polling.Start();
        // The checklist appears only while something required is missing.
        clock.After(.3, () => { if (!settings.OnboardingCompleted || !SetupNow().Ready) ShowSetup(); });
    }
    private CaptureShortcutAction ShortcutAction(nint window, bool validateTarget = false)
    {
        if (!enabled || quitting) return CaptureShortcutAction.Ignore;
        var originalRemote = capture == null ? eligibleRemote?.Handle == window :
            destination is WindowTarget target && target.Handle == window && (!validateTarget || target.IsValid);
        return CaptureShortcut.Route(capture != null, window == composer.Handle, originalRemote);
    }
    // The low-level hook only compares captured handles; process/title checks run on the UI callback.
    private bool IsEligible(nint window) => ShortcutAction(window) != CaptureShortcutAction.Ignore;

    // MARK: Tray

    private UiStyle.TrayState Readiness =>
        !enabled ? UiStyle.TrayState.Paused : attention ? UiStyle.TrayState.Attention : capture != null ? UiStyle.TrayState.Listening :
        deliveries.IsInjecting || deliveries.PendingCount > 0 ? UiStyle.TrayState.Delivering : UiStyle.TrayState.Ready;
    private void UpdateTray()
    {
        if (tray == null) return;
        var state = Readiness;
        if (state != trayState) { trayState = state; tray.Icon = trayIcons[state]; }
        var tooltip = $"{ProductInfo.Name} · {message}" + (deliveries.PendingCount > 0 ? $" · {deliveries.PendingCount} 条等待投送" : "");
        tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }
    private void SetStatus(string value)
    {
        message = value; setup?.SetStatus(value); UpdateTray();
    }
    private void BuildMenu()
    {
        attention = false; recovery.Prune(clock.Now);
        foreach (var old in menu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        menu.Items.Clear();
        var label = Readiness switch { UiStyle.TrayState.Paused => "已暂停", UiStyle.TrayState.Attention => "需要处理", UiStyle.TrayState.Listening => "听写中", UiStyle.TrayState.Delivering => "投送中", _ => "就绪" };
        menu.Items.Add(new ToolStripMenuItem($"● {label}") { Enabled = false, Font = UiStyle.Strong, ToolTipText = $"{ProductInfo.Name} {ProductInfo.VersionLabel}\n{Application.ExecutablePath}" });
        menu.Items.Add(new ToolStripMenuItem(message.Length > 48 ? message[..48] + "…" : message) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("最近文字 · 5 分钟后自动清除") { Enabled = false });
        if (recovery.Records.Count == 0) menu.Items.Add(new ToolStripMenuItem("暂无最近文字") { Enabled = false });
        var idle = enabled && capture == null && !deliveries.IsInjecting;
        foreach (var record in recovery.Records)
        {
            var preview = record.Text.Replace("\r", "").Replace('\n', ' ');
            var shortText = preview.Length > 18 ? preview[..18] + "…" : preview;
            var age = Math.Max(0, (int)(clock.Now - record.Created));
            var prefix = record.Status switch { RecoveryStatus.Failed => "未投送 · ", RecoveryStatus.Cancelled => "已取消 · ", RecoveryStatus.Queued => "等待投送 · ", _ => "" };
            var item = new ToolStripMenuItem($"{prefix}{shortText}    {(age < 60 ? $"{age} 秒前" : $"{age / 60} 分钟前")}");
            var busy = deliveries.IsSending(record.Id);
            var id = record.Id;
            item.DropDownItems.Add(new ToolStripMenuItem("复制原文", null, (_, _) => CopyRecord(id)));
            item.DropDownItems.Add(new ToolStripMenuItem("再投送到原窗口", null, (_, _) => Redeliver(id)) { Enabled = idle && !busy });
            item.DropDownItems.Add(new ToolStripMenuItem("打开核对…", null, (_, _) => RestoreRecord(id)) { Enabled = idle && !busy });
            menu.Items.Add(item);
        }
        menu.Items.Add("全部最近文字…", null, (_, _) => ShowRecovery());
        if (deliveries.PendingCount > 0)
            menu.Items.Add($"取消等待中的投送（{deliveries.PendingCount} 条）", null, (_, _) => { deliveries.CancelAll("已取消等待中的投送"); SetStatus("等待中的投送已取消；文字可从最近文字恢复"); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(enabled ? "暂停远程语音输入" : "恢复远程语音输入", null, (_, _) => SetPaused(enabled));
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add($"退出 {ProductInfo.Name}", null, (_, _) => Quit());
        UpdateTray();
    }
    // A press that could not start: the tray text alone is invisible while the user looks at the remote.
    private void Hint(string title, string detail)
    {
        SetStatus(title + "：" + detail);
        hud.ShowHud(title, detail, HudTone.Warning, duration: 4);
    }

    // MARK: Polling

    private void Poll()
    {
        recovery.Prune(clock.Now);
        var foreground = Native.GetForegroundWindow();
        if (foreground != previousForeground)
        {
            previousForeground = foreground;
            if (capture != null) Diagnostics.Write($"foreground -> {Native.ProcessOf(foreground)} receiver={foreground == composer.Handle}");
            eligibleRemote = remote.Capture(foreground);
            if (eligibleRemote != null) lastRemote = eligibleRemote;
            if (eligibleRemote?.Kind == RemoteClientKind.RemoteDesktop) RefreshHook("remote desktop in front");
        }
        var recording = DoubaoProbe.Recording();
        if (recording != doubaoRecording)
        {
            doubaoRecording = recording;
            if (capture != null) Diagnostics.Write($"doubao recording={recording}");
        }
        if (capture is { } session) PollCapture(session, recording);
        // Full screen can be toggled without a focus change; keep the hook ahead of mstsc's.
        if (eligibleRemote?.Kind == RemoteClientKind.RemoteDesktop && foreground == eligibleRemote.Handle && clock.Now - lastHookRefresh >= 3) RefreshHook(null);
        if (clock.Now - lastUiRefresh >= 1)
        {
            lastUiRefresh = clock.Now;
            recoveryWindow?.RefreshRecords(recovery.Records, clock.Now);
            if (checklist?.Visible == true) checklist.Update(SetupNow());
            UpdateTray();
        }
        deliveries.Pump();
    }
    private void PollCapture(CaptureSession session, bool recording)
    {
        var composing = composer.Editor.IsComposing;
        // An open composition grows without committed text, and not every IME update
        // sends a message: keep the floatbar height in step while composing.
        if (composing || composing != lastComposing) { lastComposing = composing; composer.RefreshContent(); }
        if (cancelling)
        {
            // The receiver stays until Doubao is confirmed idle, so no late text reaches another window.
            if (!recording && !composing && clock.Now - cancelledAt >= .3) FinishCancel();
            else if (recording && clock.Now - cancelledAt > 8 && deliveries.PendingCount > 0 && !cancelWaitNotified)
            {
                cancelWaitNotified = true;
                deliveries.CancelAll("未确认豆包录音已结束，文字未投送");
                Hint("待投送文字已保留", "豆包似乎仍在录音。请先结束录音，再从托盘“最近文字”恢复。");
            }
            return;
        }
        var text = composer.Editor.Text;
        var auto = settings.AutoDeliver;
        if (recording)
        {
            sawRecording = true; recordingHiddenSince = null;
            if (session.Phase == CapturePhase.Settling && stoppedBySource)
            {
                // Doubao started again by itself: keep collecting in this round.
                session.Resume(); stoppedBySource = false; composer.SetPhase(CapturePhase.Listening, "");
            }
            // Never finalize while Doubao's recording window is still showing.
            else if (session.Phase == CapturePhase.Settling && Shared && session.StoppedAt is double stopAt && clock.Now - stopAt > 2 && stillRecordingNoticeAt < stopAt)
            {
                stillRecordingNoticeAt = clock.Now; composer.SetMessage($"豆包仍在录音，再按 {Key} 结束。"); composer.SetChip($"再按 {Key} 结束");
            }
            return;
        }
        // Doubao ended the recording itself (silence, its own key): same as the finish key.
        if (session.Phase == CapturePhase.Listening && sawRecording && !sourceStartPending && !stopPending)
        {
            recordingHiddenSince ??= clock.Now;
            if (clock.Now - recordingHiddenSince >= .3)
            {
                Diagnostics.Write("source stopped by itself");
                session.Stop(clock.Now); stoppedBySource = true;
                composer.SetPhase(CapturePhase.Settling, "已结束录音，等待最终文字…");
            }
            return;
        }
        if (session.Phase == CapturePhase.Settling && session.StoppedAt is double stopped && SettleRule.NoSpeech(text, composing, recording, clock.Now - stopped))
        { EndWithoutText(); return; }
        if (deliverAfterCompose == session.Id && session.Phase == CapturePhase.Review && !composing && clock.Now - session.LastChange >= CaptureSession.AutoSettle)
        {
            deliverAfterCompose = null;
            if (auto && CaptureSession.AutoDeliver(true, text)) { Diagnostics.Write("auto delivery after Doubao stopped in review"); Submit(confirmed: false); }
            else composer.SetMessage($"核对最终文字后按 {Key} 投送。");
        }
        // An empty receiver right after the stop is not yet a result: wait for text or "no speech".
        else if (session.Phase == CapturePhase.Settling && TextPolicy.IsEmpty(text)) { }
        else if (session.ReadyToReview(clock.Now, composing, auto ? CaptureSession.AutoSettle : .8))
        {
            if (auto && CaptureSession.AutoDeliver(true, text)) { Diagnostics.Write("auto delivery after stable final text"); Submit(confirmed: false); }
            else Review(auto ? $"文字含换行或特殊字符，请核对。按 {Key} 会原样投送。" : $"核对最终文字后按 {Key} 投送。");
        }
        else if (session.Phase == CapturePhase.Settling && session.StoppedAt is double waited && clock.Now - waited > 30)
            Review("等待定稿超时，文字已保留。先结束豆包录音，再核对后投送。");
        // Do not infer completion from an IME window title or a pause in speech.
    }
    private void RefreshHook(string? reason)
    {
        lastHookRefresh = clock.Now;
        var refreshed = hook.Refresh();
        if (reason != null) Diagnostics.Write($"keyboard hook refreshed={refreshed} ({reason})");
    }

    // MARK: Dictation

    private void StartCapture(nint origin, bool heardOpening)
    {
        if (!enabled || quitting || capture != null) return;
        if (deliveries.IsInjecting)
        {
            // Not dropped: the round starts as soon as the paste keys are out.
            pendingActivation = (origin, clock.Now);
            Diagnostics.Write("capture deferred: previous paste still sending");
            hud.ShowHud("上一条正在投送，完成后自动开始听写", duration: 2);
            return;
        }
        if (Native.GetForegroundWindow() != origin || remote.Capture(origin) is not { } target)
        { Hint("没有找到远程窗口", $"请先点一下 UU / 远程桌面里的远端输入框，再按 {Key}。"); return; }
        pendingActivation = null; lastRemote = target;
        if (testArmed) { RunTest(target); return; }
        destination = target; cancelling = false;
        setup?.Hide(); recoveryWindow?.Hide(); checklist?.Hide(); hud.HideHud();
        sourceStartPending = true; finishRequested = false; stopPending = false;
        sawRecording = stoppedBySource = false; recordingHiddenSince = null; stillRecordingNoticeAt = 0;
        var session = new CaptureSession(clock.Now); capture = session;
        deliverAfterCompose = null; pendingShared = null;
        var remoteDesktop = target.Kind == RemoteClientKind.RemoteDesktop;
        // With a shared key the opening press is kept from Doubao (SharedKey.OpeningPasses).
        var sourceHeardOpening = !Shared || heardOpening;
        Diagnostics.Write($"capture started source={(source.IsManual ? "manual" : settings.VoiceShortcut)} heardOpening={sourceHeardOpening}");
        // The floatbar answers Doubao's voice key only with Doubao as its input method.
        var imeBefore = DoubaoIme.ActiveName();
        var imeReady = imeBefore == "doubao" || DoubaoIme.Activate();
        Diagnostics.Write($"ime before={imeBefore} doubao={imeReady}");
        Diagnostics.Write($"remote input layout={DoubaoIme.LayoutOf(origin)} prompts={DoubaoProbe.Prompts()}");
        composer.Present("", target.Label, false);
        composer.SetMessage(Shared && !sourceHeardOpening ? $"按 {Key} 开始录音，说完再按 {Key} 投送" : "正在唤起豆包…可直接开口");
        SetStatus("本地听写 → " + target.Label); UpdateTray();
        clock.After(.15, () =>
        {
            if (capture?.Id != session.Id || session.Phase != CapturePhase.Listening || cancelling) return;
            sourceStartPending = false;
            if (!composer.OwnsInput) composer.FocusInput();
            Diagnostics.Write($"source start ownsInput={composer.OwnsInput} ownForeground={Native.IsOwnForeground}");
            if (source.IsManual && !composer.OwnsInput)
            {
                // Manual dictation needs no key from the tool; stay in listening instead of skipping to review.
                composer.SetMessage($"浮条未获得键盘焦点：请点一下浮条，或回到远端再按 {Key}。");
                if (finishRequested) Primary(); return;
            }
            if (!composer.OwnsInput || !source.Start(() => composer.OwnsInput))
            { Review("未能调用豆包，请在浮条内手动启动；文字会继续保留。"); return; }
            composer.SetMessage(source.IsManual
                ? Shared ? (sourceHeardOpening ? "请说话…" : $"按 {Key} 开始录音，说完再按 {Key} 投送")
                    : $"按豆包语音键开始录音；说完按 {Key} 结束"
                : "已发送模拟启动键；没有录音条时请手动按豆包语音键。");
            if (finishRequested) Primary();
        });
    }
    private void ResumePendingActivation()
    {
        if (pendingActivation is not { } pending || deliveries.IsInjecting || capture != null) return;
        pendingActivation = null;
        if (!enabled || clock.Now - pending.At >= 5 || Native.GetForegroundWindow() != pending.Window) return;
        Diagnostics.Write("deferred capture starts after the paste");
        StartCapture(pending.Window, heardOpening: false);
    }
    // Called by the hook at key-down; the decision travels to the release that triggers the action.
    private bool PassToSource(nint window)
    {
        var action = ShortcutAction(window);
        var remoteDesktop = (action == CaptureShortcutAction.Begin ? eligibleRemote?.Kind : (destination as WindowTarget)?.Kind) == RemoteClientKind.RemoteDesktop;
        // Try once pastes fixed text and never starts Doubao.
        if (testArmed && action == CaptureShortcutAction.Begin) return openingPassed = false;
        if (!Shared) return CaptureShortcut.PassToSource(action, capture?.Phase);
        if (action == CaptureShortcutAction.Begin) return openingPassed = SharedKey.OpeningPasses;
        if (action == CaptureShortcutAction.Resume && cancelling && !DoubaoProbe.Recording()) return openingPassed = SharedKey.OpeningPasses;
        if (action != CaptureShortcutAction.Advance || capture == null) return false;
        // While cancelling the press only ever stops Doubao; it never starts a new recording.
        if (cancelling) { pendingShared = null; return DoubaoProbe.Recording(); }
        pendingShared = SharedKey.Decide(DoubaoProbe.Recording(), composer.Editor.IsComposing || !TextPolicy.IsEmpty(composer.Editor.Text));
        // Doubao must be this window's input method when the start press reaches it.
        if (pendingShared == SharedPress.StartSource && !DoubaoIme.IsActive) Diagnostics.Write($"ime reselected before start press doubao={DoubaoIme.Activate()}");
        return SharedKey.Passes(pendingShared.Value);
    }
    private void Advance()
    {
        if (cancelling) { if (!DoubaoProbe.Recording()) FinishCancel(); return; }
        if (!Shared || capture is not { } session) { Primary(); return; }
        var press = pendingShared ?? SharedKey.Decide(DoubaoProbe.Recording(), composer.Editor.IsComposing || !TextPolicy.IsEmpty(composer.Editor.Text));
        pendingShared = null;
        Diagnostics.Write($"shared press={press} phase={session.Phase}");
        switch (press)
        {
            case SharedPress.StartSource:
                // The press reached Doubao and starts recording; keep collecting in this receiver.
                if (session.Phase != CapturePhase.Listening) { session.Resume(); composer.SetPhase(CapturePhase.Listening, ""); }
                sawRecording = false; recordingHiddenSince = null; stoppedBySource = false;
                deliverAfterCompose = null;
                composer.SetMessage("请说话…");
                var started = session.Id;
                clock.After(1.2, () =>
                {
                    // Doubao ignored the press: record why (metadata only) and select it again.
                    if (capture?.Id != started || session.Phase != CapturePhase.Listening || sawRecording || cancelling ||
                        !TextPolicy.IsEmpty(composer.Editor.Text) || composer.Editor.IsComposing || DoubaoProbe.Recording()) return;
                    Diagnostics.Write($"start press not answered ime={DoubaoIme.ActiveName()} layout={DoubaoIme.LayoutOf(composer.Handle)} focus={composer.OwnsInput} prompts={DoubaoProbe.Prompts()}");
                    Diagnostics.Write($"ime refreshed doubao={DoubaoIme.Refresh()}");
                    composer.FocusInput();
                    composer.SetMessage($"豆包没有响应，请再按 {Key}；仍不行请先关闭豆包的提示框");
                });
                break;
            case SharedPress.StopSource:
                // The press reached Doubao and stops it; deliver once its window closes and text settles.
                if (session.Phase == CapturePhase.Listening) Primary();
                else if (session.Phase == CapturePhase.Review) { deliverAfterCompose = session.Id; composer.SetMessage(settings.AutoDeliver ? "正在结束豆包录音，文字稳定后自动投送…" : $"正在结束豆包录音，稍后按 {Key} 投送。"); }
                break;
            default:
                // Doubao is not recording and text exists: this press sends (it was kept from Doubao).
                if (composer.Editor.IsComposing)
                {
                    // Doubao closed its window but is still committing: send once the text settles.
                    if (session.Phase != CapturePhase.Review) { session.Review(); composer.SetPhase(CapturePhase.Review, "正在等待豆包最终文字…"); }
                    deliverAfterCompose = session.Id; break;
                }
                if (session.Phase == CapturePhase.Review) { Submit(confirmed: true); break; }
                session.Stop(clock.Now);
                if (settings.AutoDeliver && CaptureSession.AutoDeliver(true, composer.Editor.Text)) Submit(confirmed: false);
                else Review($"核对文字后按 {Key} 投送。");
                break;
        }
    }
    private void Primary()
    {
        if (capture is not { } session || cancelling || !enabled || quitting) return;
        if (session.Phase == CapturePhase.Listening)
        {
            if (sourceStartPending) { finishRequested = true; composer.SetMessage("本地输入就绪后将结束本轮，请稍候…"); return; }
            if (stopPending) return;
            stopPending = true; composer.FocusInput();
            composer.SetPhase(CapturePhase.Settling, settings.AutoDeliver ? "等待豆包定稿…" : "请在豆包中结束录音，等待最终文字…");
            clock.After(.1, () =>
            {
                stopPending = false;
                if (capture?.Id != session.Id || session.Phase != CapturePhase.Listening || cancelling) return;
                if (!source.Stop(() => composer.OwnsInput)) Review("结束键未发出；请手动结束豆包录音并核对。");
                else session.Stop(clock.Now);
            });
            return;
        }
        if (session.Phase == CapturePhase.Review) Submit(confirmed: true);
    }
    // confirmed: the user's key on the review card, an explicit request that may
    // bring the window forward. Otherwise auto delivery after stable final text.
    private void Submit(bool confirmed)
    {
        if (capture is not { } session || cancelling) return;
        if (confirmed ? session.Phase != CapturePhase.Review : session.Phase == CapturePhase.Listening) return;
        var origin = session.Manual ? "recovery" : "voice";
        Diagnostics.Write($"submit requested origin={origin} confirmed={confirmed} composing={composer.Editor.IsComposing} chars={composer.Editor.TextLength} targetValid={destination?.IsValid} targetForeground={destination?.IsForeground} ownForeground={Native.IsOwnForeground}");
        // Every refusal is traceable: a review card that "did nothing" shows why on screen and in diagnostics.
        void Refuse(string code, string reason)
        {
            Diagnostics.Write($"submit refused={code} origin={origin} confirmed={confirmed}");
            Review(reason);
            if (confirmed) hud.ShowHud("未投送", reason, HudTone.Warning, duration: 4);
        }
        if (!enabled) { Refuse("paused", "工具已暂停，恢复后再投送。"); return; }
        if (composer.Editor.IsComposing) { Refuse("composing", "输入法还有未提交文字，请先完成识别或选词。"); return; }
        if (DoubaoProbe.Recording()) { Refuse("sourceNotStopped", $"豆包仍在录音，请先按 {Key} 或在豆包中结束录音。"); return; }
        var text = composer.Editor.Text;
        if (TextPolicy.IsEmpty(text)) { Refuse("empty", "还没有文字，可以在浮条内输入或取消。"); return; }
        if (!TextPolicy.SizeAllowed(text)) { composer.SetExpanded(true); Refuse("tooLarge", "正文超过 64 KB，请分段；当前文字完整保留。"); return; }
        if (TextPolicy.NeedsReview(text) && !confirmed) { Review($"文字含换行或控制字符，请核对。按 {Key} 会原样投送。"); return; }
        if (destination is not WindowTarget target || !target.IsValid) { Refuse("windowChanged", "原远程窗口已关闭或改变；文字保留，可以复制。"); return; }
        // The confirm key may bring the window forward from anywhere, like "再贴一次".
        if (!confirmed && !target.IsForeground && !Native.IsOwnForeground) { Refuse("frontChanged", "前台应用已变化，已停止自动投送；请返回浮条核对。"); return; }
        var job = new DeliveryJob(Guid.NewGuid(), text, target, JobDelay(target), settings.RestoreClipboard, FocusTarget: true);
        Diagnostics.Write($"{job.Id.ToString()[..8]} queued origin={origin} client={target.Kind} paste={(target.Kind == RemoteClientKind.RemoteDesktop ? "Ctrl + V (scan codes)" : PasteShortcut.Label(settings.PasteKeyFor(target.Device)))} deviceKey={(PasteShortcut.Remembered(settings.DevicePasteKeys, target.Device) ? "remembered" : PasteShortcut.Guess(target.Device) != null ? "name-hint" : "default")} speed={settings.Speed}");
        if (!deliveries.Enqueue(job)) { Refuse("queueFull", "已有多条文字等待投送；当前文字保留，稍后再试。"); return; }
        recovery.Save(new(job.Id, text, target, clock.Now, RecoveryStatus.Queued, "等待投送"), clock.Now);
        capture = null; destination = null; composer.ClearAndHide();
        SetStatus("文字已进入投送队列；可在原远程窗口继续下一轮听写");
        // Same route as re-delivery: the queue activates the client once, right before
        // its own checks. Only a job left waiting behind an earlier one hands focus back early.
        deliveries.Pump();
        if (deliveries.Contains(job.Id) && deliveries.IsSending(job.Id) && !deliveries.IsActive(job.Id))
        {
            target.Activate();
            hud.ShowHud("已排队，按顺序投送；可继续下一轮听写", duration: 2);
        }
    }
    private double JobDelay(WindowTarget target) => SyncSpeed.ForJob(RemoteClients.SyncDelay(target.Kind, settings.Speed), lastPasteAt, clock.Now);
    private void Review(string reason)
    {
        if (capture == null || cancelling) return;
        Diagnostics.Write("review: " + reason);
        capture.Review(); composer.SetPhase(CapturePhase.Review, reason); SetStatus(reason);
    }
    // Recording ended with no text: close the round and hand focus back.
    private void EndWithoutText()
    {
        var target = destination;
        Diagnostics.Write("no speech: recording ended without text");
        capture = null; destination = null; composer.ClearAndHide();
        if (target is { IsValid: true }) target.Activate();
        SetStatus("没有识别到文字");
        hud.ShowHud($"没有识别到文字，再按 {Key} 重试", kind: HudTone.Warning, duration: 3);
        deliveries.Pump();
    }
    // Cancelling affects only this round. Earlier queued texts wait until Doubao is
    // confirmed idle, then continue; the receiver stays so no late text goes elsewhere.
    private void CancelCapture()
    {
        hud.HideHud(); testArmed = false;
        Diagnostics.Write($"cancel requested capture={(capture == null ? "none" : capture.Phase.ToString())} cancelling={cancelling} recording={DoubaoProbe.Recording()}");
        if (capture == null) { composer.ClearAndHide(); return; }
        if (cancelling) { FinishCancel(); return; } // "已结束，关闭" or a second Esc
        cancelRecord = null;
        var text = composer.Editor.Text;
        if (!TextPolicy.IsEmpty(text) && TextPolicy.SizeAllowed(text) && destination != null)
        {
            cancelRecord = Guid.NewGuid();
            recovery.Save(new(cancelRecord.Value, text, destination, clock.Now, RecoveryStatus.Cancelled, "已取消投送"), clock.Now);
        }
        if (capture.Manual) { FinishCancel(); return; }
        cancelling = true; cancelledAt = clock.Now; cancelWaitNotified = false;
        capture.Review();
        composer.SetCancelling(DoubaoProbe.Recording() ? $"已取消本轮。按 {Key} 或在豆包中结束录音，浮条随后自动关闭；之前排队的文字会继续投送。" : "已取消本轮，正在确认豆包已结束…");
        SetStatus("本轮已取消；等待豆包结束录音");
    }
    private void FinishCancel()
    {
        if (capture == null) return;
        var text = composer.Editor.Text;
        // Text that arrived after the cancel is kept with the same recovery entry.
        if (destination != null && !TextPolicy.IsEmpty(text) && TextPolicy.SizeAllowed(text))
        {
            var id = cancelRecord ?? Guid.NewGuid();
            recovery.Save(new(id, text, destination, clock.Now, RecoveryStatus.Cancelled, "已取消投送"), clock.Now);
        }
        Diagnostics.Write("cancel finished");
        capture = null; destination = null; cancelling = false; cancelRecord = null;
        composer.ClearAndHide();
        SetStatus("本轮已取消；文字可从最近文字恢复");
        deliveries.Pump(); ResumePendingActivation();
    }
    // The key in the original remote while a cancelled round still records:
    // adopt that recording as this round instead of fighting Doubao.
    private void ResumeCancelled(nint origin)
    {
        if (capture is not { } session) return;
        if (!DoubaoProbe.Recording()) { FinishCancel(); StartCapture(origin, openingPassed); return; }
        Diagnostics.Write("cancelled round adopted: Doubao still recording");
        cancelling = false; cancelRecord = null;
        if (!composer.Editor.IsComposing) composer.Editor.Clear();
        session.Resume(); sawRecording = true; recordingHiddenSince = null; stoppedBySource = false; sourceStartPending = false;
        composer.SetPhase(CapturePhase.Listening, "豆包仍在录音，已接入这一轮");
        composer.FocusInput();
        SetStatus("本地听写 → " + (destination?.Label ?? ""));
    }

    // MARK: Results, re-delivery and recovery

    private void Received(DeliveryJob job, DeliveryOutcome outcome)
    {
        Diagnostics.Write($"{job.Id.ToString()[..8]} outcome attempted={outcome.Attempted} test={job.Id == testJobId} message={outcome.Message}");
        if (job.Id == testJobId) { TestFinished(job, outcome); return; }
        recovery.Save(new(job.Id, job.Text, job.Target, clock.Now, outcome.Attempted ? RecoveryStatus.Unconfirmed : RecoveryStatus.Failed, outcome.Message), clock.Now);
        var id = job.Id;
        if (!outcome.Attempted)
        {
            attention = true;
            if (capture == null && !quitting)
            {
                SetStatus(outcome.Message + "；可从托盘“最近文字”恢复");
                if (enabled) hud.ShowHud("本次未投送", outcome.Message + "。文字已保留。", HudTone.Warning,
                    [new("重新投送", () => Redeliver(id), Primary: true), new("打开核对", () => RestoreRecord(id))], duration: 8);
            }
        }
        else
        {
            lastPasteAt = clock.Now; attention = false;
            if (capture == null)
            {
                SetStatus($"已投送 · {Characters(job.Text)} 字；远端无回执");
                if (enabled && job.Target.IsForeground && job.Target is WindowTarget { Kind: RemoteClientKind.UU } uu && !PasteShortcut.Remembered(settings.DevicePasteKeys, uu.Device) && hintedDevices.Add(uu.Device))
                {
                    // A UU device never set up: the paste key is a guess. Offer the other key once.
                    var used = settings.PasteKeyFor(uu.Device); var other = PasteShortcut.Other(used); var device = uu.Device;
                    hud.ShowHud($"已投送 {Characters(job.Text)} 字", $"首次投送到「{device}」，使用 {PasteShortcut.Label(used)}。远端没出现时改用 {PasteShortcut.Label(other)}（Mac 远端通常是 Win + V）。", HudTone.Success,
                        [new("再贴一次", () => Redeliver(id)), new($"改用 {PasteShortcut.Label(other)} 重贴", () =>
                        {
                            settings.DevicePasteKeys[device] = other; settings.Save(); RefreshUi();
                            Diagnostics.Write("paste key switched for a new device from the notice");
                            Redeliver(id);
                        }, Primary: true)], duration: 10);
                }
                else if (enabled && job.Target.IsForeground)
                    hud.ShowHud($"已投送 {Characters(job.Text)} 字", kind: HudTone.Success, buttons: [new("再贴一次", () => Redeliver(id))], duration: 3.5);
            }
        }
        UpdateTray();
    }
    // An explicit request: no confirmation step, and every refusal is shown (a silent
    // return looked exactly like "the button does nothing").
    private void Redeliver(Guid id)
    {
        recovery.Prune(clock.Now);
        if (recovery.Records.FirstOrDefault(x => x.Id == id) is not { } record) { hud.ShowHud("这段文字已过期或已清除", kind: HudTone.Warning, duration: 3); return; }
        if (!enabled) { hud.ShowHud("已暂停，恢复后再投送", kind: HudTone.Warning, duration: 3); return; }
        if (capture != null) { hud.ShowHud("正在听写，结束后再投送", kind: HudTone.Warning, duration: 3); return; }
        if (deliveries.IsSending(record.Id)) { hud.ShowHud("这条正在投送，请稍候", duration: 2); return; }
        if (record.Target is not WindowTarget target || !target.IsValid) { Hint("原远程窗口已改变", "回到原窗口后再试；也可从托盘“最近文字”复制原文。"); return; }
        // A fresh job: the previous attempt may still hold the clipboard.
        var job = new DeliveryJob(Guid.NewGuid(), record.Text, target, JobDelay(target), settings.RestoreClipboard, FocusTarget: true);
        Diagnostics.Write($"{job.Id.ToString()[..8]} queued origin=redeliver client={target.Kind} speed={settings.Speed}");
        if (!deliveries.Enqueue(job)) { hud.ShowHud("已有多条文字等待投送，稍后再试", kind: HudTone.Warning, duration: 3); return; }
        recovery.Remove(record.Id);
        recovery.Save(new(job.Id, record.Text, target, clock.Now, RecoveryStatus.Queued, "等待投送"), clock.Now);
        hud.ShowHud("正在再贴一次…", duration: 1.5);
        deliveries.Pump();
    }
    private void CopyRecord(Guid id)
    {
        if (recovery.Records.FirstOrDefault(x => x.Id == id) is not { } record) return;
        try { Clipboard.SetText(record.Text); hud.ShowHud("已复制原文，可手动粘贴", duration: 2); }
        catch (System.Runtime.InteropServices.ExternalException) { hud.ShowHud("剪贴板暂时被占用，请稍后再复制", kind: HudTone.Warning, duration: 3); }
    }
    private void RestoreRecord(Guid id)
    {
        recovery.Prune(clock.Now);
        if (capture != null) { composer.FocusInput(); return; }
        if (!enabled || deliveries.IsInjecting || deliveries.IsSending(id) || recovery.Records.FirstOrDefault(x => x.Id == id) is not { } record)
        { hud.ShowHud("现在无法打开核对，请稍后再试", kind: HudTone.Warning, duration: 3); return; }
        BeginReview(record.Text, record.Target);
        composer.SetPhase(CapturePhase.Review, record.Status switch
        {
            RecoveryStatus.Unconfirmed => $"上次已发出粘贴。先看一眼远端，确实没收到再按 {Key} 投送。",
            RecoveryStatus.Failed => record.Reason + "；确认目标后可重新投送。",
            RecoveryStatus.Cancelled => "已恢复取消前的文字。请确认豆包录音已结束，再投送。",
            _ => "文字已恢复，请核对后投送。"
        });
    }
    private void BeginReview(string text, IRemoteTarget target)
    {
        setup?.Hide(); recoveryWindow?.Hide(); checklist?.Hide(); hud.HideHud(); testArmed = false;
        destination = target; cancelling = false; capture = new(clock.Now, manual: true);
        composer.Present(text, target.Label, true); UpdateTray();
    }
    private void ClearRecovery()
    {
        recovery.Clear(); recoveryWindow?.RefreshRecords(recovery.Records, clock.Now);
        SetStatus("最近文字已清除；正在编辑及排队的任务不受影响。");
    }
    private void SetPaused(bool paused)
    {
        enabled = !paused; deliveries.Suspended = paused;
        if (paused)
        {
            pendingActivation = null; testArmed = false; hud.HideHud();
            deliveries.CancelAll("工具已暂停，投送已取消");
            if (capture != null) Review("工具已暂停；文字保留，请结束豆包录音。");
        }
        SetStatus(enabled ? $"已恢复：在远程窗口按 {Key} 开始听写" : $"已暂停：{Key} 保持原有用途");
        setup?.RefreshState(); UpdateTray();
    }

    // MARK: Try once in the real remote window

    private void ArmTest(bool fromSetup)
    {
        if (!enabled || capture != null) { hud.ShowHud(enabled ? "请先结束当前听写，再试一次" : "已暂停，恢复后再试一次", kind: HudTone.Warning, duration: 3); return; }
        testArmed = true; testFromSetup = fromSetup;
        setup?.Hide(); checklist?.Hide(); recoveryWindow?.Hide();
        Diagnostics.Write("test armed");
        hud.ShowHud($"切到远端输入框，按 {Key}", kind: HudTone.Test, buttons: [new("取消", () => { testArmed = false; ReturnToSetup(); })], duration: null);
        if (lastRemote is { IsValid: true } target) target.Activate();
    }
    private void RunTest(WindowTarget target)
    {
        testArmed = false;
        var device = target.Kind == RemoteClientKind.UU ? target.Device : null;
        var job = new DeliveryJob(Guid.NewGuid(), $"{ProductInfo.Name} 测试 {DateTime.Now:HHmmss}", target, JobDelay(target), settings.RestoreClipboard, FocusTarget: true);
        Diagnostics.Write($"{job.Id.ToString()[..8]} queued origin=test client={target.Kind} paste={(target.Kind == RemoteClientKind.RemoteDesktop ? "Ctrl + V (scan codes)" : PasteShortcut.Label(settings.PasteKeyFor(device)))} speed={settings.Speed}");
        if (!deliveries.Enqueue(job)) { hud.ShowHud("已有文字等待投送，稍后再试", kind: HudTone.Warning, duration: 3); return; }
        testJobId = job.Id;
        testProfile = (target.Kind, device, settings.CalibrationProfile(target.Kind, device), target.Kind == RemoteClientKind.RemoteDesktop ? "远程桌面" : device == null ? "UU 远程" : $"UU · {device}");
        hud.ShowHud("正在投送测试文字…", kind: HudTone.Test, duration: null);
        deliveries.Pump();
    }
    private void TestFinished(DeliveryJob job, DeliveryOutcome outcome)
    {
        testJobId = null;
        if (testProfile is not { } profile) return;
        testProfile = null;
        if (!outcome.Attempted) { TestFailed(profile, outcome.Message); return; }
        lastPasteAt = clock.Now;
        if (settings.CalibrationProfile(profile.Kind, profile.Device) != profile.Profile)
        { hud.ShowHud("投送设置已变化，请重新试一次", kind: HudTone.Warning, duration: 4); return; }
        hud.ShowHud("远端出现测试文字了吗？", $"“{job.Text}”", HudTone.Test,
            [new("出现了", () => TestPassed(profile), Primary: true), new("没有 / 不完整", () => TestFailed(profile, null))], duration: null);
    }
    private void SaveCalibration((RemoteClientKind Kind, string? Device, string Profile, string Label) profile, bool passed)
    {
        settings.Calibrations[Calibration.Key(profile.Kind, profile.Device)] = Calibration.Encode(passed, settings.CalibrationProfile(profile.Kind, profile.Device));
        settings.Save(); RefreshUi();
    }
    private void TestPassed((RemoteClientKind Kind, string? Device, string Profile, string Label) profile)
    {
        SaveCalibration(profile, true);
        Diagnostics.Write("test confirmed by user");
        hud.ShowHud($"{profile.Label} 可以用了。按 {Key} 开始听写", kind: HudTone.Success, duration: 3.5);
        ReturnToSetup();
    }
    // Map what the user saw to the one setting that usually explains it.
    private void TestFailed((RemoteClientKind Kind, string? Device, string Profile, string Label) profile, string? reason)
    {
        SaveCalibration(profile, false);
        Diagnostics.Write($"test failed reported={(reason == null ? "user" : "delivery")}");
        var fromSetup = testFromSetup;
        void Retry(Action change) { change(); settings.Save(); RefreshUi(); ArmTest(fromSetup); }
        var options = new List<HudOption>();
        if (profile.Kind == RemoteClientKind.UU)
        {
            var compatible = settings.Speed == SyncSpeed.Compatible;
            options.Add(new("贴出了别的旧内容", compatible ? "已是兼容速度，可再试一次" : "剪贴板同步慢于粘贴",
                new(compatible ? "再试一次" : "改用兼容速度并重试", () => Retry(() => settings.SyncSpeed = SyncSpeed.Compatible))));
            var other = settings.PasteKeyFor(profile.Device) == PasteShortcut.WinV ? PasteShortcut.CtrlV : PasteShortcut.WinV;
            options.Add(new("出现一个 v，或什么都没有", other == PasteShortcut.WinV ? "Mac 远端通常需要 Win + V" : "Windows 远端通常需要 Ctrl + V",
                new($"改用 {PasteShortcut.Label(other)} 并重试", () => Retry(() => { if (profile.Device != null) settings.DevicePasteKeys[profile.Device] = other; else settings.PasteKey = other; }))));
            options.Add(new("手动复制粘贴也不行", "UU 远程 的剪贴板同步没有打开", new("查看开启方法", () =>
                hud.ShowHud("在 UU 中开启剪贴板同步", "在 UU 的设置里打开剪贴板同步（本地 → 远端）。先在本机复制一段文字，到远端手动粘贴确认能贴出，再重新测试。", HudTone.Info,
                    [new("重新测试", () => ArmTest(fromSetup), Primary: true), new("关闭", ReturnToSetup)], duration: null))));
        }
        else
        {
            options.Add(new("贴出了别的旧内容", "远程桌面固定等待 0.3 秒，可再试一次", new("再试一次", () => ArmTest(fromSetup))));
            options.Add(new("手动复制粘贴也不行", "远程桌面的剪贴板重定向没有打开", new("查看开启方法", () =>
                hud.ShowHud("在远程桌面中开启剪贴板", "在远程桌面连接的「显示选项 → 本地资源」中勾选“剪贴板”，重新连接后再测试。", HudTone.Info,
                    [new("重新测试", () => ArmTest(fromSetup), Primary: true), new("关闭", ReturnToSetup)], duration: null))));
        }
        hud.ShowHud("没出现？按看到的现象调整后重试", reason != null ? reason + "。" : "调整后会立即用同一个远端窗口再测一次。", HudTone.Warning,
            [new("关闭", ReturnToSetup)], options.ToArray(), duration: null);
    }
    private void ReturnToSetup()
    {
        if (!testFromSetup) return;
        testFromSetup = false;
        if (!settings.OnboardingCompleted) ShowSetup();
    }

    // MARK: Windows

    private SetupStatus SetupNow()
    {
        var passed = settings.Calibrations.Keys.Select(key => key == Calibration.RemoteDesktopProfile
                ? (settings.CalibrationFor(RemoteClientKind.RemoteDesktop, null), "远程桌面")
                : key.StartsWith("uu:", StringComparison.Ordinal) ? (settings.CalibrationFor(RemoteClientKind.UU, key[3..].Length == 0 ? null : key[3..]), key[3..].Length == 0 ? "UU 远程" : "UU · " + key[3..]) : (CalibrationResult.Untested, ""))
            .Where(x => x.Item1 == CalibrationResult.Passed).Select(x => x.Item2).ToArray();
        return new(DoubaoProbe.Installed, DoubaoProbe.Running, Shared, settings.VoiceKeyReady, remote.Running(), settings.ClipboardReady,
            passed.Length == 0 ? null : string.Join("、", passed) + " 已通过测试");
    }
    private void RefreshUi()
    {
        setup?.RefreshState();
        if (checklist?.Visible == true) { checklist.SetKey(Key); checklist.Update(SetupNow()); }
    }
    private void SettingsChanged()
    {
        previousForeground = 0; composer.Key = Key; checklist?.SetKey(Key); recoveryWindow?.SetKey(Key);
        setup?.SetTarget(lastRemote?.Kind, lastRemote?.Device);
        RefreshUi(); UpdateTray();
    }
    private bool CanOpenManager()
    {
        if (capture != null) { composer.FocusInput(); return false; }
        if (deliveries.IsInjecting) { hud.ShowHud("正在投送，请稍后再打开", duration: 2); return false; }
        if (remote.Capture(Native.GetForegroundWindow()) is { } target) lastRemote = target;
        return true;
    }
    private void ShowSettings()
    {
        if (!CanOpenManager()) return;
        if (setup == null)
        {
            setup = new SettingsForm(settings)
            {
                Changed = SettingsChanged, TestDelivery = () => ArmTest(false), OpenSetup = ShowSetup,
                CopyDiagnostics = CopyDiagnostics, ClearRecent = ClearRecovery, PauseChanged = SetPaused, Paused = () => !enabled
            };
        }
        setup.SetTarget(lastRemote?.Kind, lastRemote?.Device); setup.SetStatus(message);
        recoveryWindow?.Hide(); checklist?.Hide(); hud.HideHud(); testArmed = false;
        setup.Show(); setup.SelectSection(0); setup.Activate();
    }
    private void CopyDiagnostics()
    {
        if (capture != null || deliveries.HasWork) { setup?.SetStatus("请先结束当前听写和投送，再复制诊断"); return; }
        var report = Diagnostics.Report($"settings tool={settings.ToolShortcut} source={settings.VoiceShortcut} auto={settings.AutoDeliver} speed={settings.Speed} restore={settings.RestoreClipboard} pasteDefault={settings.PasteKey} devices={settings.DevicePasteKeys.Count} calibrations={settings.Calibrations.Count} mouseHook={mouse != null}");
        try { Clipboard.SetText(report); setup?.SetStatus("已复制投送诊断；不含正文、剪贴板内容或窗口标题"); }
        catch (System.Runtime.InteropServices.ExternalException) { setup?.SetStatus("剪贴板暂时被占用，请稍后再复制诊断"); }
    }
    private void ShowRecovery()
    {
        if (!CanOpenManager()) return;
        recovery.Prune(clock.Now);
        if (recoveryWindow == null)
        {
            recoveryWindow = new RecoveryForm(); recoveryWindow.SetKey(Key);
            recoveryWindow.CanRestore = id => enabled && capture == null && !deliveries.IsInjecting && !deliveries.IsSending(id) && recovery.Records.Any(x => x.Id == id);
            recoveryWindow.ClearRecords = ClearRecovery;
            recoveryWindow.RestoreRecord = RestoreRecord;
            recoveryWindow.RedeliverRecord = Redeliver;
        }
        setup?.Hide(); checklist?.Hide(); hud.HideHud();
        recoveryWindow.RefreshRecords(recovery.Records, clock.Now); recoveryWindow.Show(); recoveryWindow.Activate();
    }
    private void ShowSetup()
    {
        if (!CanOpenManager()) return;
        if (checklist == null)
        {
            checklist = new SetupForm();
            checklist.VoiceKeyConfirmed = value => { settings.VoiceKeyConfirmed = value; settings.Save(); RefreshUi(); };
            checklist.ClipboardConfirmed = value => { settings.ClipboardConfirmed = value; settings.Save(); RefreshUi(); };
            checklist.UseSharedKey = () =>
            {
                // The recommended pairing: two presses in UU, delivered automatically.
                settings.ToolShortcut = ToolShortcut.SharedRightCtrl; settings.VoiceShortcut = "manual"; settings.AutoDeliver = true;
                settings.VoiceKeyConfirmed = false; settings.Save(); SettingsChanged();
                SetStatus("已改为右 Ctrl 与豆包同键，结束后自动投送");
            };
            checklist.TestDelivery = () => ArmTest(true);
            checklist.Start = CompleteSetup;
            // Closing a fully ready checklist is the same as starting.
            checklist.Dismissed = () => { if (SetupNow().Ready && !testArmed && !settings.OnboardingCompleted) { settings.OnboardingCompleted = true; settings.Save(); } };
        }
        setup?.Hide(); recoveryWindow?.Hide(); hud.HideHud(); testArmed = false;
        checklist.SetKey(Key); checklist.Update(SetupNow()); checklist.Show(); checklist.Activate();
    }
    private void CompleteSetup()
    {
        settings.OnboardingCompleted = true;
        if (!settings.Save()) { SetStatus("准备完成，但无法保存设置，下次启动可能再次显示清单。"); return; }
        SetStatus($"已就绪：回到远端，按 {Key} 开始听写");
        hud.ShowHud($"准备就绪。回到远端输入框，按 {Key} 开始", kind: HudTone.Success, duration: 4);
    }
    private void Quit()
    {
        if (quitting) return;
        if (capture != null && DoubaoProbe.Recording())
        {
            composer.FocusInput(); composer.SetMessage("请先结束豆包录音，再从托盘退出。"); return;
        }
        capture = null; destination = null; cancelling = false; composer.ClearAndHide();
        hud.HideHud(); quitting = true; enabled = false; deliveries.Suspended = true; deliveries.CancelAll("工具退出");
        void Complete()
        {
            if (deliveries.HasWork) { clock.After(.15, Complete); return; }
            recovery.Clear(); hook.Dispose(); mouse?.Dispose(); polling.Dispose(); tray.Visible = false; tray.Dispose(); menu.Dispose();
            composer.Dispose(); hud.Dispose(); setup?.Dispose(); recoveryWindow?.Dispose(); checklist?.Dispose();
            foreach (var icon in trayIcons.Values) icon.Dispose();
            clock.Dispose(); ExitThread();
        }
        Complete();
    }
}
