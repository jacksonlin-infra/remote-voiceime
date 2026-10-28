namespace RemoteVoiceInput.Core;

public interface IClock
{
    double Now { get; }
    void After(double seconds, Action action);
}
public interface IRemoteTarget
{
    string Label { get; }
    bool IsValid { get; }
    bool IsForeground { get; }
    // Requests the window to come forward and reports whether it is still the
    // target. Whether it actually came forward is checked by the delivery gate.
    bool Activate();
}
public enum ReleaseResult { Restored, Kept, Changed, Busy, Failed }
// Owned: unchanged since our write. Republished: a client or sync tool published
// the same text again (the lease adopts it). Changed: different content.
// Unreadable: busy or still rendering, which is not evidence of a user copy.
public enum LeaseState { Owned, Republished, Changed, Unreadable }
public interface IClipboardLease
{
    LeaseState Check();
    // Writes the payload once more after a client echoed the remote clipboard back.
    bool Rewrite();
    // restore=false leaves the transcript on the clipboard for a manual remote paste.
    ReleaseResult Release(bool restore);
}
public interface IClipboardBridge
{
    // Must capture all supported formats before replacing anything, under lock.
    IClipboardLease Acquire(string text);
}
public enum PasteResult { NotSent, Attempted }
public interface IPasteEmitter { PasteResult Send(IRemoteTarget target); }
// RestoreClipboard: put the previous clipboard back after the paste (3 s hold);
// otherwise the transcript stays and the hold is short. FocusTarget: an explicit
// request (confirm key, re-delivery) that may bring its window forward.
public sealed record DeliveryJob(Guid Id, string Text, IRemoteTarget Target, double SyncDelay, bool RestoreClipboard = false, bool FocusTarget = false);
public sealed record DeliveryOutcome(bool Attempted, string Message);

