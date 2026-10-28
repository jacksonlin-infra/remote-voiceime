using RemoteVoiceInput.Core;
using RemoteVoiceInput.Windows;
using System.Runtime.InteropServices;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    count++; Console.WriteLine("PASS: " + name);
}

Check(CaptureShortcut.Route(false, false, true) == CaptureShortcutAction.Begin, "idle F8 in remote begins capture");
Check(CaptureShortcut.Route(true, true, false) == CaptureShortcutAction.Advance, "receiver F8 advances existing capture");
Check(CaptureShortcut.Route(true, false, true) == CaptureShortcutAction.Resume, "F8 after returning to original remote resumes receiver without toggling speech or delivering");
Check(CaptureShortcut.Route(true, false, false) == CaptureShortcutAction.Ignore, "active capture cannot bind F8 in unrelated or changed target");
Check(CaptureShortcut.Route(false, true, false) == CaptureShortcutAction.Ignore, "receiver without capture cannot begin an unbound session");

Check(CaptureShortcut.PassToSource(CaptureShortcutAction.Advance, CapturePhase.Listening), "finish key during listening also reaches Doubao so one press ends recording");
Check(!CaptureShortcut.PassToSource(CaptureShortcutAction.Advance, CapturePhase.Review) && !CaptureShortcut.PassToSource(CaptureShortcutAction.Begin, null) && !CaptureShortcut.PassToSource(CaptureShortcutAction.Resume, CapturePhase.Listening), "opening in UU, resuming and confirming keep the key away from the remote and Doubao");
Check(!SharedKey.OpeningPasses, "the opening press in UU and Remote Desktop is kept: never sent to the remote, never shown to Doubao in a window where it is not the input method");
Check(SharedKey.Decide(sourceRecording: true, hasText: false) == SharedPress.StopSource && SharedKey.Decide(true, true) == SharedPress.StopSource, "while Doubao's recording window shows, the press stops Doubao (silent recording included)");
Check(SharedKey.Decide(false, false) == SharedPress.StartSource && SharedKey.Decide(false, true) == SharedPress.Deliver, "not recording: no text starts Doubao, existing text is sent");
Check(SharedKey.Passes(SharedPress.StartSource) && SharedKey.Passes(SharedPress.StopSource) && !SharedKey.Passes(SharedPress.Deliver), "start / stop reach Doubao; the send press is kept so it cannot restart Doubao");
{ var s = new CaptureSession(0); s.Stop(3); s.Review(); s.Resume(); Check(s.Phase == CapturePhase.Listening && s.StoppedAt == null && !s.ReadyToReview(100, false), "Doubao restarted in the same receiver returns to listening without finalizing"); }
Check(ClipboardBridge.Classify(2, null, true, dibAvailable: true) == ClipboardFormatPolicy.Skip && ClipboardBridge.Classify(9, null, true, dibAvailable: true) == ClipboardFormatPolicy.Skip && ClipboardBridge.Classify(17, null, true, dibAvailable: true) == ClipboardFormatPolicy.Copy, "copied images are backed up via DIB; the system-synthesized bitmap is not required");
Check(ClipboardBridge.Classify(2, null, true) == ClipboardFormatPolicy.Reject && ClipboardBridge.Classify(14, null, true, dibAvailable: true) == ClipboardFormatPolicy.Reject, "bitmap without DIB and metafiles still refuse to overwrite");
Check(ToolShortcut.VirtualKey(ToolShortcut.SharedRightCtrl) == 0xa3 && ToolShortcut.ConflictsWithSource(ToolShortcut.SharedRightCtrl, "right-control") && !ToolShortcut.ConflictsWithSource(ToolShortcut.SharedRightCtrl, "manual") && ToolShortcut.ConflictsWithSource(ToolShortcut.RightAlt, "right-alt"), "shared right Ctrl requires the manual Doubao source");
Check(CaptureSession.AutoDeliver(true, "测试 Remote VoiceIME 123，好！") && !CaptureSession.AutoDeliver(false, "测试"), "plain final text auto-delivers only when enabled");
Check(!CaptureSession.AutoDeliver(true, " \r\n") && !CaptureSession.AutoDeliver(true, "第一行\r\n第二行") &&!CaptureSession.AutoDeliver(true, new string('字', 30000)), "empty, multiline or oversized text always stops for review");
{ var s = new CaptureSession(0); s.Stop(10); s.Changed(10.5); Check(!s.ReadyToReview(11.4, false, 1) && s.ReadyToReview(11.5, false, 1), "auto delivery waits a full settle interval after the last text change"); }
{ var s = new CaptureSession(0); s.Stop(10); s.Changed(10.2); Check(!s.ReadyToReview(10.6, false, CaptureSession.AutoSettle) && s.ReadyToReview(10.7, false, CaptureSession.AutoSettle) && !s.ReadyToReview(12, true, CaptureSession.AutoSettle), "auto settle is 0.5 s after the last committed text and never during composition"); }
Check(RemoteClients.SyncDelay(RemoteClientKind.RemoteDesktop, SyncSpeed.Compatible) == .3 && RemoteClients.SyncDelay(RemoteClientKind.UU, SyncSpeed.Compatible) == 1.2 && RemoteClients.SyncDelay(RemoteClientKind.UU, SyncSpeed.Standard) == .5 && RemoteClients.SyncDelay(RemoteClientKind.UU, SyncSpeed.Fast) == .18, "Remote Desktop uses a short clipboard wait; UU uses fast 180 ms / standard 0.5 s / compatible 1.2 s");

