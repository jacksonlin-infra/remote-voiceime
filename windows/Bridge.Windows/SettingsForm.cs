using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

// Three pages, applied as soon as a value changes (like the macOS settings):
// 通用 / 远程客户端 / 运行环境与诊断. Shows state and calls back; never touches
// the clipboard, keys or remote windows itself.
internal sealed class SettingsForm : Form
{
    internal Action? Changed;
    internal Action? TestDelivery;
    internal Action? OpenSetup;
    internal Action? CopyDiagnostics;
    internal Action? ClearRecent;
    internal Action<bool>? PauseChanged;
    internal Func<bool> Paused = () => false;
    internal static readonly string[] Sections = ["通用", "远程客户端", "运行环境与诊断"];
    private readonly AppSettings settings;
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new Padding(27, 22, 27, 16), AutoScroll = true };
    private readonly Label status = UiStyle.Label("设置保存到本机当前用户，修改后立即生效。", secondary: true);
    private readonly List<Button> navigation = [];
    private int section;
    private string? device;
    private RemoteClientKind kind = RemoteClientKind.UU;
    private string Key => ToolShortcut.Label(settings.ToolShortcut);

    internal SettingsForm(AppSettings settings)
    {
        this.settings = settings;
        Text = $"{ProductInfo.Name} · 设置"; Font = UiStyle.Body;
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(800, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 190, Padding = new Padding(10, 20, 10, 12), Tag = "soft" };
        var nav = UiStyle.Column(); nav.Tag = "soft";
        var brand = UiStyle.Label(ProductInfo.Name); brand.Font = UiStyle.Strong; brand.Margin = new Padding(6, 8, 0, 24); nav.Controls.Add(brand);
        foreach (var (title, index) in Sections.Select((x, i) => (x, i)))
        {
            var item = UiStyle.Button(title, () => SelectSection(index)); item.Dock = DockStyle.Top; item.Width = 170; item.Height = 36; item.MinimumSize = new Size(170, 36);
            item.TextAlign = ContentAlignment.MiddleLeft; item.Margin = new Padding(0, 0, 0, 6);
            navigation.Add(item); nav.Controls.Add(item);
        }
        status.Dock = DockStyle.Bottom; status.Font = UiStyle.Small; status.MaximumSize = new Size(170, 0); status.Margin = Padding.Empty; status.Padding = new Padding(6, 0, 0, 0);
        sidebar.Controls.Add(nav); sidebar.Controls.Add(status);
        Controls.Add(content); Controls.Add(sidebar);
        FormClosing += (_, e) => { e.Cancel = true; Hide(); };
        UiStyle.Watch(this); UiStyle.FitToScreen(this); SelectSection(0);
    }
    internal void ShowSection(int index) { if (index != section || Visible) SelectSection(index); }
    internal void SelectSection(int index)
    {
        section = index; UiStyle.Clear(content);
        foreach (var (button, i) in navigation.Select((button, i) => (button, i))) button.Tag = i == section ? "selected" : "navigation";
        var column = UiStyle.Column(); column.ColumnStyles.Add(new(SizeType.Percent, 100)); content.Controls.Add(column);
        void Add(Control control) { control.Dock = DockStyle.Top; column.Controls.Add(control); }
        if (section == 0) General(Add);
        else if (section == 1) Remote(Add);
        else DiagnosticsPage(Add);
        UiStyle.Apply(this);
    }
    private static ComboBox Choice(string name, string[] items, int selected)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, AccessibleName = name };
        box.Items.AddRange(items); box.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1); return box;
    }
    private void General(Action<Control> add)
    {
        add(UiStyle.Label("通用", heading: true)); add(UiStyle.Label("熟悉一个语音键，就能完成日常输入。", secondary: true));
        var shared = settings.ToolShortcut == ToolShortcut.SharedRightCtrl;
        var tool = Choice("语音键", ["F8（默认）", "右 Alt（与 Mac 同键）", "右 Ctrl 与豆包同键（推荐）"],
            settings.ToolShortcut switch { ToolShortcut.RightAlt => 1, ToolShortcut.SharedRightCtrl => 2, _ => 0 });
        tool.SelectedIndexChanged += (_, _) =>
        {
            var value = tool.SelectedIndex switch { 1 => ToolShortcut.RightAlt, 2 => ToolShortcut.SharedRightCtrl, _ => ToolShortcut.F8 };
            // The shared key leaves the start to the user's physical press.
            if (value == ToolShortcut.SharedRightCtrl) settings.VoiceShortcut = "manual";
            if (ToolShortcut.ConflictsWithSource(value, settings.VoiceShortcut)) settings.VoiceShortcut = "manual";
            Apply(() => settings.ToolShortcut = value, "语音键已更新");
        };
        var after = Choice("结束后的操作", ["自动投送（与 Mac 一致）", "核对后投送"], settings.AutoDeliver ? 0 : 1);
        after.SelectedIndexChanged += (_, _) => Apply(() => settings.AutoDeliver = after.SelectedIndex == 0, "结束后的操作已更新");
        var board = Choice("投送后的剪贴板", ["保留听写文字（默认）", "恢复原内容"], settings.RestoreClipboard ? 1 : 0);
        board.SelectedIndexChanged += (_, _) => Apply(() => settings.RestoreClipboard = board.SelectedIndex == 1, "投送后的剪贴板已更新");
        var pause = new CheckBox { AutoSize = true, Text = "暂停", Checked = Paused(), AccessibleName = "暂停远程语音输入" };
        pause.CheckedChanged += (_, _) => { var value = pause.Checked; BeginInvoke(() => PauseChanged?.Invoke(value)); };
        add(UiStyle.Group(
            UiStyle.Row("语音键", shared ? "按一次打开浮条，再按开始录音，说完再按投送（UU 与远程桌面相同）。仅在远程窗口和浮条中生效，远端不会收到这个键。" : "仅在 UU / 远程桌面窗口与浮条中拦截，其他应用不受影响。", tool),
            UiStyle.Row("结束后的操作", settings.AutoDeliver ? "结束后文字稳定约 0.5 秒自动投送；多行文字仍先核对。" : "结束后先核对，再按语音键投送。", after),
            UiStyle.Row("投送后的剪贴板", settings.RestoreClipboard ? "恢复原内容时，远端同步慢可能贴出原剪贴板内容。" : "保留听写文字时，远端没出现可直接手动粘贴。", board),
            UiStyle.Row("多行文字", $"含换行或控制字符时先展开核对，按 {Key} 投送。", UiStyle.Label("先核对", secondary: true)),
            UiStyle.Row("暂停远程语音输入", $"取消当前听写与待发任务，{Key} 恢复原行为。", pause)));
        if (!shared)
        {
            var source = Choice("豆包启动键", DoubaoShortcutSource.Shortcuts.Select(x => x.Label).ToArray(), Array.FindIndex(DoubaoShortcutSource.Shortcuts, x => x.Id == settings.VoiceShortcut));
            source.SelectedIndexChanged += (_, _) =>
            {
                var value = DoubaoShortcutSource.Shortcuts[source.SelectedIndex].Id;
                if (ToolShortcut.ConflictsWithSource(settings.ToolShortcut, value))
                { status.Text = "工具快捷键与豆包启动键都是右 Alt，浮条会拦截豆包；请改用其他启动键。"; BeginInvoke(() => SelectSection(0)); return; }
                Apply(() => settings.VoiceShortcut = value, "豆包启动键已更新");
            };
            var rows = new List<Control> { UiStyle.Row("豆包启动键", "必须与你在豆包中的设置相同。", source) };
            if (settings.VoiceShortcut != "manual")
            {
                var enter = new CheckBox { AutoSize = true, Text = "使用本地 Enter", Checked = settings.StopWithEnter, AccessibleName = "使用本地 Enter 结束语音" };
                enter.CheckedChanged += (_, _) => Apply(() => settings.StopWithEnter = enter.Checked, "结束语音方式已更新");
                rows.Add(UiStyle.Row("结束语音", "仅当豆包支持 Enter 结束时启用。", enter));
            }
            add(UiStyle.Group(rows.ToArray()));
        }
        add(UiStyle.Label(shared
            ? "需在豆包把语音键设为右 Ctrl、免按模式。远程桌面（mstsc）不把首次按键交给豆包，因此多按一次。与豆包共用右 Ctrl 时不提供“长按重贴”，请用提示上的“再贴一次”或托盘菜单。"
            : settings.ToolShortcut == ToolShortcut.RightAlt
            ? "右 Alt 与 Mac 版同键，但浮条会拦截右 Alt：请先在豆包中把语音键改为右 Ctrl 等其他键。AltGr（右 Alt + Ctrl）保持原样。"
            : "F8 不占用豆包默认的右 Alt 语音键。推荐改为“右 Ctrl 与豆包同键”，全程只用一个键。", secondary: true));
    }
    private void Remote(Action<Control> add)
    {
        add(UiStyle.Label("远程客户端", heading: true)); add(UiStyle.Label("UU 按设备分别记住粘贴键；远程桌面自动识别。", secondary: true));
        var paste = Choice("粘贴按键", ["Ctrl + V · Windows 远端", "Win + V · Mac 远端"], settings.PasteKeyFor(device) == PasteShortcut.WinV ? 1 : 0);
        paste.SelectedIndexChanged += (_, _) =>
        {
            var value = paste.SelectedIndex == 1 ? PasteShortcut.WinV : PasteShortcut.CtrlV;
            Apply(() => { if (device == null) settings.PasteKey = value; else settings.DevicePasteKeys[device] = value; }, "粘贴按键已更新");
        };
        var speed = Choice("同步速度", SyncSpeed.All.Select(SyncSpeed.Label).ToArray(), Array.IndexOf(SyncSpeed.All, settings.Speed));
        speed.SelectedIndexChanged += (_, _) => Apply(() => settings.SyncSpeed = SyncSpeed.All[speed.SelectedIndex], "同步速度已更新");
        var result = settings.CalibrationFor(kind, kind == RemoteClientKind.UU ? device : null);
        var target = kind == RemoteClientKind.RemoteDesktop ? "远程桌面" : device == null ? "UU 远程" : $"UU · {device}";
        add(UiStyle.Group(
            UiStyle.Row(device == null ? "粘贴按键（默认）" : $"粘贴按键 · {device}", device == null ? "未识别到 UU 设备时使用；Mac 远端通常为 Win + V。" : "只用于这台 UU 设备；与你手动粘贴的键相同。", paste),
            UiStyle.Row("同步速度", "远端粘贴出旧内容时，改用兼容速度。远程桌面固定 0.3 秒。", speed),
            UiStyle.Row($"试一次 · {Calibration.Label(result)}", $"{target}：点后切到远端输入框按 {Key}，投送一句测试文字，不启动录音。", UiStyle.Button("试一次", () => BeginInvoke(() => TestDelivery?.Invoke()))),
            UiStyle.Row("UU 主程序", string.IsNullOrEmpty(settings.RemoteExecutable) ? "自动识别失败时选择正在运行的 exe。" : Path.GetFileName(settings.RemoteExecutable), UiStyle.Button("选择程序…", PickRemote))));
        add(UiStyle.Label("请在 UU 中开启本地到远端的剪贴板同步，每次只使用一个活动同步目标。", secondary: true));
        add(UiStyle.Label("远程桌面（mstsc）自动识别，固定使用带扫描码的 Ctrl + V；需在连接选项「本地资源」中勾选剪贴板。", secondary: true));
    }
    private void DiagnosticsPage(Action<Control> add)
    {
        add(UiStyle.Label("运行环境与诊断", heading: true)); add(UiStyle.Label("Windows 版按普通用户权限运行；诊断不含正文。", secondary: true));
        add(UiStyle.Group(
            UiStyle.Row("运行级别", "工具与 UU / 远程桌面尽量使用相同运行级别；提权窗口可能无法接收输入。", UiStyle.Label("普通用户", secondary: true)),
            UiStyle.Row("语音权限", "麦克风与识别由豆包负责。", UiStyle.Label("本工具不录音", secondary: true))));
        add(UiStyle.Actions(UiStyle.Button("打开准备清单", () => OpenSetup?.Invoke())));
        add(UiStyle.Group(
            UiStyle.Row("投送诊断", "仅元数据，不含正文、剪贴板内容或窗口标题。", UiStyle.Button("复制诊断", () => CopyDiagnostics?.Invoke())),
            UiStyle.Row("最近文字", "仅在本机内存，最多 5 条、5 分钟，退出即清空。", UiStyle.Button("清除", () => ClearRecent?.Invoke()))));
        var about = UiStyle.Label($"{ProductInfo.Name} {ProductInfo.VersionLabel} · {Application.ExecutablePath}\n设置：{AppSettings.Location}", secondary: true);
        about.Font = UiStyle.Small; add(about);
    }
    private void Apply(Action change, string message)
    {
        change();
        if (!settings.Save()) { status.Text = "保存失败，请检查本地用户目录权限。"; return; }
        status.Text = message + "，后续投送使用新配置。";
        Changed?.Invoke();
        BeginInvoke(() => { if (!IsDisposed) SelectSection(section); });
    }
    private void PickRemote()
    {
        using var picker = new OpenFileDialog { Title = "选择 UU 正在使用的主程序", Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        Apply(() => settings.RemoteExecutable = picker.FileName, "UU 主程序已更新");
    }
    // The paste row edits the key of the remote last in front (UU device, or the default).
    internal void SetTarget(RemoteClientKind? value, string? uuDevice)
    {
        kind = value ?? RemoteClientKind.UU; device = kind == RemoteClientKind.UU ? uuDevice : null;
        RefreshState();
    }
    internal void SetStatus(string value) => status.Text = value;
    // Deferred: a change may come from one of this page's own controls, which re-rendering disposes.
    internal void RefreshState() { if (Visible && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) SelectSection(section); }); }
}
