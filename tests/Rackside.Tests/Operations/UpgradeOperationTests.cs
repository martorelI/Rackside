using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Rackside.Core.Localization;
using Rackside.Core.Security;
using Rackside.Core.Upgrade;
using Rackside.Operations.Upgrade;
using Rackside.Tests.Transport;
using Rackside.Transport.Console;

namespace Rackside.Tests.Operations;

/// <summary>
/// Update IOS by USB, Xmodem and SCP (IOS-XE and classic IOS) against the simulators of tests/sim
/// (SSH ports 2325 and 2335-2339).
/// </summary>
[Trait("Category", "Slow")]
public sealed class UpgradeOperationTests
{
    private static string FakeImage(string path, int bytes)
    {
        var b = new byte[bytes];
        new Random(2026).NextBytes(b);
        File.WriteAllBytes(path, b);
        return Convert.ToHexStringLower(MD5.HashData(b));
    }

    private static UpgradeOperation Op(Simulator sim, Translator t, FakeOperator ui, bool pty = true) =>
        new(Path.Combine(sim.Dir, "ssh_fingerprints.csv"), t, new SecretMasker(), ui, pty ? (p, b) => SerialChannel.Open(p, b, t, pty: true) : null) { RetrySeconds = 1 };

    private static string Log(UpgradeSession ses) => File.ReadAllText(Path.Combine(ses.Folder, "log.txt"));

