using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VolumeBridge.Core.Usb;

/// <summary>Drives usbip-win2's usbip.exe (attach / detach / port) for the local server.</summary>
public sealed partial class UsbipCli(string exePath, Action<string> log)
{
    public string ExePath { get; } = exePath;

    /// <summary>usbip.exe from config, the default install folders, or PATH; null if none exists.</summary>
    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var candidates = new List<string>();
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            candidates.Add(Path.Combine(root, "USBip", "usbip.exe"));
            candidates.Add(Path.Combine(root, "usbip-win2", "usbip.exe"));
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0) candidates.Add(Path.Combine(dir, "usbip.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Attaches 127.0.0.1:<paramref name="tcpPort"/>/1-1; returns the VHCI port number.</summary>
    public int Attach(int tcpPort)
    {
        // --tcp-port is a global option and must precede the subcommand; --once stops the driver
        // from retrying forever if we are not listening. No --serial: broken in usbip-win2 0.9.8.1.
        var (code, output) = Run("--tcp-port", tcpPort.ToString(), "attach", "-r", "127.0.0.1", "-b", UsbipServer.BusId, "--once");
        if (code != 0) throw new InvalidOperationException($"usbip attach failed (exit {code}): {output.Trim()}");
        return ParseAttachedPort(output) ?? throw new InvalidOperationException($"usbip attach: no port in output: {output.Trim()}");
    }

    public void Detach(int port)
    {
        var (code, output) = Run("detach", "-p", port.ToString());
        if (code != 0) log($"usbip detach -p {port} failed (exit {code}): {output.Trim()}");
    }

    /// <summary>Detaches leftovers of earlier runs (e.g. after a crash) that point at our server.</summary>
    public void DetachStale(int tcpPort)
    {
        // After a crash the driver keeps background re-attach attempts for our location; a later
        // successful re-attach would replace our new session. --once does not cancel them, so stop them.
        Run("--tcp-port", tcpPort.ToString(), "attach", "-r", "127.0.0.1", "-b", UsbipServer.BusId, "--stop");
        var (code, output) = Run("port");
        if (code != 0) return; // "no imported devices" is also reported via exit code on some versions
        foreach (int port in ParseOwnPorts(output, tcpPort))
        {
            log($"usbip: detaching stale port {port}");
            Detach(port);
        }
    }

    (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start usbip.exe");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"usbip {string.Join(' ', args)} timed out");
        }
        string all = stdout.Result + stderr.Result;
        log($"usbip {string.Join(' ', args)} -> exit {p.ExitCode}: {all.Trim().Replace(Environment.NewLine, " | ")}");
        return (p.ExitCode, all);
    }

    [GeneratedRegex(@"attached to port (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AttachedRegex();

    [GeneratedRegex(@"^\s*Port\s+(\d+):", RegexOptions.IgnoreCase)]
    private static partial Regex PortHeaderRegex();

    public static int? ParseAttachedPort(string output)
    {
        var m = AttachedRegex().Match(output);
        if (m.Success) return int.Parse(m.Groups[1].Value);
        // Terse mode prints just the number.
        foreach (var line in output.Split('\n'))
            if (int.TryParse(line.Trim(), out int n) && n > 0) return n;
        return null;
    }

    /// <summary>Ports in `usbip port` output whose location is usbip://127.0.0.1:{tcpPort}/1-1.</summary>
    public static List<int> ParseOwnPorts(string output, int tcpPort)
    {
        string location = $"usbip://127.0.0.1:{tcpPort}/{UsbipServer.BusId}";
        var result = new List<int>();
        int? current = null;
        foreach (var line in output.Split('\n'))
        {
            var m = PortHeaderRegex().Match(line);
            if (m.Success) { current = int.Parse(m.Groups[1].Value); continue; }
            if (current is int port && line.Trim().EndsWith(location, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(port);
                current = null;
            }
        }
        return result;
    }
}
