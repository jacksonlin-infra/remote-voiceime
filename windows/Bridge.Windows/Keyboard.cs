using System.Runtime.InteropServices;
using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

internal static class KeyboardInjector
{
    // withScan fills the hardware scan code next to the virtual key: Remote Desktop
    // forwards scan codes, and a VK-only event would arrive at the remote as nothing.
    private static Native.Input Event(ushort key, bool up, bool withScan = false)
    {
        var scan = withScan ? Native.MapVirtualKey(key, 4) : 0; // MAPVK_VK_TO_VSC_EX
        return new()
        {
            Type = 1,
            Data = new() { Key = new() { Vk = key, Scan = (ushort)(scan & 0xff), Extra = Native.Marker,
                Flags = (up ? Native.KeyUp : 0) | (key is 0xa3 or 0xa5 or 0x5b or 0x5c || (scan >> 8) is 0xe0 or 0xe1 ? Native.Extended : 0) } }
        };
    }
    internal static Native.Input[] Events(ushort[] keys, bool withScan = false) =>
        keys.Select(k => Event(k, false, withScan)).Concat(keys.Reverse().Select(k => Event(k, true, withScan))).ToArray();
    internal static Native.Input[] ReleasesAfterPartial(Native.Input[] events, uint sent)
    {
        var held = new List<ushort>();
        for (var i = 0; i < Math.Min(sent, events.Length); i++)
        {
            var e = events[i].Data.Key;
            if ((e.Flags & Native.KeyUp) == 0) held.Add(e.Vk); else held.Remove(e.Vk);
        }
        var withScan = events.Length > 0 && events[0].Data.Key.Scan != 0;
        return held.AsEnumerable().Reverse().Select(k => Event(k, true, withScan)).ToArray();
    }
    // Voice-source keys imitate a physical press: hardware scan code (E0 38 for
    // right Alt) and a separate, held release. Paste keys keep the plain batch.
    internal static Native.Input SourceEvent(ushort key, bool up)
    {
        var scan = Native.MapVirtualKey(key, 4); // MAPVK_VK_TO_VSC_EX keeps the E0 prefix.
        return new()
        {
            Type = 1,
            Data = new() { Key = new() { Vk = key, Scan = (ushort)(scan & 0xff), Extra = Native.Marker,
                Flags = Native.ScanCode | (up ? Native.KeyUp : 0) | ((scan >> 8) is 0xe0 or 0xe1 || key is 0xa3 or 0xa5 or 0x5b or 0x5c ? Native.Extended : 0) } }
        };
    }
    internal static bool Press(ushort[] keys, double hold, Action<double, Action> after)
    {
        if (keys.Length == 0 || !Native.ModifiersClear) return false;
        var downs = keys.Select(k => SourceEvent(k, false)).ToArray();
        var sent = Native.SendInput((uint)downs.Length, downs, Marshal.SizeOf<Native.Input>());
        // Every key that went down is always released, even if focus changes meanwhile.
        var ups = keys.Take((int)sent).Reverse().Select(k => SourceEvent(k, true)).ToArray();
        if (ups.Length > 0) after(sent == downs.Length ? hold : 0, () => Native.SendInput((uint)ups.Length, ups, Marshal.SizeOf<Native.Input>()));
        return sent == downs.Length;
    }
    internal static (bool Complete, bool MainKeyDown) Chord(ushort[] keys, bool withScan = false)
    {
        if (keys.Length == 0 || !Native.ModifiersClear) return (false, false);
        var events = Events(keys, withScan);
        var sent = Native.SendInput((uint)events.Length, events, Marshal.SizeOf<Native.Input>());
        if (sent < events.Length && sent > 0)
        {
            // Release only synthetic keys still held after a partially inserted sequence.
            var cleanup = ReleasesAfterPartial(events, sent);
            if (cleanup.Length > 0) Native.SendInput((uint)cleanup.Length, cleanup, Marshal.SizeOf<Native.Input>());
        }
        return (sent == events.Length, sent >= keys.Length);
    }
}

