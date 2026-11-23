using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace VolumeBridge.Audio;

/// <summary>
/// Puts VolumeBridgeApo into a render endpoint's endpoint-effect (EFX) slot. Machine-wide; needs
/// administrator rights. The endpoint keys are owned by TrustedInstaller, so ownership is taken and
/// Administrators get write access (same approach as Equalizer APO's configurator). The original
/// FxProperties values are backed up once and restored on removal.
/// </summary>
public static partial class ApoRegistration
{
    public const string ApoClsid = "{B2800674-8C35-428D-A3EA-750171E7DD5C}";

    const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    const string BackupRoot = @"SOFTWARE\VolumeBridge\FxBackup";
    const string StateKey = @"SOFTWARE\VolumeBridge";
    const string AudioKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";

    // PKEY_FX_EndpointEffectClsid, PKEY_CompositeFX_EndpointEffectClsid, PKEY_EFX_ProcessingModes_Supported_For_Streaming
    const string EfxSingle = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
    const string EfxComposite = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";
    const string EfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";
    const string ModeDefault = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    const string NoBackupMarker = "__VolumeBridge_NoFxProperties";
    const string FxAssociation = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0"; // PKEY_FX_Association
    // Legacy (Windows 7 style) PKEY_FX_PreMixEffectClsid / PKEY_FX_PostMixEffectClsid. While either is present,
    // Windows builds the legacy LFX/GFX chain and ignores SFX/MFX/EFX (Equalizer APO removes them for that reason).
    const string LegacyLfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1";
    const string LegacyGfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2";
    const string DisableSysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";   // PKEY_AudioEndpoint_Disable_SysFx

    /// <summary>A CLSID string, or null for blank / all-zero entries.</summary>
    static string? RealClsid(string? s) =>
        string.IsNullOrWhiteSpace(s) || !Guid.TryParse(s, out var g) || g == Guid.Empty ? null : s.Trim();

    /// <summary>"Audio enhancements: Off" for this endpoint (stored in FxProperties). Readable unelevated.</summary>
    public static bool EnhancementsDisabled(string endpointId)
    {
        using var fx = Registry.LocalMachine.OpenSubKey($@"{RenderRoot}\{KeyName(endpointId)}\FxProperties");
        return fx?.GetValue(DisableSysFx) switch
        {
            int i => i != 0,
            byte[] b => b.Length >= 4 && BitConverter.ToInt32(b, b.Length >= 12 ? 8 : 0) != 0, // VT_UI4 PROPVARIANT blob
            _ => false,
        };
    }

    /// <summary>"{0.0.0.00000000}.{guid}" → "{guid}" (the registry key name).</summary>
    public static string KeyName(string endpointId) => endpointId[(endpointId.LastIndexOf('.') + 1)..];

