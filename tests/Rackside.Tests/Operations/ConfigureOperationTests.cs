using System.Globalization;
using System.Text.Json;
using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Core.Security;
using Rackside.Operations;
using Rackside.Operations.Configure;
using Rackside.Tests.Transport;
using Rackside.Transport.Ssh;

namespace Rackside.Tests.Operations;

/// <summary>
/// Query and push over SSH against sim/configure.py:
/// SW01 IOS (2361), SW02 IOS-XE (2362), SW03 with a fingerprint different from the saved one (2363), a non-Cisco
/// HP (2364), a port with no server (2365) and an invalid IP.
/// </summary>
public sealed class ConfigureOperationTests
{
    private const int Base = 2361;
    private static readonly string[] Secrets = ["Pa55w0rd!", "En4ble", "Supp0rt!2026", "NewEn4ble!", "MonitorRO2026", "VtpPass123", "AuthPass123", "PrivPass123", "Other2026RO"];

    private static string P(int offset) => (Base + offset).ToString(CultureInfo.InvariantCulture);

    private static JsonElement State(Simulator sim, string host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return JsonDocument.Parse(sim.ReadFile("state.json")).RootElement.GetProperty(host).Clone();
            }
            catch (Exception ex) when ((ex is IOException or JsonException or KeyNotFoundException) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static string Evidence(string folder) => string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Where(f => (f.EndsWith(".txt", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal)) && Path.GetFileName(f) != "running_before.txt")
        .Select(File.ReadAllText));

