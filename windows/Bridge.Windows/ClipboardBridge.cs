using System.Runtime.InteropServices;
using System.Text;
using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

internal enum ClipboardFormatPolicy { Copy, Skip, Reject }

internal sealed class ClipboardBridge(nint owner) : IClipboardBridge
{
    private const int Limit = 16_000_000;
    private sealed record Format(uint Id, byte[] Bytes);
    // OLE publishes these as private marshalling state for its IDataObject; the
    // bytes are not restorable and OLE rebuilds them from the remaining formats.
    private static readonly string[] OleInternal = ["DataObject", "Ole Private Data", "OleClipboardPersistOnFlush"];
    internal static ClipboardFormatPolicy Classify(uint format, string? name, bool hasData, bool dibAvailable = false)
    {
        // Windows synthesizes CF_BITMAP / CF_PALETTE from CF_DIB(V5), which are
        // plain HGLOBAL data; restoring the DIB brings the bitmap back.
        if (dibAvailable && format is 2 or 9) return ClipboardFormatPolicy.Skip;
        // GDI/owner handles are not HGLOBAL memory. Never silently downgrade
        // an image/metafile clipboard to plain text.
        if (format is 2 or 3 or 9 or 14 or 0x80 or 0x82 or 0x83 or 0x8e || format is >= 0x300 and <= 0x3ff)
            return ClipboardFormatPolicy.Reject;
        if (name != null && OleInternal.Contains(name, StringComparer.Ordinal)) return ClipboardFormatPolicy.Skip;
        // Advertised-only markers (Windows 11 EnterpriseDataProtectionId etc.)
        // carry no data, so nothing is lost by not restoring them.
        return hasData ? ClipboardFormatPolicy.Copy : ClipboardFormatPolicy.Skip;
    }
    private static readonly string[] SyncTools = ["deskflow", "synergy", "barrier", "input-leap", "inputleap"];
    // Names a keyboard/clipboard sharing tool that now owns the clipboard, so a
    // failed delivery can say why instead of a generic "clipboard changed".
    internal static string? SyncToolOwner()
    {
        var window = Native.GetClipboardOwner();
        if (window == 0) return null;
        Native.GetWindowThreadProcessId(window, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return SyncTools.Any(x => process.ProcessName.StartsWith(x, StringComparison.OrdinalIgnoreCase)) ? process.ProcessName : null;
        }
        catch (ArgumentException) { return null; }
    }
    public IClipboardLease Acquire(string text)
    {
        List<Format> snapshot;
        if (!Native.OpenClipboard(owner)) throw new InvalidOperationException("剪贴板正被占用；文字已保留，请稍后重试");
        try
        {
            snapshot = Capture();
            if (!Write(Payload(text)))
            {
                if (!Write(snapshot)) throw new InvalidOperationException("剪贴板写入及回滚失败；听写文字仍在浮条中");
                throw new InvalidOperationException("无法写入剪贴板，已尝试恢复原内容");
            }
        }
        finally { Native.CloseClipboard(); }
        // CloseClipboard itself advances the sequence (synthesized text/locale
        // formats), so ownership is recorded only after closing, and only if
        // nobody replaced our text in between.
        var generation = Native.GetClipboardSequenceNumber();
        return new Lease(owner, snapshot, ReadText(owner) is { } current && !SameText(current, text) ? 0 : generation, text);
    }
    // The transcript is marked so it stays out of Windows clipboard history and
    // cloud clipboard, also when it is left on the clipboard after delivery.
    private static List<Format> Payload(string text)
    {
        var history = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
        var cloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");
        if (history == 0 || cloud == 0) throw new InvalidOperationException("无法创建剪贴板隐私标记，未改动原内容");
        return [new(13, Encoding.Unicode.GetBytes(text + '\0')), new(history, new byte[4]), new(cloud, new byte[4])];
    }
    // Clipboard sync tools (Deskflow server, UU) re-publish the text they just
    // read, advancing the sequence without a new user copy. Only different
    // content counts as the user's copy; line endings may be normalized.
    internal static bool SameText(string? current, string payload) =>
        current != null && current.TrimEnd('\0').Replace("\r\n", "\n") == payload.TrimEnd('\0').Replace("\r\n", "\n");
    private static string? ReadText(nint owner)
    {
        if (!Native.OpenClipboard(owner)) return null; // Busy readers do not change the sequence.
        try { return OpenText(); }
        finally { Native.CloseClipboard(); }
    }
    // Null when the text cannot be read yet: a sync tool's delayed rendering
    // may not have produced data, which is not evidence of a different copy.
    private static string? OpenText()
    {
        var handle = Native.GetClipboardData(13);
        var pointer = handle == 0 ? 0 : Native.GlobalLock(handle);
        if (pointer == 0) return null;
        try { return Marshal.PtrToStringUni(pointer, (int)(Native.GlobalSize(handle) / 2)).TrimEnd('\0'); }
        finally { Native.GlobalUnlock(handle); }
    }

