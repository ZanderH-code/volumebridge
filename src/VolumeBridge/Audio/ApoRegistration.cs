using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using VolumeBridge.Core;

namespace VolumeBridge.Audio;

/// <summary>
/// Puts VolumeBridgeApo into a render endpoint's endpoint-effect (EFX) slot. Machine-wide; needs
/// administrator rights. The endpoint keys are owned by TrustedInstaller, so ownership is taken and
/// Administrators get write access (same approach as Equalizer APO's configurator). The original
/// FxProperties values are backed up once and restored on removal.
/// </summary>
public static class ApoRegistration
{
    public const string ApoClsid = "{B2800674-8C35-428D-A3EA-750171E7DD5C}";

    const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    const string BackupRoot = @"SOFTWARE\VolumeBridge\FxBackup";
    const string StateKey = @"SOFTWARE\VolumeBridge";
    const string AudioKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";
    const string LegacyUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6F1C2B7E-4A0B-4D8E-9C55-5642B0A1D001}_is1";
    const string ApoResourceName = "VolumeBridgeApo.dll";
    const uint MoveFileDelayUntilReboot = 0x4;

    static readonly Lazy<string?> EmbeddedApoHash = new(ComputeEmbeddedApoSha256);
    static readonly object HashCacheGate = new();
    static string? _cachedFilePath;
    static DateTime _cachedFileMtime;
    static long _cachedFileLen;
    static string? _cachedFileHash;

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
        string.IsNullOrWhiteSpace(s) || !Guid.TryParse(s, out var g) || g == Guid.Empty ? null : s!.Trim();

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
    public static string KeyName(string endpointId) => endpointId.Substring(endpointId.LastIndexOf('.') + 1);

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
    public static bool ComRegistered() => GetRegisteredApoPath() is string path && File.Exists(path);

    /// <summary>Path of the registered APO DLL from InprocServer32, or null if not registered.</summary>
    public static string? GetRegisteredApoPath()
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{ApoClsid}\InprocServer32");
        string? path = k?.GetValue(null) as string;
        return string.IsNullOrWhiteSpace(path) ? null : path!.Trim().Trim('"');
    }

    /// <summary>True when the old Inno Setup installation (0.4.0 and earlier) is still installed. Readable unelevated.</summary>
    public static bool IsLegacyInstallerPresent()
    {
        try
        {
            using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm64.OpenSubKey(LegacyUninstallKey);
            return k is not null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>True when the registered APO DLL differs from the one embedded in this exe.</summary>
    public static bool IsPluginOutdated()
    {
        string? embedded = EmbeddedApoHash.Value;
        if (embedded is null) return false;
        string? regPath = GetRegisteredApoPath();
        if (regPath is null || !File.Exists(regPath)) return false;
        string? actual = GetCachedFileSha256(regPath);
        return actual is not null && !string.Equals(actual, embedded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when any plugin registration, backup, or deployed DLL is present on the machine.</summary>
    public static bool IsAnyPluginInstalled()
    {
        if (ComRegistered()) return true;
        try
        {
            using var state = Registry.LocalMachine.OpenSubKey(StateKey);
            if (state is not null)
            {
                if (state.GetValue("PrevDisableProtectedAudioDG") is not null) return true;
                using var backup = state.OpenSubKey("FxBackup");
                if (backup is not null && backup.SubKeyCount > 0) return true;
            }
            string dir = PluginInstallDir;
            if (Directory.Exists(dir) && EnumeratePluginDlls(dir).Any()) return true;
        }
        catch (Exception) { }
        return false;
    }

    public static string PluginInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VolumeBridge");

    /// <summary>
    /// Elevated: extract the embedded APO DLL to %ProgramFiles%\VolumeBridge\VolumeBridgeApo-&lt;8hex&gt;.dll,
    /// register it with regsvr32, and ensure %ProgramData%\VolumeBridge is writable by Everyone.
    /// Returns the deployed DLL path.
    /// </summary>
    public static string DeployAndRegisterDll(Action<string> log)
    {
        EnsureProgramDataWritable(log);

        byte[] bytes = ReadEmbeddedApoBytes()
            ?? throw new InvalidOperationException("This build does not contain an embedded VolumeBridgeApo.dll.");
        string hash = UpdateCheck.Sha256Hex(bytes);
        string dir = PluginInstallDir;
        Directory.CreateDirectory(dir);
        string dllPath = Path.Combine(dir, $"VolumeBridgeApo-{hash.Substring(0, 8)}.dll");

        if (File.Exists(dllPath) && string.Equals(UpdateCheck.Sha256FileHex(dllPath), hash, StringComparison.OrdinalIgnoreCase))
        {
            log($"APO DLL already up to date: {dllPath}");
        }
        else
        {
            File.WriteAllBytes(dllPath, bytes);
            log($"extracted APO DLL ({bytes.Length} bytes, sha256={hash.Substring(0, 8)}): {dllPath}");
        }

        RunRegsvr32(dllPath, unregister: false, log);
        return dllPath;
    }

    /// <summary>Best-effort cleanup of old APO DLLs in %ProgramFiles%\VolumeBridge except <paramref name="keepPath"/>.</summary>
    public static void CleanupOldDlls(string? keepPath, bool removeDirIfEmpty, Action<string> log)
    {
        string dir = PluginInstallDir;
        if (!Directory.Exists(dir)) return;
        string? keepFull = keepPath is null ? null : Path.GetFullPath(keepPath);
        foreach (string f in EnumeratePluginDlls(dir).ToList())
        {
            if (keepFull is not null && string.Equals(Path.GetFullPath(f), keepFull, StringComparison.OrdinalIgnoreCase))
                continue;
            DeleteOrDelayReboot(f, log);
        }
        if (removeDirIfEmpty)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    log($"removed empty directory: {dir}");
                }
            }
            catch (Exception e) { log($"could not remove {dir}: {e.Message}"); }
        }
    }

    /// <summary>Elevated: restore audio settings, unregister the COM DLL, delete plugin DLLs and empty state key.</summary>
    public static void FullRemove(Action<string> log)
    {
        string? regPath = GetRegisteredApoPath();
        RemoveAll(log);

        if (regPath is not null && File.Exists(regPath))
        {
            try { RunRegsvr32(regPath, unregister: true, log); }
            catch (Exception e) { log($"regsvr32 /u failed (continuing): {e.Message}"); }
        }

        CleanupOldDlls(keepPath: null, removeDirIfEmpty: true, log);
        DeleteStateKeyIfEmpty(log);
    }

    static IEnumerable<string> EnumeratePluginDlls(string dir)
    {
        foreach (string f in Directory.EnumerateFiles(dir, "VolumeBridgeApo-*.dll")) yield return f;
        string unversioned = Path.Combine(dir, "VolumeBridgeApo.dll");
        if (File.Exists(unversioned)) yield return unversioned;
    }

    static void DeleteOrDelayReboot(string path, Action<string> log)
    {
        try
        {
            File.Delete(path);
            log($"deleted old DLL: {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (MoveFileExW(path, null, MoveFileDelayUntilReboot))
                log($"DLL in use, scheduled delete on reboot: {path}");
            else
                log($"could not delete or schedule delete for {path}: win32={Marshal.GetLastWin32Error()}");
        }
    }

    static void RunRegsvr32(string dllPath, bool unregister, Action<string> log)
    {
        string regsvr = Path.Combine(Environment.SystemDirectory, "regsvr32.exe");
        string args = unregister ? $"/u /s \"{dllPath}\"" : $"/s \"{dllPath}\"";
        using var p = Process.Start(new ProcessStartInfo(regsvr, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch (Exception) { }
            throw new InvalidOperationException($"regsvr32 {args} timed out");
        }
        log($"regsvr32 {args} -> exit {p.ExitCode}");
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"regsvr32 {args} failed with exit code {p.ExitCode}");
    }

    static void EnsureProgramDataWritable(Action<string> log)
    {
        try
        {
            string dir = Path.GetDirectoryName(ApoShared.FilePath)!;
            Directory.CreateDirectory(dir);
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            sec.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        catch (Exception e) { log($"could not set ProgramData ACL (continuing): {e.Message}"); }
    }

    static void DeleteStateKeyIfEmpty(Action<string> log)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(StateKey);
            if (k is null) return;
            if (k.SubKeyCount == 0 && k.ValueCount == 0)
            {
                Registry.LocalMachine.DeleteSubKey(StateKey, throwOnMissingSubKey: false);
                log($@"removed empty HKLM\{StateKey}");
            }
        }
        catch (Exception e) { log($@"could not clean HKLM\{StateKey}: {e.Message}"); }
    }

    static byte[]? ReadEmbeddedApoBytes()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ApoResourceName);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static string? ComputeEmbeddedApoSha256()
    {
        byte[]? bytes = ReadEmbeddedApoBytes();
        return bytes is null ? null : UpdateCheck.Sha256Hex(bytes);
    }

    static string? GetCachedFileSha256(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            DateTime mtime = fi.LastWriteTimeUtc;
            long len = fi.Length;
            lock (HashCacheGate)
            {
                if (_cachedFilePath == path && _cachedFileMtime == mtime && _cachedFileLen == len && _cachedFileHash is not null)
                    return _cachedFileHash;
            }
            string hash = UpdateCheck.Sha256FileHex(path);
            lock (HashCacheGate)
            {
                _cachedFilePath = path;
                _cachedFileMtime = mtime;
                _cachedFileLen = len;
                _cachedFileHash = hash;
            }
            return hash;
        }
        catch (Exception) { return null; }
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
                byte[] b => BitConverter.ToString(b, 0, Math.Min(b.Length, 32)).Replace("-", "") + (b.Length > 32 ? "…" : ""),
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

    /// <summary>
    /// Restarts Windows Audio (like Equalizer APO) so audiodg rebuilds the effect chain: remembers which
    /// dependent services were running, waits for a clean stop, retries the start, then brings the
    /// dependents back. Throws if Windows Audio is not running afterwards.
    /// </summary>
    public static void RestartAudioService(Action<string> log)
    {
        using var audio = new ServiceController("Audiosrv");
        var dependents = audio.DependentServices.Where(d => d.Status == ServiceControllerStatus.Running).Select(d => d.ServiceName).ToList();
        log($"restarting Audiosrv (running dependents: {(dependents.Count == 0 ? "none" : string.Join(", ", dependents))})");

        audio.Refresh();
        if (audio.Status != ServiceControllerStatus.Stopped)
        {
            try { audio.Stop(); } catch (InvalidOperationException e) { log($"stop: {e.Message}"); }
            try { audio.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
            catch (System.ServiceProcess.TimeoutException) { log("Audiosrv did not stop within 30 s"); }
        }

        for (int attempt = 1; ; attempt++)
        {
            audio.Refresh();
            if (audio.Status == ServiceControllerStatus.Running) break;
            try
            {
                if (audio.Status is ServiceControllerStatus.Stopped) audio.Start();
                audio.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException)
            {
                log($"start attempt {attempt}: {e.Message}");
                if (attempt >= 4) throw new InvalidOperationException("Windows Audio could not be started again; please restart Windows.");
                Thread.Sleep(1000);
            }
        }
        log("Audiosrv running");

        foreach (var name in dependents)
        {
            try
            {
                using var dep = new ServiceController(name);
                if (dep.Status == ServiceControllerStatus.Stopped) dep.Start();
            }
            catch (Exception e) { log($"could not restart dependent {name}: {e.Message}"); }
        }
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out IntPtr token)) // ADJUST_PRIVILEGES | QUERY
            throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValueW(null, name, out long luid)) throw new Win32Exception();
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"cannot enable {name} (run as administrator)");
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string? newFileName, uint flags);
}