    [SkippableFact]
    public void Query_and_push_sweep_over_ssh()
    {
        Skip.If(Simulator.Python is null, "requires Python with paramiko");
        using var sim = Simulator.Start("configure.py", ["ssh"], new Dictionary<string, string> { ["PORT_BASE"] = P(0) }, waitPort: Base + 3, seconds: 60);
        var t = Translator.English;
        var paths = new ConfigurePaths(Path.Combine(sim.Dir, "Inventory"), Path.Combine(sim.Dir, "Inventory", "switches.csv"), Path.Combine(sim.Dir, "ssh_fingerprints.csv"));
        var ui = new FakeOperator();
        ui.WarningAnswer["does not match"] = false;
        var op = new ConfigureOperation(paths, t, new SecretMasker(), ui);

        var csv = Path.Combine(sim.Dir, "devices.csv");
        File.WriteAllLines(csv, ["Switch;Direccion IP;Puerto", $"SW01;127.0.0.1;{P(0)}", $"SW02;127.0.0.1;{P(1)}", $"SW03;127.0.0.1;{P(2)}", $"HP-IDF4;127.0.0.1;{P(3)}", $"SW05;127.0.0.1;{P(4)}", "BAD;300.1.1.1;22"]);
        Assert.Equal(6, op.Inventory.Import(csv, t).Added);
        new FingerprintStore(paths.Fingerprints).Save("127.0.0.1:" + P(2), "ssh-rsa", "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "SW03", "X", "test");
        op.Inventory.Rows.ForEach(f => f.Selected = true);
        var cred = new Credentials("admin", "Pa55w0rd!", "En4ble");
        var opt = new ConfigureOptions { TrustNew = true, Save = true, Continue = true, FolderRecord = Path.Combine(sim.Dir, "Pushes"), Ticket = "CHG-TEST-1", Responsible = "operator" };
        var ex = Assert.Throws<FormatException>(() => op.Query(opt, cred));
        Assert.Contains("300.1.1.1", ex.Message, StringComparison.Ordinal);
        op.Inventory.Rows.Single(f => f["Name"] == "BAD").Selected = false;

        // Query: documents, changes nothing.
        Assert.Equal((2, 3), op.Query(opt, cred));
        var row = (string name) => op.Inventory.Rows.Single(f => f["Name"] == name);
        Assert.Equal("IOS", row("SW01")["Platform"]);
        Assert.Equal("IOS-XE", row("SW02")["Platform"]);
        Assert.StartsWith("SKIPPED", row("SW03")["Status"], StringComparison.Ordinal);
        Assert.StartsWith("FAILED", row("HP-IDF4")["Status"], StringComparison.Ordinal);
        Assert.StartsWith("FAILED", row("SW05")["Status"], StringComparison.Ordinal);
        Assert.Contains(new FingerprintStore(paths.Fingerprints).Read(), f => f.Device == "127.0.0.1:" + P(2) && f.Fingerprint.StartsWith("SHA256:AAAA", StringComparison.Ordinal));
        Assert.True(File.Exists(paths.InventoryCsv));

        // Push: every group, with extra commands that must be blocked.
        var prepared = op.PreparePush(PushScenarios.Parameters(), opt with { TrustNew = false });
        Assert.True(prepared.ConfirmationIsWarning);
        Assert.DoesNotContain(Secrets, s => prepared.Preview.Contains(s, StringComparison.Ordinal));
        var run = op.Push(prepared, cred);
        var by = run.Results.ToDictionary(r => r.Name);
        foreach (var n in new[] { "SW01", "SW02" })
        {
            Assert.True(by[n].Result is PushOutcome.Successful or PushOutcome.SuccessfulWithNotes, n + ": " + by[n].Result + " " + by[n].Detail);
            var s = State(sim, n);
            Assert.True(s.GetProperty("saved").GetBoolean() && s.GetProperty("lldp").GetBoolean() && s.GetProperty("stp").GetString() == "rapid-pvst", n);
            Assert.Contains("SUCCESSFUL", row(n)["Status"], StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(by[n].Folder, "running_before.txt")));
        }
        Assert.Equal(PushOutcome.Skipped, by["SW03"].Result);
        Assert.False(State(sim, "SW03").GetProperty("saved").GetBoolean());
        Assert.Equal(PushOutcome.Failed, by["HP-IDF4"].Result);
        Assert.Equal(PushOutcome.Failed, by["SW05"].Result);
        Assert.StartsWith("FAILED: 2, SKIPPED: 1, SUCCESSFUL", run.Summary, StringComparison.Ordinal);
        var sent = File.ReadAllText(Path.Combine(by["SW01"].Folder, "sent_lines.txt"));
        Assert.DoesNotContain("hostname OTHER", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("do reload", sent, StringComparison.Ordinal);
        Assert.DoesNotContain(Secrets, s => Evidence(run.Folder).Contains(s, StringComparison.Ordinal));
        Assert.Contains("Switch user: admin", File.ReadAllText(Path.Combine(run.Folder, "log.txt")), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(run.Folder, "push_report.html"), run.RecordPath);
        Assert.Contains("===== Push finished: ", File.ReadAllText(Path.Combine(run.Folder, "log.txt")), StringComparison.Ordinal);
        Assert.Contains("SW03", File.ReadAllText(run.RecordPath), StringComparison.Ordinal);
    }

    [SkippableFact]
    public void A_rejected_line_leaves_the_switch_with_failures_and_unsaved_in_spanish()
    {
        Skip.If(Simulator.Python is null, "requires Python with paramiko");
        const int Base2 = 2366;
        using var sim = Simulator.Start("configure.py", ["ssh"], new Dictionary<string, string> { ["PORT_BASE"] = Base2.ToString(CultureInfo.InvariantCulture) }, waitPort: Base2 + 3, seconds: 60);
        var t = Translator.Spanish;
        var paths = new ConfigurePaths(Path.Combine(sim.Dir, "Inventory"), Path.Combine(sim.Dir, "Inventory", "switches.csv"), Path.Combine(sim.Dir, "ssh_fingerprints.csv"));
        var op = new ConfigureOperation(paths, t, new SecretMasker(), new FakeOperator());
        var row = new Rackside.Core.Configure.InventoryRow { Selected = true };
        row["Name"] = "SW01";
        row["IP"] = "127.0.0.1";
        row["Port"] = Base2.ToString(CultureInfo.InvariantCulture);
        op.Inventory.Rows.Add(row);
        var opt = new ConfigureOptions { TrustNew = true, Save = true, FolderRecord = Path.Combine(sim.Dir, "Pushes") };
        var cred = new Credentials("admin", "Pa55w0rd!", "En4ble");
        Assert.Equal((1, 0), op.Query(opt, cred));
        Assert.Equal("WS-C2960X-48FPD-L", row["Model"]);

        var p = new PushParameters
        {
            Lldp = { Apply = true, Lldp = "Enable", Cdp = "Disable" },
            Snmp = { Apply = true, Location = "IDF9", Community = "Other2026RO" },
            Extra = { Apply = true, Text = "mls qos\r\nerrdisable recovery cause all" },
            Ssh = { Apply = true, Retries = "3", VtyTimeout = "10" },
        };
        var run = op.Push(op.PreparePush(p, opt), cred);
        var r = Assert.Single(run.Results);
        Assert.Equal(PushOutcome.WithFailures, r.Result);
        Assert.Contains(t.T("WITH FAILURES"), row["Status"], StringComparison.Ordinal);
        var s = State(sim, "SW01");
        Assert.False(s.GetProperty("saved").GetBoolean());
        Assert.True(s.GetProperty("lldp").GetBoolean() && !s.GetProperty("cdp").GetBoolean());
        Assert.DoesNotContain(Secrets, x => Evidence(run.Folder).Contains(x, StringComparison.Ordinal));
    }
}
