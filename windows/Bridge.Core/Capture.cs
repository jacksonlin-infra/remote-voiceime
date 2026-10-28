using System.Text;

namespace RemoteVoiceInput.Core;

public enum CapturePhase { Listening, Settling, Review }

public sealed class CaptureSession(double now, bool manual = false)
{
    public Guid Id { get; } = Guid.NewGuid();
    // Opened from recent text, not from a recording: no voice source to stop.
    public bool Manual { get; } = manual;
    public CapturePhase Phase { get; private set; } = manual ? CapturePhase.Review : CapturePhase.Listening;
    public double LastChange { get; private set; } = now;
    public double? StoppedAt { get; private set; }
    public void Changed(double now) => LastChange = now;
    public void Stop(double now)
    {
        if (Phase != CapturePhase.Listening) return;
        StoppedAt = now;
        Phase = CapturePhase.Settling;
    }
    public void Review() => Phase = CapturePhase.Review;
    // Doubao recording again in the same receiver (shared key); text keeps accumulating.
    public void Resume() { Phase = CapturePhase.Listening; StoppedAt = null; }
    // Windows IME end-composition is NOT a speech recognition completion ACK.
    // Stability only opens review; only the user's next action may submit text.
    public bool ReadyToReview(double now, bool composing, double settle = .8) => Phase == CapturePhase.Settling &&
        !composing && StoppedAt is double stop && now - stop >= settle && now - LastChange >= settle;
    // Like macOS: after the user's explicit finish key and a stable final text,
    // plain text is delivered without a second key. Anything unusual is reviewed.
    // Final text is committed at IME composition end; this only absorbs a late revision.
    public const double AutoSettle = .5;
    public static bool AutoDeliver(bool enabled, string text) => enabled && !TextPolicy.IsEmpty(text) && !TextPolicy.NeedsReview(text) && TextPolicy.SizeAllowed(text);
}

public static class TextPolicy
{
    public static bool IsEmpty(string text) => string.IsNullOrWhiteSpace(text);
    public static bool SizeAllowed(string text) => Encoding.UTF8.GetByteCount(text) <= 64 * 1024;
    public static bool NeedsReview(string text) => text.Any(c => c < 0x20 || c is >= (char)0x7f and <= (char)0x9f || c is '\u2028' or '\u2029');
}

public static class ToolShortcut
{
    public const string F8 = "f8", RightAlt = "right-alt", SharedRightCtrl = "right-ctrl-shared";
    public static bool IsKnown(string id) => id is F8 or RightAlt or SharedRightCtrl;
    public static uint VirtualKey(string id) => id switch { RightAlt => 0xa5u, SharedRightCtrl => 0xa3u, _ => 0x77u };
    public static string Label(string id) => id switch { RightAlt => "右 Alt", SharedRightCtrl => "右 Ctrl", _ => "F8" };
    // Experimental: the user's physical press is also Doubao's voice key, so the
    // gesture that opens the receiver starts recording too (2 presses like macOS).
    public static bool SharesSourceKey(string id) => id == SharedRightCtrl;
    // The receiver consumes the tool key, so the source may not start on that same key;
    // a shared key must leave the start to the user's physical press (manual source).
    public static bool ConflictsWithSource(string tool, string sourceShortcut) =>
        (tool == RightAlt && sourceShortcut == "right-alt") || (tool == SharedRightCtrl && sourceShortcut != "manual");
}

