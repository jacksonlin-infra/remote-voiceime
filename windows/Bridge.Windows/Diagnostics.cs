namespace RemoteVoiceInput.Windows;

// Metadata only: stages, check results and timings. Callers never pass
// dictated text, clipboard contents or window titles.
internal static class Diagnostics
{
    private const int Limit = 300;
    private static readonly List<string> lines = [];
    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteVoiceInput", "diagnostics.log");
    internal static void Start() => Write($"--- {ProductInfo.Name} {ProductInfo.VersionLabel} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}; metadata only, paste-attempted is not a remote receipt");
    // "Copy diagnostics": the in-memory metadata lines plus a settings summary
    // without device names (UU device names are window titles).
    internal static string Report(string summary) =>
        $"{ProductInfo.Name} {ProductInfo.VersionLabel} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · metadata only\r\n{summary}\r\n" + string.Join("\r\n", lines);
    internal static void Write(string message)
    {
        lines.Add($"{DateTime.Now:HH:mm:ss.fff} {message}");
        if (lines.Count > Limit) lines.RemoveRange(0, lines.Count - Limit);
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllLines(FilePath, lines); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