Check(RemoteClients.Classify("mstsc.exe", "TscShellContainerClass", false) == RemoteClientKind.RemoteDesktop && RemoteClients.Classify("MSTSC.EXE", "TscShellContainerClass", false) == RemoteClientKind.RemoteDesktop, "Remote Desktop session window is a delivery target");
Check(RemoteClients.Classify("mstsc.exe", "#32770", false) == null && RemoteClients.Classify("notepad.exe", "Notepad", false) == null, "mstsc connection dialog and unrelated apps are never targets");
Check(RemoteClients.Classify("GameViewer.exe", "Qt51512QWindowIcon", true) == RemoteClientKind.UU && RemoteClients.Classify("UURemote.exe", "x", false) == RemoteClientKind.UU, "UU stays matched by name or the chosen executable");
Check(RemoteClients.Paste(RemoteClientKind.RemoteDesktop, PasteShortcut.WinV) is ({ } rdpKeys, true) && rdpKeys.SequenceEqual(new ushort[] { 0x11, 0x56 }) && RemoteClients.Paste(RemoteClientKind.UU, PasteShortcut.WinV) is ({ } uuKeys, false) && uuKeys[0] == 0x5b, "Remote Desktop pastes Ctrl+V with scan codes while UU keeps its configured key");
var rdpPaste = KeyboardInjector.Events([0x11, 0x56], withScan: true);
Check(rdpPaste[0].Data.Key.Scan == 0x1d && rdpPaste[1].Data.Key.Scan == 0x2f && rdpPaste.All(x => (x.Data.Key.Flags & Native.ScanCode) == 0 && x.Data.Key.Extra == Native.Marker) && KeyboardInjector.ReleasesAfterPartial(rdpPaste, 2).All(x => x.Data.Key.Scan != 0), "Remote Desktop Ctrl+V carries hardware scan codes, also in partial-release cleanup");
Check(KeyboardInjector.Events([0x11, 0x56]).All(x => x.Data.Key.Scan == 0), "UU paste events stay unchanged (virtual keys only)");

var gate = new ShortcutGate();
Check(!gate.Key(true, false, 1).Consume && !gate.Key(false, false, 1).Consume, "F8 in unrelated applications remains untouched");
Check(gate.Key(true, true, 1) == (true, false), "remote F8 down waits for release");
Check(!gate.Key(true, true, 1).Activate, "held F8 never repeats");
Check(gate.Key(false, true, 1) == (true, true), "F8 release activates once");
gate.Key(true, true, 1);
Check(gate.Key(false, true, 2) == (true, false), "focus change between down and up cancels gesture");
gate.Key(true, true, 1);
Check(gate.Key(false, false, 1) == (true, false), "disabled shortcut still consumes its captured release");
var session = new CaptureSession(0);
Check(!session.ReadyToReview(10, false), "speech pause alone cannot end listening");
session.Stop(10); session.Changed(10.2);
Check(!session.ReadyToReview(10.5, false), "wait for final text after stop request");
Check(!session.ReadyToReview(12, true), "unfinished IME composition blocks review readiness");
Check(session.ReadyToReview(12, false), "stable stopped text can enter review");
session.Review();
Check(session.Phase == CapturePhase.Review && !session.ReadyToReview(20, false), "review never automatically submits or repeats");
Check(new CaptureSession(0).Id != new CaptureSession(0).Id, "capture callbacks can identify obsolete sessions");
Check(TextPolicy.IsEmpty(" \r\n\t") && !TextPolicy.IsEmpty("你好"), "blank text not deliverable");
Check(TextPolicy.NeedsReview("第一行\r\n第二行") && TextPolicy.NeedsReview("\u001b[A"), "control and multiline text recognized");
Check(!TextPolicy.NeedsReview("👩🏽‍💻 e\u0301 中文"), "ZWJ and combining Unicode remain ordinary text");
Check(!TextPolicy.SizeAllowed(new string('字', 30000)), "64 KiB payload bound counts UTF8 bytes");