public static class PasteShortcut
{
    // UU sends Ctrl+V to a Windows remote; for a Mac remote UU maps Win to Command.
    public const string CtrlV = "ctrl-v", WinV = "win-v";
    public static bool IsKnown(string id) => id is CtrlV or WinV;
    public static ushort[] Keys(string id) => id == WinV ? [0x5b, 0x56] : [0x11, 0x56];
    public static string Label(string id) => id == WinV ? "Win + V" : "Ctrl + V";
    // UU can reach both Mac and Windows computers; the key is remembered per UU
    // device (its session window title). A device never set up uses a hint from
    // its name (a Mac needs Win+V), else the default key.
    public static string For(IReadOnlyDictionary<string, string>? devices, string? device, string fallback) =>
        device != null && devices != null && devices.TryGetValue(device, out var key) && IsKnown(key) ? key :
        Guess(device) ?? (IsKnown(fallback) ? fallback : CtrlV);
    public static bool Remembered(IReadOnlyDictionary<string, string>? devices, string? device) =>
        device != null && devices != null && devices.TryGetValue(device, out var key) && IsKnown(key);
    private static readonly System.Text.RegularExpressions.Regex MacName = new(@"(?<![a-z])i?mac(book|mini|pro|studio)?(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public static string? Guess(string? device) => device != null && MacName.IsMatch(device) ? WinV : null;
    public static string Other(string id) => id == WinV ? CtrlV : WinV;
}

public enum RemoteClientKind { UU, RemoteDesktop }

public static class RemoteClients
{
    // Remote Desktop (mstsc) is matched by its session window only; the
    // connection dialog of the same process is an ordinary dialog, not a target.
    public static RemoteClientKind? Classify(string executable, string windowClass, bool chosenUu) =>
        chosenUu || executable.Equals("UURemote.exe", StringComparison.OrdinalIgnoreCase) ? RemoteClientKind.UU :
        executable.Equals("mstsc.exe", StringComparison.OrdinalIgnoreCase) && windowClass == "TscShellContainerClass" ? RemoteClientKind.RemoteDesktop : null;
    // RDP always reaches Windows and forwards hardware scan codes, so it pastes
    // Ctrl+V with scan codes; UU keeps the user's key (Win+V for a Mac remote).
    public static (ushort[] Keys, bool ScanCodes) Paste(RemoteClientKind kind, string uuPasteKey) =>
        kind == RemoteClientKind.RemoteDesktop ? (PasteShortcut.Keys(PasteShortcut.CtrlV), true) : (PasteShortcut.Keys(uuPasteKey), false);
    public static string Label(RemoteClientKind kind) => kind == RemoteClientKind.RemoteDesktop ? "远程桌面" : "UU 远程";
    // RDP announces the clipboard at once and the remote fetches data on paste,
    // so it needs only a short wait; UU follows the user's sync speed.
    public static double SyncDelay(RemoteClientKind kind, string speed) =>
        kind == RemoteClientKind.RemoteDesktop ? .3 : SyncSpeed.Delay(speed);
}

public static class SyncSpeed
{
    public const string Fast = "fast", Standard = "standard", Compatible = "compatible";
    public static readonly string[] All = [Fast, Standard, Compatible];
    public static bool IsKnown(string? id) => id is Fast or Standard or Compatible;
    public static double Delay(string id) => id switch { Fast => .18, Compatible => 1.2, _ => .5 };
    public static string Label(string id) => id switch { Fast => "快速 · 180 毫秒", Compatible => "兼容 · 1.2 秒", _ => "标准 · 0.5 秒" };
    // 0.2 saved a compatible / fast boolean (default compatible). An unchanged
    // old setting keeps its wait; a new installation starts at standard.
    public static string Migrate(string? speed, bool? legacyCompatibility) =>
        IsKnown(speed) ? speed! : legacyCompatibility == false ? Fast : legacyCompatibility == true ? Compatible : Standard;
    // The first paste after a quiet period is the one that went missing: give the
    // client's clipboard channel a little more time.
    public const double IdleAfter = 120, IdleMinimum = .8;
    public static double ForJob(double delay, double? lastPasteAt, double now) =>
        lastPasteAt is double last && now - last <= IdleAfter ? delay : Math.Max(delay, IdleMinimum);
}

public static class SettleRule
{
    // Recording ended and nothing arrived: end the round instead of waiting.
    public const double NoSpeechAfter = 2.5;
    public static bool NoSpeech(string text, bool composing, bool sourceRecording, double stoppedFor) =>
        TextPolicy.IsEmpty(text) && !composing && !sourceRecording && stoppedFor >= NoSpeechAfter;
}

public enum CalibrationResult { Untested, Passed, Failed }

// "Try once" results are kept per client (UU per device) and count only for the
// paste key and sync speed they were made with.
public static class Calibration
{
    public const string RemoteDesktopProfile = "rdp";
    public static string Key(RemoteClientKind kind, string? device) => kind == RemoteClientKind.RemoteDesktop ? RemoteDesktopProfile : "uu:" + (device ?? "");
    public static string Profile(RemoteClientKind kind, string pasteKey, string speed) => kind == RemoteClientKind.RemoteDesktop ? RemoteDesktopProfile + "|" + RemoteDesktopProfile : pasteKey + "|" + speed;
    public static string Encode(bool passed, string profile) => (passed ? "pass|" : "fail|") + profile;
    public static CalibrationResult Read(IReadOnlyDictionary<string, string>? stored, string key, string profile)
    {
        if (stored == null || !stored.TryGetValue(key, out var raw)) return CalibrationResult.Untested;
        var bar = raw.IndexOf('|');
        if (bar < 0 || raw[(bar + 1)..] != profile) return CalibrationResult.Untested;
        return raw[..bar] == "pass" ? CalibrationResult.Passed : CalibrationResult.Failed;
    }
    public static string Label(CalibrationResult result) => result switch { CalibrationResult.Passed => "已通过", CalibrationResult.Failed => "上次未通过", _ => "未测试" };
}

// One page instead of a wizard: every row is recomputed, so finished rows are
// never asked again and nothing has to be resumed.
public sealed record SetupStatus(bool DoubaoDetected, bool DoubaoRunning, bool SharedKey, bool VoiceKeyConfirmed,
    IReadOnlyList<string> RunningClients, bool ClipboardConfirmed, string? Calibration)
{
    public const int RequiredCount = 3;
    // Only the shared key needs Doubao's own voice key changed.
    public bool VoiceKeyReady => !SharedKey || VoiceKeyConfirmed;
    public int RequiredDone => (DoubaoDetected ? 1 : 0) + (VoiceKeyReady ? 1 : 0) + (ClipboardConfirmed ? 1 : 0);
    public bool Ready => RequiredDone == RequiredCount;
}

public enum CaptureShortcutAction { Ignore, Begin, Advance, Resume }

public static class CaptureShortcut
{
    // Separate tool key: while listening, the finish key also reaches the source
    // (Doubao's tap mode ends a recording on any key).
    public static bool PassToSource(CaptureShortcutAction action, CapturePhase? phase) =>
        action == CaptureShortcutAction.Advance && phase == CapturePhase.Listening;
    public static CaptureShortcutAction Route(bool hasCapture, bool receiverForeground, bool originalRemoteForeground) =>
        hasCapture ? (receiverForeground ? CaptureShortcutAction.Advance :
            originalRemoteForeground ? CaptureShortcutAction.Resume : CaptureShortcutAction.Ignore) :
        originalRemoteForeground ? CaptureShortcutAction.Begin : CaptureShortcutAction.Ignore;
}

public enum SharedPress { StartSource, StopSource, Deliver }

// Tool key == Doubao's voice key. Decided at key-down from Doubao's observed state
// (its recording window), never guessed from text: Doubao may record silently.
public static class SharedKey
{
    public static SharedPress Decide(bool sourceRecording, bool hasText) =>
        sourceRecording ? SharedPress.StopSource : hasText ? SharedPress.Deliver : SharedPress.StartSource;
    // Start / stop must reach Doubao; "deliver" is kept so it does not restart Doubao.
    public static bool Passes(SharedPress press) => press != SharedPress.Deliver;
    // The opening press in a remote window is never passed to Doubao. Doubao answers
    // it only where it is that window's input method (often not in UU, never in
    // Remote Desktop); otherwise it shows its "not Doubao" prompt, ignores the next
    // presses and the Ctrl reaches the remote. The floatbar selects Doubao itself,
    // so the second press starts recording and the third ends it.
    public static bool OpeningPasses => false;
}

public sealed class ShortcutGate
{
    public bool Captured { get; private set; }
    private nint origin;
    public (bool Consume, bool Activate) Key(bool down, bool eligible, nint foreground)
    {
        if (down)
        {
            if (!Captured && eligible) { Captured = true; origin = foreground; }
            return (Captured, false);
        }
        var consumed = Captured;
        Captured = false;
        return (consumed, consumed && eligible && origin == foreground);
    }
}

public enum RecoveryStatus { Cancelled, Queued, Failed, Unconfirmed }
public sealed record RecoveryRecord(Guid Id, string Text, IRemoteTarget Target, double Created, RecoveryStatus Status, string Reason);
public sealed class RecoveryStore
{
    private readonly List<RecoveryRecord> records = [];
    public IReadOnlyList<RecoveryRecord> Records => records;
    public void Prune(double now) => records.RemoveAll(x => now - x.Created >= 300);
    public void Save(RecoveryRecord record, double now)
    {
        Prune(now);
        records.RemoveAll(x => x.Id == record.Id);
        records.Insert(0, record);
        if (records.Count > 5) records.RemoveRange(5, records.Count - 5);
    }
    public void Remove(Guid id) => records.RemoveAll(x => x.Id == id);
    public void Clear() => records.Clear();
}