internal sealed class PasteEmitter(Func<IRemoteTarget, (ushort[] Keys, bool ScanCodes)> profile) : IPasteEmitter
{
    public PasteResult Send(IRemoteTarget target)
    {
        if (!target.IsValid || !target.IsForeground || !Native.ModifiersClear) return PasteResult.NotSent;
        var (keys, scanCodes) = profile(target);
        // One SendInput batch keeps the key-down/up pairs contiguous.
        return KeyboardInjector.Chord(keys, scanCodes).MainKeyDown ? PasteResult.Attempted : PasteResult.NotSent;
    }
}

internal sealed class KeyboardHook : IDisposable
{
    private readonly Native.HookProc callback;
    private readonly ShortcutGate gate = new();
    private nint hook;
    private bool chord;
    private uint capturedKey;
    private bool passing;
    internal Action<nint>? Ignored { get; set; }
    // Any physical key press: a clipboard change after one may be the user's copy.
    internal Action? Activity { get; set; }
    internal KeyboardHook(Func<uint> shortcut, Func<nint, bool> eligible, Func<nint, bool> passToSource, Action<nint> trigger)
    {
        callback = (code, message, pointer) =>
        {
            if (code >= 0)
            {
                var key = Marshal.PtrToStructure<Native.HookKey>(pointer);
                if ((key.Flags & 0x10) == 0) // Physical events only; no injection recursion.
                {
                    var down = message == 0x100 || message == 0x104;
                    var up = message == 0x101 || message == 0x105;
                    if (down) Activity?.Invoke();
                    var vk = gate.Captured ? capturedKey : shortcut();
                    if (key.Vk == vk && (down || up)) // F8 or right Alt; AltGr adds Ctrl and is left alone.
                    {
                        var foreground = Native.GetForegroundWindow();
                        if (down && !gate.Captured) { chord = false; capturedKey = vk; passing = passToSource(foreground); }
                        var allowed = eligible(foreground);
                        if (down && !gate.Captured && !allowed) Ignored?.Invoke(foreground);
                        var result = gate.Key(down, allowed && Native.ModifiersClearExcept(vk) && !chord, foreground);
                        if (result.Activate) trigger(foreground);
                        // A passed gesture still triggers on release but also reaches the source.
                        if (result.Consume && !passing) return 1;
                    }
                    else if (down && gate.Captured) chord = true;
                }
            }
            return Native.CallNextHookEx(0, code, message, pointer);
        };
        hook = Native.SetWindowsHookEx(Native.HookKeyboard, callback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new InvalidOperationException("无法注册工具快捷键的键盘钩子，请重新打开工具并检查运行权限。");
    }
    // Low-level hooks run newest first. Remote Desktop installs its own hook when
    // it takes the keyboard (full screen) and swallows keys, so re-register to be first.
    internal bool Refresh()
    {
        var fresh = Native.SetWindowsHookEx(Native.HookKeyboard, callback, Native.GetModuleHandle(null), 0);
        if (fresh == 0) return false;
        var old = hook; hook = fresh;
        if (old != 0) Native.UnhookWindowsHookEx(old);
        return true;
    }
    public void Dispose() { if (hook != 0) Native.UnhookWindowsHookEx(hook); GC.KeepAlive(callback); }
}

// Physical mouse button presses only, for the same "did the user act" question:
// a copy from a context menu must never be mistaken for a client echo.
internal sealed class MouseActivityHook : IDisposable
{
    private readonly Native.HookProc callback;
    private readonly nint hook;
    internal MouseActivityHook(Action activity)
    {
        callback = (code, message, pointer) =>
        {
            // LLMHF_INJECTED is bit 0 of the MSLLHOOKSTRUCT flags (offset 12).
            if (code >= 0 && message is 0x201 or 0x204 or 0x207 or 0x20b && (Marshal.ReadInt32(pointer, 12) & 1) == 0) activity();
            return Native.CallNextHookEx(0, code, message, pointer);
        };
        hook = Native.SetWindowsHookEx(Native.HookMouse, callback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new InvalidOperationException("无法注册鼠标活动监听");
    }
    public void Dispose() { if (hook != 0) Native.UnhookWindowsHookEx(hook); GC.KeepAlive(callback); }
}
