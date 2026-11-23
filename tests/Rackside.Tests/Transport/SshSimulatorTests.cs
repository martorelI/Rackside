using Rackside.Core.Localization;
using Rackside.Core.Security;
using Rackside.Transport.Console;
using Rackside.Transport.Ssh;

namespace Rackside.Tests.Transport;

/// <summary>
/// SSH against sim/configure.py ssh (SW01 IOS privileged, SW02 IOS-XE that needs enable) on ports 2341-2344
/// and SCP against sim/console_scp.py + sim/ssh_bridge.py on 2324.
/// </summary>
public sealed class SshSimulatorTests
{
    private const int Base = 2341;

    private static string FingerprintsFile(Simulator sim) => Path.Combine(sim.Dir, "SSHFingerprints", "ssh_fingerprints.csv");

    private static SshTrust Accept(string ip, int port, FingerprintStore store, bool answer = true) =>
        FingerprintPolicy.Confirm(new FingerprintRequest(ip, port), SshConnector.ReadHostKey(ip, port, Translator.English), store, Translator.English, (_, _) => answer);

    [SkippableFact]
    public void Shell_login_enable_and_configuration_over_ssh()
    {
        Skip.If(Simulator.Python is null, "requires Python with paramiko");
        using var sim = Simulator.Start("configure.py", ["ssh"], new Dictionary<string, string> { ["PORT_BASE"] = Base.ToString(System.Globalization.CultureInfo.InvariantCulture) }, waitPort: Base + 3, seconds: 60);
        var t = Translator.English;
        var store = new FingerprintStore(FingerprintsFile(sim));
        var masker = new SecretMasker();
        masker.Register("Pa55w0rd!", "En4ble");

        // SW01: already privileged after login.
        var trust1 = Accept("127.0.0.1", Base, store);
        Assert.Equal("rsa-sha2-512", trust1.Algorithm);
        using (var ch = SshShellChannel.Open(trust1, "admin", "Pa55w0rd!", t))
        {
            var s = new ConsoleSession(ch, t, masker);
            Assert.Equal("SW01", s.Connect("admin", "Pa55w0rd!", "En4ble"));
            s.RunCommand("terminal length 0", 20);
            Assert.Contains("WS-C2960X-48FPD-L", s.RunCommand("show version"), StringComparison.Ordinal);
        }

        // SW02: user mode, needs enable.
        var trust2 = Accept("127.0.0.1", Base + 1, store);
        using (var ch = SshShellChannel.Open(trust2, "admin", "Pa55w0rd!", t))
        {
            var s = new ConsoleSession(ch, t, masker);
            Assert.Equal("SW02", s.Connect("admin", "Pa55w0rd!", "En4ble"));
            var r = s.ApplyPlan([new("lldp run"), new("mls qos")], 2);
            Assert.Equal(["mls qos"], r.Errors.Select(e => e.Line));
        }
        Assert.Equal(2, store.Read().Count);

        // Bad password: the key is pinned, the authentication fails.
        Assert.ThrowsAny<Exception>(() => SshShellChannel.Open(trust1, "admin", "wrong", t).Dispose());
    }

    [SkippableFact]
    public void A_connection_is_refused_when_the_device_presents_another_key()
    {
        Skip.If(Simulator.Python is null, "requires Python with paramiko");
        using var sim = Simulator.Start("configure.py", ["ssh"], new Dictionary<string, string> { ["PORT_BASE"] = (Base + 10).ToString(System.Globalization.CultureInfo.InvariantCulture) }, waitPort: Base + 13, seconds: 60);
        var t = Translator.English;
        var store = new FingerprintStore(FingerprintsFile(sim));
        // Key of SW01 pinned, but connecting to SW03 (another key on another port): must fail before authenticating.
        var trust1 = Accept("127.0.0.1", Base + 10, store);
        var wrong = trust1 with { Port = Base + 12, Device = "127.0.0.1:" + (Base + 12).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var ex = Assert.Throws<SwitchException>(() => SshShellChannel.Open(wrong, "admin", "Pa55w0rd!", t).Dispose());
        Assert.Contains("do not continue without checking", ex.Message, StringComparison.Ordinal);

        // Saved key different from the presented one and the operator says no: refused, nothing replaced.
        store.Save("127.0.0.1:" + (Base + 12).ToString(System.Globalization.CultureInfo.InvariantCulture), "ssh-rsa", "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "SW03", "X", "test");
        Assert.Throws<FingerprintRejectedException>(() => Accept("127.0.0.1", Base + 12, store, answer: false));
        Assert.Contains(store.Read(), f => f.Fingerprint.StartsWith("SHA256:AAAA", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Scp_upload_to_flash()
    {
        Skip.IfNot(Simulator.CanRunPty, "requires Linux or WSL (pty) and Python");
        using var console = Simulator.Start("console_scp.py", [], new Dictionary<string, string> { ["PTYFILE"] = "{dir}/pty.txt", ["MODE"] = "xe" }, waitFile: "{dir}/pty.txt");
        using var bridge = Simulator.StartIn(console.Dir, "ssh_bridge.py", new Dictionary<string, string> { ["PTYFILE"] = console.Dir + "/pty.txt", ["PORT_SSH"] = "2324" }, waitPort: 2324, seconds: 40);
        var t = Translator.English;
        var store = new FingerprintStore(Path.Combine(console.Dir, "ssh_fingerprints.csv"));
        var trust = Accept("127.0.0.1", 2324, store);
        var local = Path.Combine(console.Dir, "image.bin");
        var data = Enumerable.Range(0, 600_000).Select(i => (byte)(i % 253)).ToArray();
        File.WriteAllBytes(local, data);
        var statuses = new List<string>();
        Scp.Upload(trust, "admin", "Pa55w0rd!", local, "cat9k_test.bin", t, status: statuses.Add);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(console.Dir, "flash_scp", "cat9k_test.bin")));
        Assert.Contains(statuses, s => s.Contains("waiting for the switch to finish writing", StringComparison.Ordinal));
    }
}
