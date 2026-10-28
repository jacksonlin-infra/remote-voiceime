using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

// Explicit Windows-only check of the real clipboard lease used for delivery:
// back up, write a probe, stay owned through the UU sync wait, then restore.
// No keyboard hook, paste key, speech source or remote target is involved.
internal static class ClipboardCheck
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var lines = new List<string>();
        var passed = true;
        void Check(bool result, string message) { passed &= result; lines.Add((result ? "PASS: " : "FAIL: ") + message); }
        var original = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        // Several rounds: sync tools such as a Deskflow server re-publish at varying delays.
        for (var round = 1; round <= 3; round++)
        try
        {
            var probe = $"{ProductInfo.Name} clipboard check {DateTime.Now:HHmmss} #{round}";
            lines.Add($"Round {round}"); if (round > 1) Thread.Sleep(1000);
            var lease = new ClipboardBridge(0).Acquire(probe);
            Check(lease.Check() is LeaseState.Owned or LeaseState.Republished, "lease is owned right after writing (after CloseClipboard)");
            Check(Clipboard.ContainsText() && Clipboard.GetText() == probe, "clipboard holds the exact probe text");
            Thread.Sleep(1300);
            var state = LeaseState.Unreadable;
            for (var i = 0; i < 10 && state == LeaseState.Unreadable; i++) { state = lease.Check(); if (state == LeaseState.Unreadable) Thread.Sleep(20); }
            Check(state is LeaseState.Owned or LeaseState.Republished, $"lease stays owned through the 1.2 s compatibility sync wait ({state})");
            if (ClipboardBridge.SyncToolOwner() is { } tool) lines.Add($"NOTE: clipboard now owned by {tool} (clipboard sharing)");
            var released = ReleaseResult.Busy;
            for (var i = 0; i < 20 && released == ReleaseResult.Busy; i++) { released = lease.Release(restore: true); if (released == ReleaseResult.Busy) Thread.Sleep(100); }
            Check(released == ReleaseResult.Restored, "original clipboard restored (" + released + ")");
            if (original != null) Check(Clipboard.ContainsText() && Clipboard.GetText() == original, "restored text equals the previous clipboard text");
        }
        catch (InvalidOperationException error) { Check(false, "acquire failed: " + error.Message); }
        catch (System.Runtime.InteropServices.ExternalException error) { Check(false, "clipboard access failed: " + error.Message); }
        lines.Add(passed ? "Clipboard lease check passed. Text content is not logged." : "Clipboard lease check FAILED.");
        File.WriteAllLines(Path.Combine(directory, "clipboard-check.txt"), lines);
        return passed ? 0 : 1;
    }
}
