using System.Globalization;
using Rackside.Core.Configure;
using Rackside.Core.Localization;
using Rackside.Core.Security;
using Rackside.Operations;
using Rackside.Operations.Locate;
using Rackside.Tests.Transport;

namespace Rackside.Tests.Operations;

/// <summary>
/// "Locate IP" against sim/locate.py: CORE (gateway) -> ACC1, ACC2 -> ACC3, each switch on its own loopback
/// address. See the topology at the top of the simulator.
/// </summary>
public sealed class LocateOperationTests
{
    private static readonly Credentials Cred = new("admin", "Pa55w0rd!", string.Empty);

    private static (LocateOperation Op, IReadOnlyList<ConfTarget> Switches) Setup(Simulator sim, int @base, Translator t, int acc2Port = -1, string without = "")
    {
        var inv = new Inventory();
        var csv = Path.Combine(sim.Dir, "switches.csv");
        var p = (int offset) => (@base + offset).ToString(CultureInfo.InvariantCulture);
        string[] rows = ["Name,IP,Port", $"CORE,127.0.0.11,{p(0)}", $"ACC1,127.0.0.12,{p(1)}",
            $"ACC2,127.0.0.13,{(acc2Port > 0 ? acc2Port.ToString(CultureInfo.InvariantCulture) : p(2))}", $"ACC3,127.0.0.14,{p(3)}", "NO-IP,,22"];
        File.WriteAllLines(csv, rows.Where(r => without.Length == 0 || !r.StartsWith(without + ",", StringComparison.Ordinal)));
        inv.Import(csv, t);
        var op = new LocateOperation(new LocatePaths(Path.Combine(sim.Dir, "Locate"), Path.Combine(sim.Dir, "ssh_fingerprints.csv")), t, new SecretMasker(), new FakeOperator());
        return (op, inv.AllWithIp());
    }

    private static Simulator Start(int @base, string arpMissing = "", string aged = "") =>
        Simulator.Start("locate.py", [], new Dictionary<string, string> { ["PORT_BASE"] = @base.ToString(CultureInfo.InvariantCulture), ["ARP_MISSING"] = arpMissing, ["AGED"] = aged },
            waitFile: "{dir}/ready.txt", seconds: 60);

    private static LocateResult Run(LocateOperation op, IReadOnlyList<ConfTarget> switches, string target, bool refresh = false) =>
        op.Locate(new LocateRequest { Target = target, RefreshArp = refresh, TrustNew = true }, switches, switches[0], Cred);

