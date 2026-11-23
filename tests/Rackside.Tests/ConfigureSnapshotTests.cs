using Rackside.Core.Configure;
using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Core.Restore;

namespace Rackside.Tests;

/// <summary>tests/snapshots/configure: switch document, push verification and inventory import.</summary>
public class ConfigureSnapshotTests
{
    private static string Fixture(string name) => File.ReadAllText(RepoPaths.Combine("tests", "fixtures", "configure", name));

    private static readonly TargetInfo Version = new()
    {
        OS = "IOS", Model = "WS-C2960X-48FPD-L", Version = "15.2(7)E3", Serial = "FOC1111X0AA", Interfaces = [], Dir = [], Clean = true, Remains = string.Empty,
    };

    [Fact]
    public void Core_reproduces_the_switch_document()
    {
        var d = SwitchDocument.Build(Version, Fixture("show_lldp.txt"), Fixture("show_cdp.txt"), Fixture("show_vtp.txt"), Fixture("show_stp.txt"),
            Fixture("running_after.txt"), Fixture("show_brief.txt"), "LAB-SW01", Translator.English);
        var text = string.Concat(new (string, string)[]
        {
            ("Hostname", d.Hostname), ("Model", d.Model), ("Platform", d.Platform), ("Version", d.Version), ("Serial", d.Serial), ("LLDP", d.LLDP), ("CDP", d.CDP),
            ("VTP", d.VTP), ("STP", d.STP), ("Domain", d.Domain), ("SNMP", d.SNMP), ("NTP", d.NTP), ("Syslog", d.Syslog), ("Banner", d.Banner), ("SSH", d.SSH),
            ("IPManagement", d.IPManagement),
        }.Select(p => p.Item1 + " = " + p.Item2 + "\n"));
        Snapshot.Match(text, "configure", "document.txt");
    }

    [Fact]
    public void Core_reproduces_the_push_verification()
    {
        var p = PushScenarios.Parameters();
        var masker = PushScenarios.Masker(p);
        var t = Translator.English;
        var running = File.ReadAllText(RepoPaths.Combine("tests", "fixtures", "running", "push_running.txt"));
        var items = PushPlanner.Lines(p, "IOS", running, t, masker);
        var shows = new Dictionary<string, string>
        {
            ["show lldp"] = Fixture("show_lldp.txt"), ["show cdp"] = Fixture("show_cdp.txt"), ["show vtp status"] = Fixture("show_vtp.txt"),
            ["show snmp user"] = Fixture("show_snmp_user.txt"), ["show spanning-tree summary | include mode"] = Fixture("show_stp.txt"),
        };
        (string, string)[] errors = [("udld enable", "% Invalid input detected at '^' marker."), ("interface GigabitEthernet1/0/99", "% Invalid interface")];
        var checks = PushVerifier.Verify(items, errors, Fixture("running_after.txt"), shows, t, masker);
        Snapshot.Match(string.Concat(checks.Select(c => $"{c.Check} | {c.Result} | {c.Detail}\n")), "configure", "verification.txt");
    }

    [Fact]
    public void Core_reproduces_the_inventory_import()
    {
        var inv = new Inventory();
        var r = inv.Import(RepoPaths.Combine("tests", "fixtures", "configure", "inventory_import.csv"), Translator.English);
        var path = Path.Combine(Path.GetTempPath(), "rackside_inv_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            inv.Save(path);
            var saved = File.ReadAllText(path).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
            Snapshot.Match($"added={r.Added} updated={r.Updated} skipped={r.Skipped}\n" + saved, "configure", "inventory.csv");
            Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Targets_are_validated()
    {
        var inv = new Inventory();
        inv.Import(RepoPaths.Combine("tests", "fixtures", "configure", "inventory_import.csv"), Translator.English);
        Assert.Throws<FormatException>(() => inv.Targets(true, "COM3", Translator.English));
        inv.Rows.ForEach(f => f.Selected = true);
        inv.Rows[1]["Port"] = "99999";
        var ex = Assert.Throws<FormatException>(() => inv.Targets(true, "COM3", Translator.English));
        Assert.Contains("SW-B: invalid SSH port \"99999\"", ex.Message, StringComparison.Ordinal);
        inv.Rows[1]["Port"] = "2222";
        Assert.Equal(["SW-A renamed", "SW-B", "10.0.0.13"], inv.Targets(true, "COM3", Translator.English).Select(x => x.Name));
        var console = Assert.Single(inv.Targets(false, "COM3", Translator.English));
        Assert.Null(console.Row);
        Assert.Equal("Console COM3", console.Name);
    }
}