    /// <summary>True when the endpoint's EFX slot (single or composite) contains our APO. Readable unelevated.</summary>
    public static bool IsRegistered(string endpointId)
    {
        using var fx = Registry.LocalMachine.OpenSubKey($@"{RenderRoot}\{KeyName(endpointId)}\FxProperties");
        if (fx is null) return false;
        if (fx.GetValue(LegacyLfx) is not null || fx.GetValue(LegacyGfx) is not null) return false; // EFX would be ignored
        if (string.Equals(fx.GetValue(EfxSingle) as string, ApoClsid, StringComparison.OrdinalIgnoreCase)) return true;
        return fx.GetValue(EfxComposite) is string[] list && list.Any(c => string.Equals(c, ApoClsid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when Windows lets unsigned APOs load (audiodg not protected).</summary>
    public static bool UnsignedApoAllowed()
    {
        using var k = Registry.LocalMachine.OpenSubKey(AudioKey);
        return k?.GetValue("DisableProtectedAudioDG") is int v && v == 1;
    }

    /// <summary>True when the APO's COM class is registered (regsvr32 ran).</summary>
    public static bool ComRegistered()
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{ApoClsid}\InprocServer32");
        return k?.GetValue(null) is string path && File.Exists(path);
    }

    /// <summary>Elevated: allow unsigned APOs (remembering the previous value) and add our APO to the endpoints.</summary>
    public static void Install(IEnumerable<string> endpointIds, Action<string> log)
    {
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        AllowUnsignedApos(log);
        foreach (var id in endpointIds) AddToEndpoint(KeyName(id), log);
        RestartAudioService(log);
    }

    /// <summary>Elevated: restore every endpoint we changed and the DisableProtectedAudioDG value.</summary>
    public static void RemoveAll(Action<string> log)
    {
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        using (var backups = Registry.LocalMachine.OpenSubKey(BackupRoot))
        {
            foreach (var guid in backups?.GetSubKeyNames() ?? [])
                RestoreEndpoint(guid, log);
        }
        Registry.LocalMachine.DeleteSubKeyTree(BackupRoot, throwOnMissingSubKey: false);
        RestoreUnsignedApoSetting(log);
        RestartAudioService(log);
    }

    static void AllowUnsignedApos(Action<string> log)
    {
        using var audio = Registry.LocalMachine.CreateSubKey(AudioKey, writable: true);
        using var state = Registry.LocalMachine.CreateSubKey(StateKey, writable: true);
        if (state.GetValue("PrevDisableProtectedAudioDG") is null)
            state.SetValue("PrevDisableProtectedAudioDG", audio.GetValue("DisableProtectedAudioDG") is int prev ? prev : -1, RegistryValueKind.DWord);
        audio.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
        log("DisableProtectedAudioDG = 1");
    }

    static void RestoreUnsignedApoSetting(Action<string> log)
    {
        using var state = Registry.LocalMachine.OpenSubKey(StateKey, writable: true);
        if (state?.GetValue("PrevDisableProtectedAudioDG") is not int prev) return;
        using var audio = Registry.LocalMachine.CreateSubKey(AudioKey, writable: true);
        if (prev == -1) audio.DeleteValue("DisableProtectedAudioDG", throwOnMissingValue: false);
        else audio.SetValue("DisableProtectedAudioDG", prev, RegistryValueKind.DWord);
        state.DeleteValue("PrevDisableProtectedAudioDG", throwOnMissingValue: false);
        log($"DisableProtectedAudioDG restored ({(prev == -1 ? "absent" : prev.ToString())})");
    }

    static void AddToEndpoint(string guid, Action<string> log)
    {
        string endpointPath = $@"{RenderRoot}\{guid}";
        GrantAdministrators(endpointPath);
        using var endpoint = Registry.LocalMachine.OpenSubKey(endpointPath, writable: true)
            ?? throw new InvalidOperationException($"endpoint {guid} not found");
        var fxExisting = endpoint.OpenSubKey("FxProperties");
        if (fxExisting is not null)
        {
            fxExisting.Dispose();
            GrantAdministrators($@"{endpointPath}\FxProperties");
        }

        Backup(guid, endpoint, log);

        using var fx = endpoint.CreateSubKey("FxProperties", writable: true);
        Dump(fx, $"FxProperties of {guid} before", log);
        foreach (var legacy in new[] { LegacyLfx, LegacyGfx })
        {
            if (fx.GetValue(legacy) is null) continue;
            fx.DeleteValue(legacy, throwOnMissingValue: false); // original kept in the backup, restored on removal
            log($"removed legacy effect {legacy} (Windows ignores EFX while it exists)");
        }
        string? single = RealClsid(fx.GetValue(EfxSingle) as string);
        if (fx.GetValue(EfxComposite) is string[] composite)
        {
            var list = composite.Select(RealClsid).OfType<string>().ToList();
            if (!list.Contains(ApoClsid, StringComparer.OrdinalIgnoreCase)) list.Add(ApoClsid);
            fx.SetValue(EfxComposite, list.ToArray(), RegistryValueKind.MultiString);
            fx.DeleteValue(EfxSingle, throwOnMissingValue: false); // single and composite are mutually exclusive
        }
        else if (single is not null && !string.Equals(single, ApoClsid, StringComparison.OrdinalIgnoreCase))
        {
            // Keep the existing endpoint effect and chain ours after it (the original stays in the backup).
            fx.SetValue(EfxComposite, new[] { single, ApoClsid }, RegistryValueKind.MultiString);
            fx.DeleteValue(EfxSingle, throwOnMissingValue: false);
        }
        else
        {
            fx.SetValue(EfxSingle, ApoClsid, RegistryValueKind.String);
        }
        if (fx.GetValue(FxAssociation) is null)
            fx.SetValue(FxAssociation, "{00000000-0000-0000-0000-000000000000}", RegistryValueKind.String); // KSNODETYPE_ANY
        if (fx.GetValue(EfxModes) is not string[] modes || !modes.Contains(ModeDefault, StringComparer.OrdinalIgnoreCase))
            fx.SetValue(EfxModes, (fx.GetValue(EfxModes) as string[] ?? []).Append(ModeDefault).ToArray(), RegistryValueKind.MultiString);
        Dump(fx, $"FxProperties of {guid} after", log);
        log($"APO added to endpoint {guid}");
    }

    static void Dump(RegistryKey fx, string title, Action<string> log)
    {
        log(title + ":");
        foreach (var name in fx.GetValueNames())
        {
            object? v = fx.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string text = v switch
            {
                string[] a => string.Join(" | ", a),
                byte[] b => Convert.ToHexString(b.Length > 32 ? b[..32] : b) + (b.Length > 32 ? "…" : ""),
                _ => v?.ToString() ?? "",
            };
            log($"  {name} [{fx.GetValueKind(name)}] = {text}");
        }
    }

    /// <summary>Saves FxProperties exactly once (the original state), or a marker if it did not exist.</summary>
    static void Backup(string guid, RegistryKey endpoint, Action<string> log)
    {
        using var root = Registry.LocalMachine.CreateSubKey(BackupRoot, writable: true);
        if (root.GetSubKeyNames().Contains(guid, StringComparer.OrdinalIgnoreCase)) return;
        using var backup = root.CreateSubKey(guid, writable: true);
        using var fx = endpoint.OpenSubKey("FxProperties");
        if (fx is null)
        {
            backup.SetValue(NoBackupMarker, 1, RegistryValueKind.DWord);
        }
        else
        {
            foreach (var name in fx.GetValueNames())
                backup.SetValue(name, fx.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, fx.GetValueKind(name));
        }
        log($"backed up FxProperties of {guid}");
    }

    static void RestoreEndpoint(string guid, Action<string> log)
    {
        string endpointPath = $@"{RenderRoot}\{guid}";
        using var backup = Registry.LocalMachine.OpenSubKey($@"{BackupRoot}\{guid}");
        using var endpointProbe = Registry.LocalMachine.OpenSubKey(endpointPath);
        if (backup is null || endpointProbe is null) return; // endpoint gone: nothing to restore
        GrantAdministrators(endpointPath);
        using var endpoint = Registry.LocalMachine.OpenSubKey(endpointPath, writable: true)!;
        if (backup.GetValue(NoBackupMarker) is not null)
        {
            endpoint.DeleteSubKeyTree("FxProperties", throwOnMissingSubKey: false);
        }
        else
        {
            GrantAdministrators($@"{endpointPath}\FxProperties");
            using var fx = endpoint.CreateSubKey("FxProperties", writable: true);
            foreach (var name in fx.GetValueNames()) fx.DeleteValue(name, throwOnMissingValue: false);
            foreach (var name in backup.GetValueNames())
                fx.SetValue(name, backup.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, backup.GetValueKind(name));
        }
        log($"FxProperties of {guid} restored");
    }

    /// <summary>Takes ownership of a TrustedInstaller-owned key and gives Administrators full control.</summary>
    static void GrantAdministrators(string path)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using (var k = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership))
        {
            if (k is null) return;
            // Only WRITE_OWNER is granted before we own the key: write a fresh owner without reading the old one.
            var sec = new RegistrySecurity();
            sec.SetOwner(admins);
            k.SetAccessControl(sec);
        }
        using (var k = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions))
        {
            if (k is null) return;
            var sec = k.GetAccessControl(AccessControlSections.Access);
            sec.AddAccessRule(new RegistryAccessRule(admins, RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            k.SetAccessControl(sec);
        }
    }

    /// <summary>Restarts the Windows Audio service so audiodg reloads the effect chain (sound pauses ~2 s).</summary>
    public static void RestartAudioService(Action<string> log)
    {
        // The endpoint builder caches endpoint effect settings; restart it too (this also stops audiosrv).
        RunNet("stop audiosrv /y", log);
        RunNet("stop AudioEndpointBuilder /y", log);
        RunNet("start AudioEndpointBuilder", log);
        RunNet("start audiosrv", log);
    }

    static void RunNet(string args, Action<string> log)
    {
        var psi = new ProcessStartInfo("net.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            log($"net {args} timed out");
            return;
        }
        log($"net {args} -> {p.ExitCode} {(o.Result + e.Result).Trim().Replace(Environment.NewLine, " | ")}");
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out IntPtr token)) // ADJUST_PRIVILEGES | QUERY
            throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValueW(null, name, out long luid)) throw new Win32Exception();
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"cannot enable {name} (run as administrator)");
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previous, IntPtr returnLength);
}