    [SkippableFact]
    public void Traces_ips_and_macs_switch_by_switch()
    {
        Skip.If(Simulator.Python is null, "requires Python with paramiko");
        const int Base = 2371;
        using var sim = Start(Base);
        var t = Translator.English;
        var (op, switches) = Setup(sim, Base, t);
        Assert.Equal(4, switches.Count);

        // Two hops: CORE -> ACC2 -> ACC3, Gi1/0/7.
        var r = Run(op, switches, "10.20.30.40");
        Assert.Equal(LocateOutcome.Found, r.Outcome);
        Assert.Equal(("ACC3", "Gi1/0/7", "20", "0050.5600.0040"), (r.Switch, r.Port, r.Vlan, r.Mac));
        Assert.Equal(["CORE", "ACC2", "ACC3"], r.Hops.Select(h => h.Switch));
        Assert.Contains("PC-Accounting", r.PortStatus, StringComparison.Ordinal);
        Assert.Empty(r.Notes);
        var text = File.ReadAllText(Path.Combine(r.Folder, "result.txt"));
        Assert.Contains("Switch: ACC3 (127.0.0.14)", text, StringComparison.Ordinal);
        Assert.Contains("MAC on Gi1/0/48, VLAN 20 -> behind it: ACC3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Pa55w0rd!", File.ReadAllText(Path.Combine(r.Folder, "log.txt")), StringComparison.Ordinal);

        // A MAC typed directly, behind an unmanaged switch (3 MACs on the port).
        r = Run(op, switches, "00-50-56-00-00-41");
        Assert.Equal(LocateOutcome.Found, r.Outcome);
        Assert.Equal(("ACC1", "Gi1/0/10", 3, "10.20.30.41"), (r.Switch, r.Port, r.MacsOnPort, r.Ip));
        Assert.Contains(r.Notes, n => n.StartsWith("3 MACs are learned on Gi1/0/10", StringComparison.Ordinal));

        // On the start switch itself.
        r = Run(op, switches, "10.20.30.50");
        Assert.Equal(("CORE", "Gi1/0/24", "30"), (r.Switch, r.Port, r.Vlan));
        Assert.Single(r.Hops);

        // Not in the ARP until it is pinged.
        r = Run(op, switches, "10.20.30.42");
        Assert.Equal(LocateOutcome.NotInArp, r.Outcome);
        Assert.Contains("is not in the ARP table of CORE", r.Detail, StringComparison.Ordinal);
        r = Run(op, switches, "10.20.30.42", refresh: true);
        Assert.Equal(("ACC2", "Gi1/0/5"), (r.Switch, r.Port));

        // The management address of a switch of the list.
        r = Run(op, switches, "127.0.0.14");
        Assert.Equal((LocateOutcome.IsSwitch, "ACC3"), (r.Outcome, r.Switch));

        Assert.Throws<FormatException>(() => Run(op, switches, "10.20.30"));

        // Read only: nothing but login, terminal, show and ping reached the switches.
        var commands = File.ReadAllLines(Path.Combine(sim.Dir, "configure.log"))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, "^\\S+ \\[(priv|user)/(\\S+)\\] '(.*)'$"))
            .Where(m => m.Success).ToList();
        Assert.NotEmpty(commands);
        Assert.All(commands, m =>
        {
            Assert.Equal("exec", m.Groups[2].Value);
            Assert.Matches("^(|terminal (length|width) 0|show .+|ping \\S+ repeat 2 timeout 1)$", m.Groups[3].Value);
        });
    }

    [SkippableFact]
    public void A_switch_that_cannot_be_queried_or_whose_mac_is_unknown()
    {
        Skip.If(Simulator.Python is null, "requires Python with paramiko");
        const int Base = 2381;
        using var sim = Start(Base, arpMissing: "ACC3");
        var t = Translator.English;

        // ACC2 does not answer: behind CORE Gi1/0/2 the trace goes on with ACC3.
        var (op, switches) = Setup(sim, Base, t, acc2Port: Base + 9);
        var r = Run(op, switches, "10.20.30.40", refresh: true);
        Assert.Equal(("ACC3", "Gi1/0/7"), (r.Switch, r.Port));
        Assert.Contains(r.Notes, n => n.StartsWith("ACC2: Could not be queried", StringComparison.Ordinal));

        // ACC3 missing from the ARP and no refresh: the trail ends on the ACC2 trunk toward it, and it does not say "found".
        using var sim2 = Start(Base + 10, arpMissing: "ACC3");
        (op, switches) = Setup(sim2, Base + 10, t);
        r = Run(op, switches, "10.20.30.40");
        Assert.Equal((LocateOutcome.BehindTrunk, "ACC2", "Gi1/0/48"), (r.Outcome, r.Switch, r.Port));
        Assert.Contains("no switch of the list is learned on that port", r.Detail, StringComparison.Ordinal);
        Assert.Contains(r.Notes, n => n.StartsWith("Management MAC unknown for ACC3", StringComparison.Ordinal));

        // With the refresh the ACC3 MAC is learned and the trace reaches the real port.
        r = Run(op, switches, "10.20.30.40", refresh: true);
        Assert.Equal(("ACC3", "Gi1/0/7"), (r.Switch, r.Port));
    }

    [SkippableFact]
    public void A_switch_mac_that_aged_out_is_learned_again_with_a_ping()
    {
        Skip.If(Simulator.Python is null, "requires Python with paramiko");
        const int Base = 2401;
        using var sim = Start(Base, aged: "ACC3");
        var t = Translator.English;

        // ACC2 no longer has the ACC3 MAC: its trunk Gi1/0/48 looks like the end, so it pings the switches missing
        // from its table, learns ACC3 again and goes on.
        var (op, switches) = Setup(sim, Base, t);
        var r = Run(op, switches, "10.20.30.40");
        Assert.Equal((LocateOutcome.Found, "ACC3", "Gi1/0/7"), (r.Outcome, r.Switch, r.Port));
        Assert.Equal(["CORE", "ACC2", "ACC3"], r.Hops.Select(h => h.Switch));
        Assert.Contains("ACC2: Gi1/0/48 looks like an uplink", File.ReadAllText(Path.Combine(r.Folder, "log.txt")), StringComparison.Ordinal);

        // A switch that is not in the list: the trail ends on the trunk toward it.
        (op, switches) = Setup(sim, Base, t, without: "ACC3");
        r = Run(op, switches, "10.20.30.40");
        Assert.Equal((LocateOutcome.BehindTrunk, "ACC2", "Gi1/0/48"), (r.Outcome, r.Switch, r.Port));
        Assert.Contains("trunk", r.PortStatus, StringComparison.Ordinal);
        Assert.Contains(", trunk)", r.Detail, StringComparison.Ordinal);
    }
}