public sealed class DeliveryQueue(IClock clock, IClipboardBridge board, IPasteEmitter emitter)
{
    private enum Phase { Synchronizing, Holding, Stopped }
    private enum Block { Focus, Window, Modifiers, Clipboard }
    private sealed class Attempt(DeliveryJob job, IClipboardLease lease, double began)
    {
        public DeliveryJob Job { get; } = job;
        public IClipboardLease Lease { get; } = lease;
        public double Began { get; } = began;
        public double SyncFrom { get; set; } = began;
        public double? FrontSince { get; set; }
        public double? AttemptedAt { get; set; }
        public bool Reclaimed { get; set; }
        public bool Released { get; set; }
        public Phase Phase { get; set; }
    }
    // Activation and modifier release are asynchronous: the gate waits for them
    // instead of failing on one sample taken exactly at the sync deadline.
    public const double GateInterval = .04, GateTimeout = 1.5, FrontDwell = .1;
    // Focus stays with the client while it still processes the paste keys.
    public const double QuietAfterPaste = .15;
    public const double RestoreHold = 3, KeepHold = .8;
    public const int Backlog = 3;
    private readonly Queue<DeliveryJob> pending = new();
    private Attempt? active;
    private bool cancelling;
    public bool Suspended { get; set; }
    public bool IsInjecting => active is { } a && (a.Phase == Phase.Synchronizing || a.AttemptedAt is double at && clock.Now - at < QuietAfterPaste);
    public bool HasWork => active != null || pending.Count > 0;
    public int PendingCount => pending.Count;
    public Func<bool> CanDrain { get; set; } = () => true;
    public Func<DeliveryJob, bool> MayActivate { get; set; } = x => x.FocusTarget || x.Target.IsForeground;
    public Func<bool> ModifiersClear { get; set; } = () => true;
    // Whether the user typed or clicked after a time. Without that, a clipboard
    // change during sync is a client echo, not a user copy.
    public Func<double, bool> UserActedSince { get; set; } = _ => true;
    public Action<DeliveryJob, DeliveryOutcome> Outcome { get; set; } = (_, _) => { };
    public Action<string> Notice { get; set; } = _ => { };
    // Any change to IsInjecting / pending, e.g. to start a press that waited.
    public Action Changed { get; set; } = () => { };
    // Metadata only (stage, check results, timing); never text or titles.
    public Action<string> Trace { get; set; } = _ => { };
    private static string Tag(DeliveryJob job) => job.Id.ToString()[..8];
    public bool Contains(Guid id) => active?.Job.Id == id || pending.Any(x => x.Id == id);
    public bool IsActive(Guid id) => active?.Job.Id == id;
    // Queued or before its paste keys. A job only holding the clipboard afterwards
    // may be delivered again as a fresh job.
    public bool IsSending(Guid id) => pending.Any(x => x.Id == id) || active is { } a && a.Job.Id == id && a.Phase == Phase.Synchronizing;
    public bool Enqueue(DeliveryJob job)
    {
        if (Suspended || cancelling || pending.Count >= Backlog || Contains(job.Id)) return false;
        pending.Enqueue(job);
        Changed();
        return true;
    }
    public void Pump()
    {
        if (Suspended || cancelling || active != null || pending.Count == 0 || !CanDrain()) return;
        var job = pending.Dequeue();
        Trace($"{Tag(job)} dequeue valid={job.Target.IsValid} mayActivate={MayActivate(job)} foreground={job.Target.IsForeground} focusTarget={job.FocusTarget} restore={job.RestoreClipboard} delay_ms={(int)(job.SyncDelay * 1000)} chars={job.Text.Length}");
        if (!job.Target.IsValid || !MayActivate(job))
        {
            Trace($"{Tag(job)} failed before-clipboard");
            Outcome(job, new(false, "原窗口已改变或焦点离开，文字已保留"));
            Changed(); Pump(); return;
        }
        IClipboardLease lease;
        try { lease = board.Acquire(job.Text); }
        catch (InvalidOperationException error) { Trace($"{Tag(job)} clipboard-acquire-failed"); Outcome(job, new(false, error.Message)); Changed(); Pump(); return; }
        var attempt = new Attempt(job, lease, clock.Now); active = attempt;
        Trace($"{Tag(job)} clipboard-written state={lease.Check()}");
        // An already focused client is not activated again; the activation result
        // is not a failure condition: the gate waits for the window instead.
        if (!job.Target.IsForeground) Trace($"{Tag(job)} activate valid={job.Target.Activate()} foreground={job.Target.IsForeground}");
        Changed();
        Gate(attempt);
    }
    private void Gate(Attempt attempt)
    {
        if (active != attempt || attempt.Phase != Phase.Synchronizing) return;
        var now = clock.Now;
        var state = attempt.Lease.Check();
        if (state == LeaseState.Republished) Trace($"{Tag(attempt.Job)} clipboard-republished");
        else if (state == LeaseState.Changed)
        {
            if (attempt.Reclaimed || UserActedSince(attempt.Began)) { Stop(attempt, Block.Clipboard); return; }
            // Some clients copy the remote clipboard back when they regain focus.
            // Claim it once more and wait a full sync again.
            if (!attempt.Lease.Rewrite()) { Stop(attempt, Block.Clipboard); return; }
            attempt.Reclaimed = true; attempt.SyncFrom = now;
            Trace($"{Tag(attempt.Job)} clipboard-reclaimed");
        }
        var front = attempt.Job.Target.IsForeground;
        if (!front) attempt.FrontSince = null; else attempt.FrontSince ??= now;
        var synced = now - attempt.SyncFrom >= attempt.Job.SyncDelay - .0001;
        var settled = attempt.FrontSince is double since && now - since >= FrontDwell - .0001;
        Block? block;
        if (!synced || !settled) block = front ? null : Block.Focus;
        else if (state == LeaseState.Unreadable) block = Block.Clipboard;
        else if (!ModifiersClear()) block = Block.Modifiers;
        else if (!attempt.Job.Target.IsValid) block = Block.Window;
        else { Post(attempt); return; }
        var deadline = attempt.SyncFrom + attempt.Job.SyncDelay + GateTimeout;
        if (now >= deadline - .0001) { Stop(attempt, block ?? Block.Focus); return; }
        var remaining = attempt.SyncFrom + attempt.Job.SyncDelay - now;
        clock.After(synced ? GateInterval : Math.Min(GateInterval, remaining), () => Gate(attempt));
    }
    private void Stop(Attempt attempt, Block block)
    {
        var (code, message) = block switch
        {
            Block.Clipboard => ("clipboard", "投送前剪贴板被其他内容改写，已停止粘贴"),
            Block.Window => ("window", "原远程窗口已改变，已停止粘贴"),
            Block.Modifiers => ("modifiers", "检测到仍按住的修饰键，已停止粘贴"),
            _ => ("focus", "远控窗口未能回到前台，已停止粘贴")
        };
        Trace($"{Tag(attempt.Job)} blocked={code} waited_ms={(int)((clock.Now - attempt.Began) * 1000)} valid={attempt.Job.Target.IsValid} foreground={attempt.Job.Target.IsForeground} modifiersClear={ModifiersClear()}");
        Fail(attempt, message);
    }
    private void Post(Attempt attempt)
    {
        if (emitter.Send(attempt.Job.Target) == PasteResult.NotSent)
        { Fail(attempt, "粘贴按键未发出；请检查远控软件与工具的运行权限"); return; }
        attempt.Phase = Phase.Holding; attempt.AttemptedAt = clock.Now;
        Trace($"{Tag(attempt.Job)} paste-attempted after_ms={(int)((clock.Now - attempt.Began) * 1000)} reclaimed={attempt.Reclaimed}");
        Outcome(attempt.Job, new(true, "已发出粘贴，远端未确认"));
        Changed();
        clock.After(QuietAfterPaste, () => Changed());
        // Restoring waits for lazy remote fetches; otherwise a short hold only keeps
        // the next write from racing this paste.
        clock.After(attempt.Job.RestoreClipboard ? RestoreHold : KeepHold, () => Release(attempt));
    }
    private void Fail(Attempt attempt, string message)
    {
        if (active != attempt) return;
        attempt.Phase = Phase.Stopped; // No old gate callback may paste any more.
        Trace($"{Tag(attempt.Job)} failed");
        Outcome(attempt.Job, new(false, message));
        Release(attempt);
    }
    private void Release(Attempt attempt, int retry = 0)
    {
        if (active != attempt || attempt.Released) return;
        var result = attempt.Lease.Release(attempt.Job.RestoreClipboard);
        if (result == ReleaseResult.Busy && retry < 10)
        { clock.After(.1, () => Release(attempt, retry + 1)); return; }
        attempt.Released = true; active = null;
        Trace($"{Tag(attempt.Job)} clipboard-release={result}");
        if (result is ReleaseResult.Busy or ReleaseResult.Failed) Notice("原剪贴板恢复失败；听写正文仍可从最近文字找回");
        Changed();
        Pump();
    }
    public void CancelAll(string reason)
    {
        // Detach the backlog before callbacks can cause reentrant work.
        cancelling = true;
        var old = pending.ToArray(); pending.Clear();
        foreach (var job in old) Outcome(job, new(false, reason));
        if (active is { } attempt)
        {
            if (attempt.Phase == Phase.Synchronizing) { attempt.Phase = Phase.Stopped; Trace($"{Tag(attempt.Job)} cancelled"); Outcome(attempt.Job, new(false, reason)); }
            Release(attempt);
        }
        cancelling = false;
        Changed();
    }
}