Check(Marshal.SizeOf<Native.Input>() == 40 && Marshal.OffsetOf<Native.Input>(nameof(Native.Input.Data)) == 8, "Win64 INPUT struct has correct size and union alignment");
Check(Marshal.SizeOf<Native.HookKey>() == 24, "Win64 low-level keyboard hook struct has correct layout");
var pasteEvents = KeyboardInjector.Events([0x11, 0x56]);
Check(pasteEvents.Select(x => x.Data.Key.Vk).SequenceEqual(new ushort[] { 0x11, 0x56, 0x56, 0x11 }), "Ctrl+V includes all key down and up events");
Check(pasteEvents.Select(x => x.Data.Key.Flags).SequenceEqual(new uint[] { 0, 0, 2, 2 }), "paste flags distinguish key release");
Check(pasteEvents.All(x => x.Type == 1 && x.Data.Key.Extra == Native.Marker), "own events consistently marked to prevent shortcut recursion");
Check(KeyboardInjector.ReleasesAfterPartial(pasteEvents, 0).Length == 0, "zero inserted events need no modifier cleanup");
Check(KeyboardInjector.ReleasesAfterPartial(pasteEvents, 1).Select(x => x.Data.Key.Vk).SequenceEqual(new ushort[] { 0x11 }), "failure before V releases only Ctrl");
Check(KeyboardInjector.ReleasesAfterPartial(pasteEvents, 2).Select(x => x.Data.Key.Vk).SequenceEqual(new ushort[] { 0x56, 0x11 }), "failure after V down releases V and Ctrl in reverse order");
Check(KeyboardInjector.ReleasesAfterPartial(pasteEvents, 3).Select(x => x.Data.Key.Vk).SequenceEqual(new ushort[] { 0x11 }), "already released V is not released twice");
Check(KeyboardInjector.ReleasesAfterPartial(pasteEvents, 4).Length == 0, "complete paste needs no cleanup");
Check(PasteShortcut.Keys(PasteShortcut.WinV).SequenceEqual(new ushort[] { 0x5b, 0x56 }) && PasteShortcut.Keys(PasteShortcut.CtrlV).SequenceEqual(new ushort[] { 0x11, 0x56 }) && PasteShortcut.Keys("unknown").SequenceEqual(new ushort[] { 0x11, 0x56 }), "paste key maps Mac remote to Win+V and keeps Ctrl+V default");
var devices = new Dictionary<string, string> { ["MacBook"] = PasteShortcut.WinV, ["Broken"] = "bogus" };
Check(PasteShortcut.For(devices, "MacBook", PasteShortcut.CtrlV) == PasteShortcut.WinV && PasteShortcut.For(devices, "OFFICE-WIN", PasteShortcut.CtrlV) == PasteShortcut.CtrlV, "UU paste key is remembered per device; unknown devices use the default");
Check(PasteShortcut.For(devices, "Broken", PasteShortcut.WinV) == PasteShortcut.WinV && PasteShortcut.For(null, null, "bogus") == PasteShortcut.CtrlV, "invalid remembered or default keys fall back safely");
Check(PasteShortcut.For(devices, "MacMini-M4", PasteShortcut.CtrlV) == PasteShortcut.WinV && PasteShortcut.For(devices, "Alex's MacBook Pro", PasteShortcut.CtrlV) == PasteShortcut.WinV && PasteShortcut.For(devices, "iMac", PasteShortcut.CtrlV) == PasteShortcut.WinV, "a new UU device named like a Mac starts with Win+V");
Check(PasteShortcut.For(devices, "MacroPC", PasteShortcut.CtrlV) == PasteShortcut.CtrlV && PasteShortcut.For(devices, "OFFICE-PC", PasteShortcut.CtrlV) == PasteShortcut.CtrlV && PasteShortcut.For(new Dictionary<string, string> { ["MacMini-M4"] = PasteShortcut.CtrlV }, "MacMini-M4", PasteShortcut.WinV) == PasteShortcut.CtrlV, "other names use the default; a remembered key always wins over the name hint");
Check(!PasteShortcut.Remembered(devices, "MacMini-M4") && PasteShortcut.Remembered(devices, "MacBook") && PasteShortcut.Other(PasteShortcut.CtrlV) == PasteShortcut.WinV, "an unremembered device can be offered the other paste key");
var winPaste = KeyboardInjector.Events(PasteShortcut.Keys(PasteShortcut.WinV));
Check(winPaste.Select(x => x.Data.Key.Vk).SequenceEqual(new ushort[] { 0x5b, 0x56, 0x56, 0x5b }) && (winPaste[0].Data.Key.Flags & Native.Extended) != 0 && (winPaste[3].Data.Key.Flags & Native.KeyUp) != 0, "Win+V releases V before the extended Win key");
var altDown = KeyboardInjector.SourceEvent(0xa5, false).Data.Key; var altUp = KeyboardInjector.SourceEvent(0xa5, true).Data.Key;
Check(altDown.Scan == 0x38 && (altDown.Flags & (Native.ScanCode | Native.Extended)) == (Native.ScanCode | Native.Extended) && (altDown.Flags & Native.KeyUp) == 0 && (altUp.Flags & Native.KeyUp) != 0 && altDown.Extra == Native.Marker, "right Alt source key carries E0 38 hardware scan code and stays marked (read-only key map)");
Check(KeyboardInjector.Events([0xa3]).All(x => (x.Data.Key.Flags & Native.Extended) != 0), "right Ctrl source shortcut carries extended-key flag");
Check(ClipboardBridge.Classify(0xc0cd, "EnterpriseDataProtectionId", false) == ClipboardFormatPolicy.Skip, "advertised clipboard marker without data does not abort delivery");
Check(ClipboardBridge.Classify(0xc009, "DataObject", true) == ClipboardFormatPolicy.Skip && ClipboardBridge.Classify(0xc013, "Ole Private Data", true) == ClipboardFormatPolicy.Skip, "OLE private clipboard state is not copied as raw bytes");
Check(ClipboardBridge.Classify(13, null, true) == ClipboardFormatPolicy.Copy && ClipboardBridge.Classify(0xc0a1, "HTML Format", true) == ClipboardFormatPolicy.Copy, "text and registered data formats are backed up for restoration");
Check(ClipboardBridge.SameText("第一行\n第二行", "第一行\r\n第二行\0") && ClipboardBridge.SameText("  缩进 é ", "  缩进 é "), "identical re-publish by a sync tool (Deskflow/UU) keeps the delivery lease");
Check(!ClipboardBridge.SameText("new user copy", "听写正文") && !ClipboardBridge.SameText(null, "听写正文") && !ClipboardBridge.SameText("听写正文 ", "听写正文"), "different or unreadable content still counts as the user's new copy");
Check(ClipboardBridge.Classify(2, null, true) == ClipboardFormatPolicy.Reject && ClipboardBridge.Classify(8, null, true) == ClipboardFormatPolicy.Copy && ClipboardBridge.Classify(14, null, false) == ClipboardFormatPolicy.Reject, "image handles still refuse to overwrite instead of downgrading");

