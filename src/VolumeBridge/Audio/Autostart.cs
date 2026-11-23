using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace VolumeBridge.Audio;

/// <summary>
/// Logon autostart through Task Scheduler for the current user, unelevated, normal priority
/// (the default task priority 7 is "below normal", bad for audio), no time limit.
/// </summary>
public static class Autostart
{
    public const string TaskName = "VolumeBridge";

    public static bool IsInstalled() => Run("/Query", "/TN", TaskName).Code == 0;

    public static void Install(string exePath)
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>VolumeBridge: Windows volume for a USB speaker</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exePath)}</Command><Arguments>--tray</Arguments></Exec>
              </Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"VolumeBridge-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            var (code, output) = Run("/Create", "/TN", TaskName, "/XML", file, "/F");
            if (code != 0) throw new InvalidOperationException($"schtasks /Create failed ({code}): {output.Trim()}");
        }
        finally { File.Delete(file); }
    }

    public static void Uninstall()
    {
        var (code, output) = Run("/Delete", "/TN", TaskName, "/F");
        if (code != 0 && IsInstalled()) throw new InvalidOperationException($"schtasks /Delete failed ({code}): {output.Trim()}");
    }

    static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15_000))
        {
            p.Kill(entireProcessTree: true);
            return (-1, "timeout");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