    [SkippableFact]
    public void Usb_on_a_9200L_in_install_mode()
    {
        Skip.IfNot(Simulator.CanRunPty, "requires Linux or WSL (pty) and Python");
        using var sim = Simulator.Start("console_upgrade.py", [], new Dictionary<string, string> { ["PTYFILE"] = "{dir}/pty.txt", ["MODE"] = "xe", ["USBTAR"] = "cat9k_lite_iosxe.17.09.05.SPA.bin" }, waitFile: "{dir}/pty.txt");
        var t = Translator.English;
        var ui = new FakeOperator { QuestionAnswer = true };
        var op = Op(sim, t, ui);
        var opt = new UpgradeOptions
        {
            Connection = "SERIAL", PortCom = sim.ReadFile("pty.txt"), User = "admin", Password = "x", FolderRecord = Path.Combine(sim.Dir, "upd"), Source = "USB",
            SaveBefore = true, Reload = true, CleanInactive = true,
        };
        var ses = op.Query(opt);
        var image = Assert.Single(ses.Info!.Usb!.Entries, e => e.Name.EndsWith(".bin", StringComparison.Ordinal));
        opt = opt with { FileUsb = image.Name, Md5Expected = Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("TARX", 2000))))) };
        var r = op.Update(ses, opt);
        Assert.NotNull(r);
        Assert.True(File.Exists(r.RecordPath), "update record");
        Assert.True(r.Outcome == UpgradeOutcome.Successful, r.Outcome + ": " + r.ErrorFatal + "\n" + Log(ses));
        Assert.Contains("===== Result: SUCCESSFUL =====", Log(ses), StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Xmodem_from_the_boot_loader_of_a_2960X()
    {
        Skip.IfNot(Simulator.CanRunPty, "requires Linux or WSL (pty) and Python");
        using var sim = Simulator.Start("console_xmodem_2960.py", [], new Dictionary<string, string> { ["PTYFILE"] = "{dir}/pty.txt" }, waitFile: "{dir}/pty.txt");
        var t = Translator.Spanish;
        var op = Op(sim, t, new FakeOperator { QuestionAnswer = true });
        var image = Path.Combine(sim.Dir, "c2960x-universalk9-mz.152-7.E10.bin");
        var md5 = FakeImage(image, 300000);
        var opt = new UpgradeOptions
        {
            Connection = "SERIAL", PortCom = sim.ReadFile("pty.txt"), User = "admin", Password = "x", FolderRecord = Path.Combine(sim.Dir, "upd"), Ticket = "CHG-2026-0112",
            Source = "SERIAL", FileLocal = image, Md5Expected = md5, SaveBefore = true, Reload = true, VelXmodem = 115200,
        };
        var ses = op.Query(opt);
        var r = op.Update(ses, opt);
        Assert.NotNull(r);
        Assert.True(File.Exists(r.RecordPath), "update record");
        Assert.True(r.Outcome == UpgradeOutcome.Successful, r.Outcome + ": " + r.ErrorFatal + "\n" + Log(ses));
        Assert.Equal("15.2(7)E10", r.VersionAfter);
    }

    private static (Simulator Console, Simulator Bridge) ScpSimulators(int port, string mode, IDictionary<string, string>? extra = null, Action<string>? prepare = null)
    {
        var dir = Path.Combine(RepoPaths.Root, "tests", "out", "dotnet", "upgrade_scp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "flash_scp"));
        prepare?.Invoke(Path.Combine(dir, "flash_scp"));
        var env = new Dictionary<string, string> { ["PTYFILE"] = dir + "/pty.txt", ["MODE"] = mode };
        foreach (var (k, v) in extra ?? new Dictionary<string, string>())
        {
            env[k] = v;
        }
        var console = Simulator.StartIn(dir, "console_scp.py", env, waitFile: dir + "/pty.txt");
        var benv = new Dictionary<string, string>(env) { ["PORT_SSH"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        return (console, Simulator.StartIn(dir, "ssh_bridge.py", benv, waitPort: port, seconds: 40));
    }

    private static UpgradeOptions Ssh(Simulator sim, int port, string image, string md5) => new()
    {
        Connection = "SSH", IP = "127.0.0.1", PortSsh = port, User = "admin", Password = "Pa55w0rd!", FolderRecord = Path.Combine(sim.Dir, "upd"),
        Source = "SSH", FileLocal = image, Md5Expected = md5, SaveBefore = true,
    };

    [SkippableFact]
    public void Scp_on_a_9200L()
    {
        Skip.IfNot(Simulator.CanRunPty, "requires Linux or WSL (pty) and Python");
        var (console, bridge) = ScpSimulators(2325, "xe");
        using (console)
        using (bridge)
        {
            var image = Path.Combine(console.Dir, "cat9k_lite_iosxe.17.09.05.SPA.bin");
            var md5 = FakeImage(image, 9000000);
            var ui = new FakeOperator { QuestionAnswer = true };
            var op = Op(console, Translator.English, ui, pty: false);
            var opt = Ssh(console, 2325, image, md5) with { CleanInactive = true, Reload = true };
            var ses = op.Query(opt);
            var r = op.Update(ses, opt);
            Assert.NotNull(r);
        Assert.True(File.Exists(r.RecordPath), "update record");
            Assert.True(r.Outcome == UpgradeOutcome.Successful, r.Outcome + ": " + r.ErrorFatal + "\n" + Log(ses));
            Assert.True(File.Exists(Path.Combine(console.Dir, "ssh_fingerprints.csv")));
            Assert.Contains(ui.Warnings, w => w.Title == "Unverified SSH fingerprint");
            foreach (var f in Directory.GetFiles(Path.Combine(console.Dir, "upd"), "*.txt", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain("Pa55w0rd!", File.ReadAllText(f), StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("stale", 2335)]
    [InlineData("reuse_same", 2336)]
    [InlineData("reuse_different", 2337)]
    [InlineData("running_same", 2338)]
    [InlineData("running_different", 2339)]
    public void Scp_on_a_2960X_without_reload(string scenario, int port)
    {
        Skip.IfNot(Simulator.CanRunPty, "requires Linux or WSL (pty) and Python");
        const string name = "c2960x-universalk9-mz.152-7.E14.bin";
        var tmp = Path.Combine(Path.GetTempPath(), "rackside_img_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var image = Path.Combine(tmp, name);
        var md5 = FakeImage(image, 3000000);
        Action<string>? prepare = scenario switch
        {
            "reuse_same" or "running_same" => f => File.Copy(image, Path.Combine(f, name)),
            "reuse_different" or "running_different" => f => File.WriteAllBytes(Path.Combine(f, name), new byte[3000000]),
            _ => null,
        };
        var extra = scenario switch
        {
            "stale" => new Dictionary<string, string> { ["STALE_AFTER_SCP"] = "1" },
            // The running image is the new .bin itself (loose in the flash root)
            "running_same" or "running_different" => new Dictionary<string, string> { ["RUNNING_BIN"] = name },
            _ => null,
        };
        var (console, bridge) = ScpSimulators(port, "classic", extra, prepare);
        using (console)
        using (bridge)
        {
            var op = Op(console, Translator.English, new FakeOperator { QuestionAnswer = true }, pty: false);
            var opt = Ssh(console, port, image, md5);
            if (scenario.StartsWith("running_", StringComparison.Ordinal))
            {
                // "Also the running image" checked: the only way to leave flash clean
                opt = opt with { CleanFlash = true, CleanAll = true };
            }
            var ses = op.Query(opt);
            var r = op.Update(ses, opt);
            Assert.NotNull(r);
        Assert.True(File.Exists(r.RecordPath), "update record");
            Assert.True(r.Outcome is UpgradeOutcome.Successful or UpgradeOutcome.SuccessfulWithNotes, r.Outcome + ": " + r.ErrorFatal + "\n" + Log(ses));
            var br = File.ReadAllText(Path.Combine(console.Dir, "ssh_bridge.log"));
            var cl = File.ReadAllText(Path.Combine(console.Dir, "console_scp.log"));
            var log = Log(ses);
            switch (scenario)
            {
                case "stale":
                    Assert.Contains("RECEIVED " + name, br, StringComparison.Ordinal);
                    Assert.Contains("The control session is closed during the transfer and reopened afterwards.", log, StringComparison.Ordinal);
                    break;
                case "reuse_same":
                    Assert.DoesNotContain("RECEIVED", br, StringComparison.Ordinal);
                    Assert.Contains($"flash:{name} already has the expected MD5 ({md5}): it is neither deleted nor copied again.", log, StringComparison.Ordinal);
                    Assert.Single(System.Text.RegularExpressions.Regex.Matches(cl, "VERIFY "));
                    break;
                case "running_same":
                    Assert.DoesNotContain("THE RUNNING IMAGE WAS DELETED", cl, StringComparison.Ordinal);
                    Assert.DoesNotContain("RECEIVED", br, StringComparison.Ordinal);
                    Assert.Contains($"Not needed: flash:{name} is already the new image (same MD5).", log, StringComparison.Ordinal);
                    break;
                default:
                    Assert.Contains("DELETED " + name, cl, StringComparison.Ordinal);
                    Assert.Contains("RECEIVED " + name, br, StringComparison.Ordinal);
                    break;
            }
        }
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void A_stuck_verify_stops_after_the_stall_time()
    {
        var ch = new FakeChannel { Echo = true, OnLine = _ => new string('.', 200) };
        var s = new ConsoleSession(ch, Translator.English, new SecretMasker());
        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<SwitchException>(() => s.RunLong("verify /md5 flash:x.bin", 1800, stallSeconds: 3));
        Assert.StartsWith("No data from the switch for 3 s: the session seems stuck.", ex.Message, StringComparison.Ordinal);
        Assert.InRange(clock.Elapsed.TotalSeconds, 2.5, 20);
    }
}
