using System.Runtime.InteropServices;
using System.Text;

namespace RemoteVoiceInput.Windows;

internal static class Native
{
    internal const int HookKeyboard = 13;
    internal const uint KeyUp = 2, Extended = 1, ScanCode = 8;
    internal static readonly nuint Marker = 0x52564942;
    internal delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct HookKey
    { public uint Vk, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    { [FieldOffset(0)] public KeyboardInput Key; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input
    { public uint Type; public InputUnion Data; }

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int size);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] internal static extern nint GetFocus();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] internal static extern nuint GetMessageExtraInfo();
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern nint SendMessage(nint window, uint message, nint w, nint l);
    [DllImport("imm32.dll")] internal static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] internal static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] internal static extern int ImmGetCompositionString(nint context, uint index, nint buffer, uint bytes);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode, EntryPoint = "ImmGetCompositionStringW")] internal static extern int ImmGetCompositionText(nint context, uint index, [Out] byte[] buffer, uint bytes);
    [StructLayout(LayoutKind.Sequential)] internal struct Point32 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct CompositionForm { public uint Style; public Point32 Position; public Rect32 Area; }
    [DllImport("imm32.dll")] internal static extern bool ImmSetCompositionWindow(nint context, ref CompositionForm form);
    [DllImport("user32.dll")] internal static extern bool GetCaretPos(out Point32 point);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] internal static extern nint SendRect(nint window, uint message, nint w, out Rect32 rect);

    [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClipboardFormatName(uint format, StringBuilder name, int size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nuint GlobalSize(nint memory);

    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowScrollBar(nint window, int bar, bool show);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    internal static long GetWindowLong(nint window, int index) => GetWindowLongPtr(window, index);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect32 rect);
    internal const int HookMouse = 14;
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint attach, uint to, bool value);
    [DllImport("user32.dll")] internal static extern bool BringWindowToTop(nint window);
    // The tool is a background tray process when its hook consumes F8 in UU, so a
    // plain SetForegroundWindow is refused by the foreground lock. Joining the
    // foreground thread's input state for this one call is the documented way
    // for a cooperating process to hand over activation.
    internal static bool BringToForeground(nint window)
    {
        if (SetForegroundWindow(window) && GetForegroundWindow() == window) return true;
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var current = GetCurrentThreadId();
        var attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
        return GetForegroundWindow() == window;
    }
    internal static string ProcessOf(nint window)
    {
        if (window == 0) return "none";
        GetWindowThreadProcessId(window, out var pid);
        if (pid == Environment.ProcessId) return "self";
        try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); return process.ProcessName; }
        catch (ArgumentException) { return "pid " + pid; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder name, int size);
    internal static string ClassName(nint window) { var name = new StringBuilder(256); return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : ""; }
    internal static string Title(nint window)
    { var text = new StringBuilder(1024); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    internal static bool ModifiersClear => ModifiersClearExcept(0);
    // A modifier used as the tool key itself must not block its own gesture.
    internal static bool ModifiersClearExcept(uint trigger) =>
        (trigger switch { 0xa5 => new[] { 0x10, 0x11, 0xa4, 0x5b, 0x5c }, 0xa3 => new[] { 0x10, 0xa2, 0x12, 0x5b, 0x5c }, _ => new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c } }).All(k => (GetAsyncKeyState(k) & 0x8000) == 0);
    internal static bool IsOwnForeground
    {
        get { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return pid == Environment.ProcessId; }
    }
}