{
    var r = new Rig(); var payload = "  缩进 e\u0301 👩🏽‍💻\r\n";
    r.Submit(payload); Check(r.Queue.IsInjecting && r.Clipboard.Value == payload, "clipboard receives exact whitespace and Unicode");
    r.Clock.Advance(.2);
    Check(r.Results.Count == 1 && r.Results[0].Attempted && r.Queue.IsInjecting, "paste keys get a short quiet period before focus may move");
    r.Clock.Advance(.2);
    Check(!r.Queue.IsInjecting, "sent paste unconfirmed; next capture allowed during restoration hold");
    r.Capturing = true; r.Submit("second"); r.Clock.Advance(2.8);
    Check(r.Clipboard.Value == "original" && r.Queue.PendingCount == 1 && r.Emitter.Calls == 1, "restoration does not inject into a new local capture");
    r.Capturing = false; r.Queue.Pump(); r.Clock.Advance(.2);
    Check(r.Clipboard.Value == "second" && r.Emitter.Calls == 2, "next delivery follows capture completion");
    r.Clock.Advance(3);
    Check(r.Clipboard.Value == "original", "consecutive leases restore original not previous transcript");
}
{
    var r = new Rig(); r.Submit(); r.Clock.Advance(.2); r.Clipboard.Copy("new user copy"); r.Clock.Advance(3);
    Check(r.Clipboard.Value == "new user copy" && r.Clipboard.Restores == 0, "new user copy survives delayed restore");
}
{
    var r = new Rig(); r.Submit(); r.Clipboard.Copy("copy during sync"); r.Clock.Advance(.2);
    Check(r.Emitter.Calls == 0 && !r.Results[0].Attempted, "clipboard change prevents paste");
    Check(r.Clipboard.Value == "copy during sync", "aborted paste preserves user clipboard");
}
{
    var r = new Rig(); r.Submit(); r.Target.Foreground = false; r.Clock.Advance(.2);
    Check(r.Emitter.Calls == 0 && r.Results.Count == 0, "slow activation waits instead of failing at the sync deadline");
    r.Clock.Advance(2);
    Check(r.Emitter.Calls == 0 && r.Clipboard.Value == "original" && r.Traces.Any(x => x.Contains("blocked=focus")), "focus that never returns stops after the grace period, restores and names the focus gate");
}
{
    var r = new Rig(); r.Submit(); r.Target.Valid = false; r.Clock.Advance(2);
    Check(r.Emitter.Calls == 0 && !r.Queue.HasWork && r.Traces.Any(x => x.Contains("blocked=window")), "closed or changed remote window cannot receive paste");
}
{
    var r = new Rig(); r.Queue.ModifiersClear = () => false; r.Submit(); r.Clock.Advance(2);
    Check(r.Emitter.Calls == 0 && r.Results.Count == 1 && r.Traces.Any(x => x.Contains("blocked=modifiers")), "physical modifiers prevent paste chord interference");
}
{
    var r = new Rig(); r.Clipboard.Fails = true; r.Submit();
    Check(r.Clipboard.Value == "original" && r.Emitter.Calls == 0 && r.Results.Count == 1, "backup failure leaves original untouched and reports recoverable result");
}
{
    var r = new Rig(); r.Target.Foreground = false; r.Target.Activates = false; r.Submit();
    Check(r.Target.Activations == 1 && r.Queue.HasWork && r.Results.Count == 0, "a refused activation is not a failure by itself");
    r.Clock.Advance(.5); r.Target.Foreground = true; r.Clock.Advance(.2);
    Check(r.Emitter.Calls == 1 && r.Results[0].Attempted && r.Target.Activations == 1, "the gate delivers once the window does come forward, without activating again");
}
{
    var r = new Rig(); r.Queue.MayActivate = _ => false; r.Submit();
    Check(r.Clipboard.Acquisitions == 0 && r.Target.Activations == 0, "unrelated foreground prevents stealing focus or clipboard");
}
{
    var r = new Rig(); r.Emitter.Result = PasteResult.NotSent; r.Submit(); r.Clock.Advance(.2);
    Check(!r.Results[0].Attempted && r.Clipboard.Value == "original", "SendInput failure retains recoverable draft");
}
{
    var r = new Rig(); r.Submit("old"); r.Queue.CancelAll("cancel"); r.Submit("new"); r.Clock.Advance(.2);
    Check(r.Results.Count == 2 && r.Clipboard.Value == "new" && r.Emitter.Calls == 1, "cancelled synchronization callback cannot inject into next task");
    r.Clock.Advance(3); Check(r.Clipboard.Value == "original", "cancel and retry keep original clipboard ownership");
}
{
    var r = new Rig(); r.Submit(); r.Clock.Advance(.2); r.Queue.CancelAll("cancel after paste"); r.Clock.Advance(5);
    Check(r.Results.Count == 1 && r.Results[0].Attempted && r.Clipboard.Restores == 1, "cancel after paste never claims it was not sent or restores twice");
}
{
    var r = new Rig(); r.Capturing = true; var job = r.Job();
    Check(r.Queue.Enqueue(job) && !r.Queue.Enqueue(job), "duplicate ID cannot queue twice");
    Check(r.Queue.Enqueue(r.Job()) && r.Queue.Enqueue(r.Job()) && !r.Queue.Enqueue(r.Job()), "waiting queue bounded to three entries");
    r.Queue.Suspended = true; r.Capturing = false; r.Queue.Pump();
    Check(r.Clipboard.Acquisitions == 0, "pause blocks drain");
    r.Queue.CancelAll("paused"); r.Queue.Suspended = false; r.Queue.Pump();
    Check(r.Results.Count == 3 && r.Emitter.Calls == 0, "cancelled backlog is never replayed on resume");
}
{
    var r = new Rig(); r.Submit(); r.Clock.Advance(.2); r.Clipboard.BusyReleases = 2; r.Clock.Advance(3.05);
    Check(r.Queue.HasWork && r.Clipboard.Value == "hello", "temporarily locked clipboard keeps restore attempt alive");
    r.Clock.Advance(.3); Check(!r.Queue.HasWork && r.Clipboard.Value == "original", "bounded restoration retry succeeds once clipboard unlocks");
}
{
    var r = new Rig(); r.Submit(); r.Clock.Advance(.2); r.Clipboard.BusyReleases = 20; r.Clock.Advance(5);
    Check(!r.Queue.HasWork && r.Notices.Count == 1, "restore timeout is bounded and surfaced to user");
}
{
    var store = new RecoveryStore(); var target = new FakeTarget();
    for (var i = 0; i < 7; i++) store.Save(new(Guid.NewGuid(), "draft " + i, target, i, RecoveryStatus.Unconfirmed, "unconfirmed"), i);
    Check(store.Records.Count == 5 && store.Records[0].Text == "draft 6", "recovery bounded to newest five records");
    var record = store.Records[0]; store.Save(record with { Status = RecoveryStatus.Failed }, 7);
    Check(store.Records.Count == 5 && store.Records[0].Text == record.Text, "outcome update preserves text without duplicating recovery entry");
    store.Prune(305); Check(store.Records.Count == 1, "five-minute expiry removes old recovery records");
    store.Prune(306); Check(store.Records.Count == 0, "expiry includes exact boundary");
    store.Save(record, 6); store.Clear(); Check(store.Records.Count == 0, "explicit clear discards stored drafts");
}
// 0.3.0: waiting gate, kept clipboard, client echo, re-delivery during the hold.
{
    var r = new Rig(); var held = true; r.Queue.ModifiersClear = () => !held;
    r.Submit(); r.Clock.Advance(.6);
    Check(r.Emitter.Calls == 0 && r.Results.Count == 0, "held modifier delays the paste instead of failing");
    held = false; r.Clock.Advance(.1);
    Check(r.Emitter.Calls == 1 && r.Results[0].Attempted, "paste proceeds once the modifier is released");
}
{
    var r = new Rig(); r.Submit(); r.Clock.Advance(.05); r.Target.Foreground = false; r.Clock.Advance(.1); r.Target.Foreground = true; r.Clock.Advance(.06);
    Check(r.Emitter.Calls == 0, "the window must stay in front for 0.1 s before the paste");
    r.Clock.Advance(.1);
    Check(r.Emitter.Calls == 1, "a stable foreground window receives the paste");
}
{
    var r = new Rig(); r.Submit("同样的文字"); r.Clipboard.Copy("同样的文字"); r.Clock.Advance(.3);
    Check(r.Emitter.Calls == 1 && r.Traces.Any(x => x.Contains("clipboard-republished")), "a client republishing the same text keeps the delivery");
    r.Clock.Advance(3);
    Check(r.Clipboard.Value == "original", "republished lease still restores the original clipboard");
}
{
    var r = new Rig(); r.Queue.UserActedSince = _ => false;
    r.Submit("新的听写", keep: true); r.Clipboard.Copy("远端旧剪贴板"); r.Clock.Advance(.1);
    Check(r.Clipboard.Value == "新的听写" && r.Traces.Any(x => x.Contains("clipboard-reclaimed")), "client echo without user input is rewritten once");
    Check(r.Emitter.Calls == 0, "rewritten clipboard waits a full sync again");
    r.Clock.Advance(.3);
    Check(r.Emitter.Calls == 1 && r.Clipboard.Value == "新的听写", "rewritten delivery pastes the new transcript, not the stale one");
}
{
    var r = new Rig(); r.Queue.UserActedSince = _ => false;
    r.Submit("新的听写"); r.Clipboard.Copy("回声 1"); r.Clock.Advance(.1); r.Clipboard.Copy("回声 2"); r.Clock.Advance(.1);
    Check(r.Emitter.Calls == 0 && r.Results.Count == 1 && r.Traces.Any(x => x.Contains("blocked=clipboard")), "a second clipboard change stops instead of fighting the client");
}
{
    var r = new Rig(); r.Queue.UserActedSince = _ => true;
    r.Submit("听写", keep: true); r.Clipboard.Copy("用户新复制"); r.Clock.Advance(.3);
    Check(r.Emitter.Calls == 0 && r.Clipboard.Value == "用户新复制" && r.Clipboard.Writes == 1, "content copied by the user is never overwritten");
}
{
    var r = new Rig(); r.Submit(); r.Clipboard.Unreadable = true; r.Clipboard.Generation++; r.Clock.Advance(.4);
    Check(r.Emitter.Calls == 0 && r.Results.Count == 0, "unreadable clipboard (delayed rendering) waits instead of failing");
    r.Clipboard.Unreadable = false; r.Clipboard.Value = "hello"; r.Clock.Advance(.1);
    Check(r.Emitter.Calls == 1, "delivery continues once the republished text is readable");
}
{
    var r = new Rig(); r.Submit("保留在剪贴板", keep: true); r.Clock.Advance(4);
    Check(r.Clipboard.Value == "保留在剪贴板" && r.Clipboard.Restores == 0, "by default the transcript stays on the clipboard for a manual remote paste");
    var failed = new Rig(); failed.Target.Foreground = failed.Target.Activates = false; failed.Submit("未投送也保留", keep: true); failed.Clock.Advance(3);
    Check(failed.Results.Count == 1 && failed.Clipboard.Value == "未投送也保留", "a stopped paste leaves the transcript on the clipboard too");
}
{
    var r = new Rig(); r.Submit("保留", keep: true); r.Clock.Advance(.3);
    var next = r.Job("下一条", keep: true); Check(r.Queue.Enqueue(next), "next job queues behind a kept clipboard");
    r.Clock.Advance(1.2);
    Check(r.Emitter.Calls == 2 && r.Clipboard.Value == "下一条", "a kept clipboard holds only 0.8 s, so the next paste is not delayed 3 s");
}
{
    var r = new Rig(); var first = r.Job("第一条"); r.Submit(first); r.Clock.Advance(.3);
    Check(r.Queue.Contains(first.Id) && !r.Queue.IsSending(first.Id), "a pasted job only holding the clipboard is no longer sending");
    var again = new DeliveryJob(Guid.NewGuid(), first.Text, r.Target, .18, RestoreClipboard: true, FocusTarget: true);
    Check(r.Queue.Enqueue(again), "re-delivery during the hold is accepted as a fresh job");
    r.Clock.Advance(3.5);
    Check(r.Emitter.Calls == 2 && r.Results.Count(x => x.Attempted) == 2, "re-delivery runs once after the hold");
    r.Clock.Advance(3);
    Check(r.Clipboard.Value == "original", "re-delivery still restores the original clipboard, not the first attempt");
}
{
    var r = new Rig(); r.Capturing = true; var waiting = r.Job(); r.Queue.Enqueue(waiting);
    Check(r.Queue.IsSending(waiting.Id) && !r.Queue.IsActive(waiting.Id), "a queued job is sending but not yet active");
}
{
    var r = new Rig(); r.Queue.MayActivate = new DeliveryQueue(r.Clock, r.Clipboard, r.Emitter).MayActivate; r.Target.Foreground = false;
    r.Submit(r.Job("自动") with { FocusTarget = false });
    Check(r.Clipboard.Acquisitions == 0 && r.Results.Count == 1 && !r.Results[0].Attempted, "a plain job never steals focus from an unrelated app");
    r.Submit(r.Job("明确投送") with { FocusTarget = true }); r.Clock.Advance(.4);
    Check(r.Target.Activations == 1 && r.Emitter.Calls == 1, "an explicit confirm or re-delivery may bring its window forward, activated once by the queue");
}
{
    var r = new Rig(); r.Submit(); r.Clock.Advance(.4);
    Check(r.Target.Activations == 0 && r.Emitter.Calls == 1, "a client already in front is not activated again");
}
{
    var r = new Rig(); var changes = 0; r.Queue.Changed = () => changes++;
    r.Submit(); var before = changes; r.Clock.Advance(.2); var atPaste = changes; r.Clock.Advance(.2);
    Check(before > 0 && atPaste > before && changes > atPaste && !r.Queue.IsInjecting, "queue reports when injection ends so a waiting press can start");
}
{
    var r = new Rig(); r.Submit("取消", keep: true); r.Queue.CancelAll("cancel"); r.Clock.Advance(1);
    Check(r.Emitter.Calls == 0 && r.Results.Count == 1 && !r.Queue.HasWork, "cancelling during sync never sends the paste");
}
Check(SettleRule.NoSpeech(" ", false, false, 3) && !SettleRule.NoSpeech(" ", false, false, 1), "stopped recording without text ends the round after 2.5 s, not sooner");
Check(!SettleRule.NoSpeech("", false, true, 5) && !SettleRule.NoSpeech("", true, false, 5) && !SettleRule.NoSpeech("有字", false, false, 5), "still recording, composing or received text is never treated as no speech");
Check(SyncSpeed.Migrate(null, true) == SyncSpeed.Compatible && SyncSpeed.Migrate(null, false) == SyncSpeed.Fast && SyncSpeed.Migrate(null, null) == SyncSpeed.Standard && SyncSpeed.Migrate("standard", true) == SyncSpeed.Standard, "0.2 compatibility setting migrates without loss; new installs start at standard");
Check(SyncSpeed.ForJob(.18, null, 10) == .8 && SyncSpeed.ForJob(.18, 0, 200) == .8 && SyncSpeed.ForJob(.18, 100, 200) == .18 && SyncSpeed.ForJob(1.2, null, 0) == 1.2, "the first paste after two quiet minutes waits at least 0.8 s");
{
    var stored = new Dictionary<string, string>();
    var mac = Calibration.Key(RemoteClientKind.UU, "MacBook"); var win = Calibration.Key(RemoteClientKind.UU, "OFFICE-WIN");
    var macProfile = Calibration.Profile(RemoteClientKind.UU, PasteShortcut.WinV, SyncSpeed.Standard);
    Check(Calibration.Read(stored, mac, macProfile) == CalibrationResult.Untested, "devices start untested");
    stored[mac] = Calibration.Encode(true, macProfile);
    Check(Calibration.Read(stored, mac, macProfile) == CalibrationResult.Passed && Calibration.Read(stored, win, macProfile) == CalibrationResult.Untested, "a passed test is kept per UU device");
    Check(Calibration.Read(stored, mac, Calibration.Profile(RemoteClientKind.UU, PasteShortcut.CtrlV, SyncSpeed.Standard)) == CalibrationResult.Untested && Calibration.Read(stored, mac, Calibration.Profile(RemoteClientKind.UU, PasteShortcut.WinV, SyncSpeed.Compatible)) == CalibrationResult.Untested, "changing paste key or speed shows untested again");
    stored[mac] = Calibration.Encode(false, macProfile);
    Check(Calibration.Read(stored, mac, macProfile) == CalibrationResult.Failed && Calibration.Key(RemoteClientKind.RemoteDesktop, "x") == Calibration.RemoteDesktopProfile, "a failed test is remembered; Remote Desktop has one profile");
}
{
    var status = new SetupStatus(true, true, SharedKey: true, VoiceKeyConfirmed: false, [], ClipboardConfirmed: true, null);
    Check(!status.Ready && status.RequiredDone == 2, "the shared key needs the Doubao voice key confirmed");
    Check((status with { VoiceKeyConfirmed = true }).Ready && (status with { SharedKey = false }).Ready, "all required items make setup ready without a test or a running client");
    Check(!(status with { VoiceKeyConfirmed = true, DoubaoDetected = false }).Ready && !(status with { VoiceKeyConfirmed = true, ClipboardConfirmed = false }).Ready, "Doubao and clipboard sync stay required");
}
Check(SharedKey.Decide(false, false) == SharedPress.StartSource && SharedKey.Passes(SharedPress.StartSource), "in the floatbar the next press reaches Doubao and starts recording");
Console.WriteLine($"All {count} Windows core checks passed. No native keys, clipboard, microphone or remote session accessed.");

