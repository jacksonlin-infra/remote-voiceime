using System.Diagnostics;
using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

internal interface IRemoteClientAdapter
{
    string Name { get; }
    WindowTarget? Capture(nint window);
    IReadOnlyList<string> Running();
}

internal sealed class RemoteClientAdapter(AppSettings settings) : IRemoteClientAdapter
{
    public string Name => "UU 远程 / 远程桌面";
    // Checklist only: which supported clients are running now (labels, no titles).
    public IReadOnlyList<string> Running()
    {
        var result = new List<string>();
        var names = new List<string> { "UURemote" };
        if (!string.IsNullOrEmpty(settings.RemoteExecutable)) names.Add(Path.GetFileNameWithoutExtension(settings.RemoteExecutable));
        if (names.Any(DoubaoProbe.ProcessRunning)) result.Add(RemoteClients.Label(RemoteClientKind.UU));
        if (Native.FindWindowEx(0, 0, "TscShellContainerClass", null) != 0) result.Add(RemoteClients.Label(RemoteClientKind.RemoteDesktop));
        return result;
    }
    public WindowTarget? Capture(nint window)
    {
        if (!Native.IsWindow(window) || !Native.IsWindowVisible(window)) return null;
        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == Environment.ProcessId || pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            if (path == null) return null;
            // Known executables, with an exact user-selected UU path fallback.
            var chosen = !string.IsNullOrEmpty(settings.RemoteExecutable) &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(settings.RemoteExecutable), StringComparison.OrdinalIgnoreCase);
            if (RemoteClients.Classify(Path.GetFileName(path), Native.ClassName(window), chosen) is not { } kind) return null;
            return new WindowTarget(window, pid, process.StartTime.ToUniversalTime(), Native.Title(window), RemoteClients.Label(kind), kind);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        { return null; }
    }
}

internal sealed class WindowTarget(nint window, uint process, DateTime started, string title, string client, RemoteClientKind kind) : IRemoteTarget
{
    internal nint Handle => window;
    internal RemoteClientKind Kind => kind;
    // UU titles its session window with the remote device name.
    internal string Device => title;
    public string Label => client + (title.Length == 0 ? "" : " · " + title);
    public bool IsValid
    {
        get
        {
            if (!Native.IsWindow(window) || !Native.IsWindowVisible(window)) return false;
            Native.GetWindowThreadProcessId(window, out var pid);
            if (pid != process || Native.Title(window) != title) return false;
            try { using var p = Process.GetProcessById((int)pid); return !p.HasExited && p.StartTime.ToUniversalTime() == started; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }
    public bool IsForeground => Native.GetForegroundWindow() == window;
    // Returns whether the window is still the target; the delivery gate waits for
    // it to actually come forward. A tray process (or the passive notice) is
    // refused by the foreground lock, so this joins the foreground thread's input
    // for the one call. A client already in front is left alone.
    public bool Activate()
    {
        if (!IsValid) return false;
        if (IsForeground) return true;
        if (Native.IsIconic(window)) Native.ShowWindow(window, 9);
        Native.BringToForeground(window);
        return true;
    }
}
