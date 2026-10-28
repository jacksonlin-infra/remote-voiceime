namespace RemoteVoiceInput.Windows;

// Explicit Windows-only check of the floatbar's input-method switch: from a plain
// English layout (as on a machine whose default is not Doubao), this process is
// switched to Doubao's profile. Affects only this short-lived process; no hook,
// speech, clipboard or remote window is involved.
internal static class ImeCheck
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var lines = new List<string>();
        var passed = true;
        void Check(bool result, string message) { passed &= result; lines.Add((result ? "PASS: " : "FAIL: ") + message); }
        using var window = new Form { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        window.Show(); Application.DoEvents();
        lines.Add($"Doubao profile registered: {DoubaoIme.Identity != null}; system default is Doubao: {DoubaoIme.IsSystemDefault}");
        lines.Add($"active at start: {DoubaoIme.ActiveName()}");
        Check(DoubaoIme.Identity != null, "Doubao TSF profile found in the registry");
        Check(DoubaoIme.ActivateEnglishLayout() && DoubaoIme.ActiveName() != "doubao", $"process switched away from Doubao ({DoubaoIme.ActiveName()})");
        Application.DoEvents();
        Check(DoubaoIme.Activate(), "floatbar switch makes Doubao the active input method");
        Application.DoEvents();
        Check(DoubaoIme.ActiveName() == "doubao", $"Doubao stays active for the process ({DoubaoIme.ActiveName()})");
        lines.Add(passed ? "Input method check passed." : "Input method check FAILED.");
        File.WriteAllLines(Path.Combine(directory, "ime-check.txt"), lines);
        return passed ? 0 : 1;
    }
}
