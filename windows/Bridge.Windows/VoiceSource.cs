namespace RemoteVoiceInput.Windows;

internal interface IVoiceSourceAdapter
{
    string Name { get; }
    bool IsManual { get; }
    bool Start(Func<bool> ownsInput);
    bool Stop(Func<bool> ownsInput);
}

// Doubao shows its recording bar as a top-level window of class OimeVoiceWaveWindow
// (owned by its ImeService), visible exactly while it records. Observed on
// Doubao v0.9.2.7; reading visibility needs no rights over the elevated service.
internal static class DoubaoProbe
{
    // Checklist only. Doubao installs to Program Files\DoubaoIME and runs
    // ImeService / ImeWatchdog; process names are readable without rights over them.
    internal static bool Running => ProcessRunning("ImeWatchdog") || ProcessRunning("ImeService");
    internal static bool Installed => Running || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DoubaoIME"));
    internal static bool ProcessRunning(string name)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
    // Doubao's other top-level UI (status bar, "not Doubao" prompt) as pixel sizes, for diagnostics.
    internal static string Prompts()
    {
        var sizes = new List<string>();
        nint window = 0;
        while ((window = Native.FindWindowEx(0, window, "OimeDirectUIWindow", null)) != 0)
            if (Native.IsWindowVisible(window) && Native.GetWindowRect(window, out var r)) sizes.Add($"{r.Right - r.Left}x{r.Bottom - r.Top}");
        return sizes.Count == 0 ? "none" : string.Join(",", sizes);
    }
    internal static bool Recording()
    {
        nint window = 0;
        while ((window = Native.FindWindowEx(0, window, "OimeVoiceWaveWindow", null)) != 0)
            if (Native.IsWindowVisible(window)) return true;
        return false;
    }
}

internal sealed class DoubaoShortcutSource(AppSettings settings) : IVoiceSourceAdapter
{
    internal sealed record Shortcut(string Id, string Label, ushort[] Keys);
    internal static readonly Shortcut[] Shortcuts = [
        new("manual", "手动调用豆包（首次建议）", []),
        new("right-control", "右 Ctrl", [0xa3]),
        new("right-alt", "右 Alt", [0xa5]),
        new("ctrl-shift-space", "Ctrl + Shift + 空格", [0x11, 0x10, 0x20]),
        new("alt-space", "Alt + 空格", [0x12, 0x20]),
        new("f9", "F9", [0x78]), new("f10", "F10", [0x79])
    ];
    public string Name => "豆包语音";
    public bool IsManual => settings.VoiceShortcut == "manual";
    private ushort[] Keys => Shortcuts.First(x => x.Id == settings.VoiceShortcut).Keys;
    public bool Start(Func<bool> ownsInput) => IsManual || (ownsInput() && KeyboardInjector.Press(Keys, .12, After));
    public bool Stop(Func<bool> ownsInput) => IsManual || (ownsInput() && KeyboardInjector.Press(settings.StopWithEnter ? [0x0d] : Keys, .12, After));
    private static void After(double seconds, Action action)
    {
        if (seconds <= 0) { action(); return; }
        var timer = new System.Windows.Forms.Timer { Interval = (int)(seconds * 1000) };
        timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); action(); };
        timer.Start();
    }
}
