using Rackside.Core.Localization;
using Rackside.Core.Restore;
using Rackside.Core.Security;

namespace Rackside.Tests;

/// <summary>Adversarial cases for the restore block list and allow list (security rule 1: allow list and block list).</summary>
public class SecurityFilterTests
{
    [Theory]
    [InlineData("do reload")]
    [InlineData("DO RELOAD")]
    [InlineData("  do   write erase")]
    [InlineData("reload in 5")]
    [InlineData("write memory")]
    [InlineData("copy tftp: running-config")]
    [InlineData("event manager applet X")]
    [InlineData("no event manager applet X")]
    [InlineData("kron policy-list nightly")]
    [InlineData("alias exec x reload")]
    [InlineData("tclsh")]
    [InlineData("macro name evil")]
    [InlineData("privilege exec level 0 reload")]
    [InlineData("boot system flash:x.bin")]
    [InlineData("config-register 0x2142")]
    [InlineData("service internal")]
    [InlineData("service config")]
    [InlineData("snmp-server system-shutdown")]
    [InlineData("tftp-server flash:x")]
    [InlineData("crypto key generate rsa modulus 2048")]
    [InlineData("crypto key zeroize rsa")]
    [InlineData("vstack")]
    [InlineData("end")]
    [InlineData("license smart register idtoken X")]
    public void Dangerous_top_level_lines_are_blocked(string line) =>
        Assert.NotEqual(string.Empty, SecurityFilter.BlockedReason(line));

    [Theory]
    [InlineData("no vstack")]
    [InlineData("no service config")]
    [InlineData("NO SERVICE INTERNAL")]
    [InlineData("macro description uplink")]
    [InlineData("privilege level 15")]
    [InlineData("endpoint tracking")]
    [InlineData("hostname SW01")]
    public void Harmless_lines_are_not_blocked(string line) =>
        Assert.Equal(string.Empty, SecurityFilter.BlockedReason(line));

    [Theory]
    [InlineData(" username evil privilege 15 secret x", "interface GigabitEthernet1/0/1")]
    [InlineData(" hostname EVIL", "line vty 0 4")]
    [InlineData(" enable secret 0 x", "interface Vlan1")]
    [InlineData(" aaa new-model", "line con 0")]
    [InlineData(" snmp-server community public RW", "interface Vlan1")]
    [InlineData(" ip ssh version 1", "line vty 0 4")]
    [InlineData(" ip http server", "interface Vlan1")]
    [InlineData(" crypto pki trustpoint X", "interface Vlan1")]
    [InlineData(" interface GigabitEthernet1/0/2", "interface GigabitEthernet1/0/1")]
    [InlineData(" no username evil", "interface Vlan1")]
    [InlineData(" do reload", "interface Vlan1")]
    public void Global_commands_hidden_as_sublines_are_blocked(string subline, string header) =>
        Assert.NotEqual(string.Empty, SecurityFilter.BlockedReason(subline, subline: true, header: header));

    [Fact]
    public void Username_is_allowed_inside_the_ssh_pubkey_chain() =>
        Assert.Equal(string.Empty, SecurityFilter.BlockedReason("  username admin", subline: true, header: "ip ssh pubkey-chain"));

    [Theory]
    [InlineData("foo bar")]
    [InlineData("archive")]
    [InlineData("menu x title y")]
    public void Unknown_top_level_commands_are_not_allowed(string header) =>
        Assert.False(SecurityFilter.IsAllowed(header) && SecurityFilter.BlockedReason(header).Length == 0);

    [Fact]
    public void Extra_allowed_commands_never_skip_the_block_list()
    {
        Assert.True(SecurityFilter.IsAllowed("device-sensor filter-list x", ["^device-sensor\\s"]));
        var (blocks, report) = Filter("event manager applet X\n action 1 cli command reload", ["^event\\s"]);
        Assert.Empty(blocks);
        Assert.Contains(report.Items, i => i.Type == ReportType.Blocked && i.Line.StartsWith("event manager", StringComparison.Ordinal));
    }

    [Fact]
    public void Control_characters_block_the_whole_block()
    {
        var (blocks, report) = Filter("interface Vlan1\n description ok\u001a\n ip address 10.0.0.1 255.255.255.0");
        Assert.DoesNotContain(blocks, b => b.Header == "interface Vlan1");
        Assert.Contains(report.Items, i => i.Type == ReportType.Blocked && i.Line == "interface Vlan1");
    }

    [Fact]
    public void Tabs_are_sent_as_spaces()
    {
        var (blocks, _) = Filter("interface Vlan1\n\tdescription a\tb");
        Assert.Equal(" description a b", Assert.Single(blocks, b => b.Header == "interface Vlan1").Children[0]);
    }

    [Fact]
    public void Banner_using_every_delimiter_is_blocked()
    {
        var (blocks, report) = Filter("banner motd ^C\n# $ % ~ @ & | ^ *\n^C");
        Assert.DoesNotContain(blocks, b => b.IsBanner);
        Assert.Contains(report.Items, i => i.Type == ReportType.Blocked && i.Line == "banner motd");
    }

    [Fact]
    public void Banner_with_another_delimiter_is_blocked()
    {
        var (blocks, report) = Filter("banner motd #\nHello\n#");
        Assert.DoesNotContain(blocks, b => b.Header.StartsWith("banner", StringComparison.Ordinal));
        Assert.Contains(report.Items, i => i.Type == ReportType.Blocked && i.Line.StartsWith("banner motd #", StringComparison.Ordinal));
    }

    [Fact]
    public void Certificate_chain_with_non_hex_lines_is_blocked()
    {
        var (blocks, report) = Filter("crypto pki certificate chain TP-X\n certificate self-signed 01\n  3082 0229\n  do reload\n  quit");
        Assert.Empty(blocks);
        Assert.Contains(report.Items, i => i.Type == ReportType.Blocked && i.Reason.Contains("do reload", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocked_sublines_are_removed_and_the_rest_of_the_block_stays()
    {
        var (blocks, report) = Filter("interface GigabitEthernet1/0/1\n description user\n username evil privilege 15 secret x\n switchport mode access");
        var b = Assert.Single(blocks, x => x.Header == "interface GigabitEthernet1/0/1");
        Assert.Equal([" description user", " switchport mode access"], b.Children);
        Assert.Single(report.Items, i => i.Type == ReportType.Blocked);
    }

    private static (List<ConfigBlock> Blocks, PlanReport Report) Filter(string body, IEnumerable<string>? extra = null)
    {
        var src = SourceConfig.Parse("hostname SW\n!\n" + body + "\n!\nend\n");
        var report = new PlanReport(Translator.English, new SecretMasker());
        var blocks = SecurityFilter.Apply(src.Blocks.Where(b => b.Header != "hostname SW").Select(b => b.Clone()), report, extra);
        return (blocks, report);
    }
}