sealed class FakeClock : IClock
{
    public double Now { get; private set; }
    private int sequence;
    private readonly List<(double At, int Order, Action Action)> work = [];
    public void After(double seconds, Action action) => work.Add((Now + seconds, sequence++, action));
    public void Advance(double seconds)
    {
        var end = Now + seconds;
        while (work.Count > 0)
        {
            var next = work.OrderBy(x => x.At).ThenBy(x => x.Order).First();
            if (next.At > end) break;
            work.Remove(next); Now = next.At; next.Action();
        }
        Now = end;
    }
}
sealed class FakeTarget : IRemoteTarget
{
    public string Label => "test remote";
    public bool Valid = true, Foreground = true, Activates = true;
    public int Activations;
    public bool IsValid => Valid;
    public bool IsForeground => Foreground;
    public bool Activate() { Activations++; if (Activates) Foreground = true; return Valid; }
}
sealed class FakeClipboard : IClipboardBridge
{
    public string Value = "original";
    public int Generation, Restores, Acquisitions, BusyReleases, Writes;
    public bool Fails, Unreadable;
    public void Copy(string text) { Value = text; Generation++; }
    public IClipboardLease Acquire(string text)
    {
        if (Fails) throw new InvalidOperationException("backup failed");
        Acquisitions++; var original = Value; Copy(text); Writes++;
        return new Lease(this, original, text);
    }
    private sealed class Lease(FakeClipboard board, string original, string text) : IClipboardLease
    {
        private bool released;
        private int generation = board.Generation;
        public LeaseState Check()
        {
            if (released) return LeaseState.Changed;
            if (generation == board.Generation) return LeaseState.Owned;
            if (board.Unreadable) return LeaseState.Unreadable;
            if (board.Value != text) return LeaseState.Changed;
            generation = board.Generation; return LeaseState.Republished;
        }
        public bool Rewrite() { board.Copy(text); board.Writes++; generation = board.Generation; return true; }
        public ReleaseResult Release(bool restore)
        {
            if (released) return ReleaseResult.Changed;
            if (!restore) { released = true; return ReleaseResult.Kept; }
            if (board.BusyReleases-- > 0) return ReleaseResult.Busy;
            released = true;
            if (generation != board.Generation && board.Value != text) return ReleaseResult.Changed;
            board.Copy(original); board.Restores++; return ReleaseResult.Restored;
        }
    }
}
sealed class FakeEmitter : IPasteEmitter
{
    public int Calls;
    public PasteResult Result = PasteResult.Attempted;
    public PasteResult Send(IRemoteTarget target) { Calls++; return Result; }
}
sealed class Rig
{
    public readonly FakeClock Clock = new();
    public readonly FakeClipboard Clipboard = new();
    public readonly FakeTarget Target = new();
    public readonly FakeEmitter Emitter = new();
    public readonly DeliveryQueue Queue;
    public readonly List<DeliveryOutcome> Results = [];
    public readonly List<string> Notices = [];
    public readonly List<string> Traces = [];
    public bool Capturing;
    public Rig()
    {
        Queue = new(Clock, Clipboard, Emitter) { CanDrain = () => !Capturing, MayActivate = _ => true,
            Outcome = (_, outcome) => Results.Add(outcome), Notice = Notices.Add, Trace = Traces.Add };
    }
    // Restoring jobs exercise the clipboard lease; keep=true is the 0.3 default.
    public DeliveryJob Job(string text = "hello", bool keep = false) => new(Guid.NewGuid(), text, Target, .18, RestoreClipboard: !keep);
    public void Submit(string text = "hello", bool keep = false) => Submit(Job(text, keep));
    public void Submit(DeliveryJob job)
    { if (!Queue.Enqueue(job)) throw new Exception("Unexpected queue rejection"); Queue.Pump(); }
}
