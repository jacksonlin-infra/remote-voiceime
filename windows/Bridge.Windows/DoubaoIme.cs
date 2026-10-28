using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteVoiceInput.Windows;

// Doubao answers its voice key only where it is the active input method; a new
// window such as the floatbar starts with the system default, and Doubao then
// shows "当前不是豆包输入法". Like the macOS version selecting the Doubao input
// source, the floatbar switches its own process to Doubao's TSF profile; other
// applications keep their input method.
internal static class DoubaoIme
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Profile
    {
        public uint ProfileType; public ushort LanguageId; public Guid Clsid, GuidProfile, Category;
        public nint SubstituteLayout; public uint Caps; public nint Layout; public uint Flags;
    }
    [ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProfileManager
    {
        [PreserveSig] int ActivateProfile(uint profileType, ushort languageId, ref Guid clsid, ref Guid profile, nint layout, uint flags);
        // Unused slots keep the vtable order of ITfInputProcessorProfileMgr.
        [PreserveSig] int DeactivateProfile();
        [PreserveSig] int GetProfile();
        [PreserveSig] int EnumProfiles();
        [PreserveSig] int ReleaseInputProcessor();
        [PreserveSig] int RegisterProfile();
        [PreserveSig] int UnregisterProfile();
        [PreserveSig] int GetActiveProfile(ref Guid category, out Profile profile);
    }
    private static readonly Guid ProfilesClass = new("33C53A50-F456-4884-B049-85FD643ECFED");
    private static readonly Guid KeyboardCategory = new("34745C63-B2F0-4784-8B67-5E12C8701A31");
    private const ushort Chinese = 0x0804;
    private const uint InputProcessor = 1, KeyboardLayout = 2, ForProcess = 0x10000000, AnyLanguage = 0x4;
    // Observed for Doubao v0.9.2.7; the registry is read first so another build still matches.
    private static readonly (Guid Clsid, Guid Profile) Known = (new("9D2B2E2B-3C93-4D2F-9D35-6EEB85F0D2B0"), new("2B4D4B3A-4D4F-4C0A-8E66-7F771A2B9C10"));
    private static (Guid Clsid, Guid Profile)? identity;
    internal static (Guid Clsid, Guid Profile)? Identity => identity ??= Find();
    private static (Guid Clsid, Guid Profile)? Find()
    {
        try
        {
            using var tips = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\CTF\TIP");
            foreach (var clsid in tips?.GetSubKeyNames() ?? [])
            {
                using var profiles = tips!.OpenSubKey($@"{clsid}\LanguageProfile\0x00000804");
                foreach (var profile in profiles?.GetSubKeyNames() ?? [])
                {
                    using var key = profiles!.OpenSubKey(profile);
                    if ((key?.GetValue("Description") as string)?.Contains("豆包") == true && Guid.TryParse(clsid, out var c) && Guid.TryParse(profile, out var p)) return (c, p);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return DoubaoProbe.Installed ? Known : null;
    }
    // "0804:{clsid}{profile}" is how Windows names the default input method override.
    internal static bool IsSystemDefault
    {
        get
        {
            if (Identity is not { } id) return false;
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International\User Profile", "InputMethodOverride", null) as string;
            return value != null && value.Contains(id.Clsid.ToString("B"), StringComparison.OrdinalIgnoreCase);
        }
    }
    private static IProfileManager? Manager()
    {
        try { return Type.GetTypeFromCLSID(ProfilesClass) is { } type ? Activator.CreateInstance(type) as IProfileManager : null; }
        catch (COMException) { return null; }
    }
    // Metadata for diagnostics: "doubao", another TIP, or a keyboard layout.
    internal static string ActiveName()
    {
        if (Manager() is not { } manager) return "unknown";
        try
        {
            var category = KeyboardCategory;
            if (manager.GetActiveProfile(ref category, out var active) != 0) return "unknown";
            if (active.ProfileType == KeyboardLayout) return $"layout-{(ushort)active.Layout:x4}";
            return Identity is { } id && active.Clsid == id.Clsid ? "doubao" : $"tip-{active.LanguageId:x4}";
        }
        finally { Marshal.ReleaseComObject(manager); }
    }
    internal static bool IsActive => ActiveName() == "doubao";
    // Switches this process (all its threads) to Doubao; returns whether Doubao is active afterwards.
    internal static bool Activate()
    {
        if (Identity is not { } id) return false;
        if (IsActive) return true;
        if (Manager() is not { } manager) return false;
        try
        {
            var clsid = id.Clsid; var profile = id.Profile;
            manager.ActivateProfile(InputProcessor, Chinese, ref clsid, ref profile, 0, ForProcess | AnyLanguage);
        }
        finally { Marshal.ReleaseComObject(manager); }
        return IsActive;
    }
    // Switches away and back, for a thread whose Doubao context stopped answering.
    internal static bool Refresh() { ActivateEnglishLayout(); return Activate(); }
    // Keyboard layout language of a window's thread, e.g. 0804 (metadata only).
    internal static string LayoutOf(nint window) => window == 0 ? "none" : $"{(ushort)Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(window, out _)):x4}";
    // Self-check only: switch this process to a plain English layout, so Doubao's
    // activation is proven from a non-Doubao state like on a machine without the override.
    internal static bool ActivateEnglishLayout()
    {
        if (Manager() is not { } manager) return false;
        try { var none = Guid.Empty; var empty = Guid.Empty; return manager.ActivateProfile(KeyboardLayout, 0x0409, ref none, ref empty, unchecked((nint)0x04090409), ForProcess | AnyLanguage) == 0; }
        finally { Marshal.ReleaseComObject(manager); }
    }
}
