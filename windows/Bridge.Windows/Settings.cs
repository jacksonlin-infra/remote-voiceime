using System.Text.Json;
using RemoteVoiceInput.Core;

namespace RemoteVoiceInput.Windows;

internal sealed class AppSettings
{
    public int Schema { get; set; } = 1;
    public string VoiceShortcut { get; set; } = "manual";
    public string ToolShortcut { get; set; } = Core.ToolShortcut.F8;
    public string PasteKey { get; set; } = PasteShortcut.CtrlV;
    public bool AutoDeliver { get; set; }
    public Dictionary<string, string> DevicePasteKeys { get; set; } = [];
    internal string PasteKeyFor(string? device) => PasteShortcut.For(DevicePasteKeys, device, PasteKey);
    public bool StopWithEnter { get; set; }
    // 0.2 boolean (compatible / fast); still written so an older build keeps its wait.
    public bool? CompatibilityDelay { get; set; }
    public string? SyncSpeed { get; set; }
    // false (default): the transcript stays on the clipboard after delivery.
    public bool RestoreClipboard { get; set; }
    public bool OnboardingCompleted { get; set; }
    // Checklist confirmations of what cannot be detected; null before 0.3, when
    // a completed first-run guide already covered them.
    public bool? ClipboardConfirmed { get; set; }
    public bool? VoiceKeyConfirmed { get; set; }
    // "Try once" results: Calibration.Key -> Calibration.Encode.
    public Dictionary<string, string> Calibrations { get; set; } = [];
    public string RemoteExecutable { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] internal string Speed => Core.SyncSpeed.Migrate(SyncSpeed, CompatibilityDelay);
    [System.Text.Json.Serialization.JsonIgnore] internal bool ClipboardReady => ClipboardConfirmed ?? OnboardingCompleted;
    [System.Text.Json.Serialization.JsonIgnore] internal bool VoiceKeyReady => VoiceKeyConfirmed ?? OnboardingCompleted;
    internal string CalibrationProfile(RemoteClientKind kind, string? device) => Calibration.Profile(kind, PasteKeyFor(device), Speed);
    internal CalibrationResult CalibrationFor(RemoteClientKind kind, string? device) =>
        Calibration.Read(Calibrations, Calibration.Key(kind, device), CalibrationProfile(kind, device));
    private static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteVoiceInput", "settings.json");
    internal static string Location => ConfigPath;
    internal static AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath)) ?? new();
            if (settings.Schema != 1) return new();
            if (!DoubaoShortcutSource.Shortcuts.Any(x => x.Id == settings.VoiceShortcut)) settings.VoiceShortcut = "manual";
            if (!Core.ToolShortcut.IsKnown(settings.ToolShortcut ?? "")) settings.ToolShortcut = Core.ToolShortcut.F8;
            if (!PasteShortcut.IsKnown(settings.PasteKey ?? "")) settings.PasteKey = PasteShortcut.CtrlV;
            if (Core.ToolShortcut.ConflictsWithSource(settings.ToolShortcut!, settings.VoiceShortcut)) settings.ToolShortcut = Core.ToolShortcut.F8;
            settings.SyncSpeed = settings.Speed; settings.DevicePasteKeys ??= []; settings.Calibrations ??= [];
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal bool Save()
    {
        try
        {
            // An older build reads only the boolean: anything slower than fast maps to its safe wait.
            SyncSpeed = Speed; CompatibilityDelay = Speed != Core.SyncSpeed.Fast;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var temporary = ConfigPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, ConfigPath, true); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
