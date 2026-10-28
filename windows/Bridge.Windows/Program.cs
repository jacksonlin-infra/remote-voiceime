namespace RemoteVoiceInput.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--preview-ui", var directory, ..] && args.Length <= 3)
        {
            ApplicationConfiguration.Initialize();
            var theme = args.Length == 3 ? args[2] : "system";
            if (theme is "light" or "dark") UiStyle.ForcedDark = theme == "dark";
            Application.SetColorMode(theme switch { "dark" => SystemColorMode.Dark, "light" => SystemColorMode.Classic, _ => SystemColorMode.System });
            UiPreview.Run(directory); return;
        }
        if (args is ["--verify-ime", var imeOutput])
        {
            ApplicationConfiguration.Initialize(); Environment.ExitCode = ImeCheck.Run(imeOutput); return;
        }
        if (args is ["--verify-clipboard", var output])
        {
            ApplicationConfiguration.Initialize(); Environment.ExitCode = ClipboardCheck.Run(output); return;
        }
        using var singleton = new Mutex(true, "Local\\RemoteVoiceInput.Windows", out var created);
        if (!created) { MessageBox.Show($"{ProductInfo.Name} 的另一个实例已在托盘运行。\n本次打开的是 {ProductInfo.VersionLabel}。\n解压覆盖不会更新已运行的程序，请先从托盘退出旧实例，再打开本版本。", ProductInfo.Name); return; }
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        try { using var context = new BridgeApp(); Application.Run(context); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(ex.Message, $"{ProductInfo.Name} 未能启动", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
