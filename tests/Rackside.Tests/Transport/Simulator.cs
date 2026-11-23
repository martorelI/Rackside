using System.Diagnostics;
using System.Net.Sockets;

namespace Rackside.Tests.Transport;

/// <summary>
/// Starts a Python simulator from tests/sim. The console ones use a
/// pty: Linux or WSL only. Each instance gets its own SIM_DIR under tests/out/dotnet.
/// </summary>
internal sealed class Simulator : IDisposable
{
    private readonly Process process;

    private Simulator(Process process, string dir)
    {
        this.process = process;
        Dir = dir;
    }

    public string Dir { get; }

    public static bool CanRunPty => !OperatingSystem.IsWindows() && Python is not null;

    public static string? Python { get; } = FindPython();

    private static string? FindPython()
    {
        var env = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }
        // On Windows: python.exe, skipping the Microsoft Store alias in WindowsApps (it is not Python).
        var names = OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : new[] { "python3", "python" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var name in names)
            {
                var f = Path.Combine(dir, name);
                if (File.Exists(f))
                {
                    return f;
                }
            }
        }
        return null;
    }

    public static Simulator Start(string script, string[] args, IDictionary<string, string>? env = null, string? waitFile = null, int waitPort = 0, int seconds = 30) =>
        StartIn(Path.Combine(RepoPaths.Root, "tests", "out", "dotnet", Path.GetFileNameWithoutExtension(script) + "_" + Guid.NewGuid().ToString("N")[..8]), script, env, waitFile, waitPort, seconds, args);

    /// <summary>Another simulator sharing a SIM_DIR (ssh_bridge.py next to console_scp.py).</summary>
    public static Simulator StartIn(string dir, string script, IDictionary<string, string>? env = null, string? waitFile = null, int waitPort = 0, int seconds = 30, string[]? args = null)
    {
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(Python ?? throw new InvalidOperationException("Python was not found (set PYTHON)."))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(RepoPaths.Combine("tests", "sim", script));
        foreach (var a in args ?? [])
        {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["SIM_DIR"] = dir;
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[k] = v.Replace("{dir}", dir, StringComparison.Ordinal);
        }
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        var sim = new Simulator(p, dir);
        var file = waitFile?.Replace("{dir}", dir, StringComparison.Ordinal);
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (p.HasExited)
            {
                throw new InvalidOperationException($"The simulator {script} exited: {p.StandardError.ReadToEnd()}");
            }
            if (file is not null && File.Exists(file) && new FileInfo(file).Length > 0)
            {
                Thread.Sleep(300);
                return sim;
            }
            if (waitPort > 0 && PortOpen(waitPort))
            {
                Thread.Sleep(300);
                return sim;
            }
            Thread.Sleep(200);
        }
        sim.Dispose();
        throw new InvalidOperationException($"The simulator {script} was not ready in {seconds} s");
    }

    public string ReadFile(string name) => File.ReadAllText(Path.Combine(Dir, name)).Trim();

    private static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        process.Dispose();
    }
}