    private static List<Format> Capture()
    {
        var result = new List<Format>();
        var total = 0;
        uint format = 0;
        var dib = Native.IsClipboardFormatAvailable(8) || Native.IsClipboardFormatAvailable(17);
        while ((format = Native.EnumClipboardFormats(format)) != 0)
        {
            if (result.Count >= 128) throw new InvalidOperationException("剪贴板格式过多，原内容未改动；请先复制一小段纯文本后重试");
            if (Classify(format, FormatName(format), true, dib) == ClipboardFormatPolicy.Reject)
                throw new InvalidOperationException("当前剪贴板含图像或特殊句柄，未覆盖；请先复制一小段纯文本后重试");
            var handle = Native.GetClipboardData(format);
            if (Classify(format, FormatName(format), handle != 0, dib) == ClipboardFormatPolicy.Skip) continue;
            var size = Native.GlobalSize(handle);
            if (size == 0 || size > (nuint)(Limit - total))
                throw new InvalidOperationException("无法完整备份剪贴板或超过 16 MB，原内容未改动");
            var pointer = Native.GlobalLock(handle);
            if (pointer == 0) throw new InvalidOperationException("无法读取原剪贴板，已停止投送");
            try
            {
                var data = new byte[(int)size]; Marshal.Copy(pointer, data, 0, data.Length);
                result.Add(new(format, data)); total += data.Length;
            }
            finally { Native.GlobalUnlock(handle); }
        }
        if (Marshal.GetLastPInvokeError() != 0) throw new InvalidOperationException("无法枚举完整剪贴板，原内容未改动");
        return result;
    }

    private static string? FormatName(uint format)
    {
        if (format < 0xc000) return null; // Standard formats have no registered name.
        var name = new StringBuilder(256);
        return Native.GetClipboardFormatName(format, name, name.Capacity) > 0 ? name.ToString() : null;
    }

    private static bool Write(List<Format> formats)
    {
        var allocated = new List<(uint Id, nint Handle)>();
        try
        {
            // Allocate and copy everything before EmptyClipboard can change data.
            foreach (var format in formats)
            {
                var handle = Native.GlobalAlloc(2, (nuint)format.Bytes.Length);
                if (handle == 0) return false;
                allocated.Add((format.Id, handle));
                var pointer = Native.GlobalLock(handle);
                if (pointer == 0) return false;
                try { Marshal.Copy(format.Bytes, 0, pointer, format.Bytes.Length); }
                finally { Native.GlobalUnlock(handle); }
            }
            if (!Native.EmptyClipboard()) return false;
            for (var i = 0; i < allocated.Count; i++)
            {
                var item = allocated[i];
                if (Native.SetClipboardData(item.Id, item.Handle) == 0) return false;
                allocated[i] = (item.Id, 0); // System owns successfully published handles.
            }
            return true;
        }
        finally { foreach (var item in allocated) if (item.Handle != 0) Native.GlobalFree(item.Handle); }
    }

    private sealed class Lease(nint owner, List<Format> snapshot, uint generation, string text) : IClipboardLease
    {
        private bool released;
        // Never blocks: a sync tool briefly holding the clipboard open or still
        // rendering is reported as unreadable and the delivery gate polls again.
        public LeaseState Check()
        {
            if (released) return LeaseState.Changed;
            if (Native.GetClipboardSequenceNumber() == generation) return LeaseState.Owned;
            var sequence = Native.GetClipboardSequenceNumber();
            if (ReadText(owner) is not { } current) return LeaseState.Unreadable;
            if (!SameText(current, text)) return LeaseState.Changed;
            generation = sequence; return LeaseState.Republished;
        }
        public bool Rewrite()
        {
            if (released || !Native.OpenClipboard(owner)) return false;
            try { if (!Write(Payload(text))) return false; }
            catch (InvalidOperationException) { return false; }
            finally { Native.CloseClipboard(); }
            generation = Native.GetClipboardSequenceNumber();
            return ReadText(owner) is not { } current || SameText(current, text);
        }
        public ReleaseResult Release(bool restore)
        {
            if (released) return ReleaseResult.Changed;
            if (!restore) { released = true; return ReleaseResult.Kept; }
            if (!Native.OpenClipboard(owner)) return ReleaseResult.Busy;
            try
            {
                if (Native.GetClipboardSequenceNumber() != generation)
                {
                    var current = OpenText();
                    if (current == null) return ReleaseResult.Busy; // Retry once rendering settles.
                    if (!SameText(current, text)) { released = true; return ReleaseResult.Changed; }
                }
                released = true;
                return Write(snapshot) ? ReleaseResult.Restored : ReleaseResult.Failed;
            }
            finally { Native.CloseClipboard(); }
        }
    }
}
