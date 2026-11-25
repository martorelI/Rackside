using Rackside.Core.Locate;
using Rackside.Operations.Locate;

namespace Rackside.Tests;

/// <summary>"Locate IP": parsing of the switch outputs and the hop decision.</summary>
public class LocateTests
{
    [Theory]
    [InlineData("00:50:56:AB:cd:01", "0050.56ab.cd01")]
    [InlineData("00-50-56-ab-cd-01", "0050.56ab.cd01")]
    [InlineData("0050.56AB.CD01", "0050.56ab.cd01")]
    [InlineData(" 005056abcd01 ", "0050.56ab.cd01")]
    public void Mac_addresses_are_normalized(string text, string expected)
    {
        Assert.True(MacAddress.TryNormalize(text, out var mac));
        Assert.Equal(expected, mac);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("0050.56ab.cd0")]
    [InlineData("00:50:56:ab:cd")]
    [InlineData("0050-56ab-cd01")]
    [InlineData("")]
    public void Invalid_mac_addresses_are_rejected(string text) => Assert.False(MacAddress.TryNormalize(text, out _));

    [Fact]
    public void Arp_skips_incomplete_entries()
    {
        const string text = """
            Protocol  Address          Age (min)  Hardware Addr   Type   Interface
            Internet  10.1.1.1                -   00aa.0000.0011  ARPA   Vlan1
            Internet  10.20.30.40            12   0050.5600.0040  ARPA   Vlan20
            Internet  10.20.30.99             0   Incomplete      ARPA
            """;
        var arp = LocateParser.Arp(text);
        Assert.Equal(2, arp.Count);
        Assert.Equal(new ArpEntry("10.20.30.40", "0050.5600.0040", "Vlan20"), arp[1]);
        Assert.Equal("20", LocateParser.VlanOf(arp[1].Interface));
        Assert.Equal(string.Empty, LocateParser.VlanOf("GigabitEthernet1/0/1"));
    }

    [Fact]
    public void Mac_table_keeps_only_entries_on_one_port()
    {
        const string text = """
                      Mac Address Table
            -------------------------------------------

            Vlan    Mac Address       Type        Ports
            ----    -----------       --------    -----
             All    0100.0ccc.cccc    STATIC      CPU
               1    00AA.0000.0012    DYNAMIC     Gi1/0/1
              20    0050.5600.0040    DYNAMIC     Po1
              30    0050.5600.0050    STATIC      Gi1/0/24
               1    0001.0001.0001    STATIC      Gi1/0/1,Gi1/0/2
            Total Mac Addresses for this criterion: 5
            """;
        var table = LocateParser.MacTable(text);
        Assert.Equal(3, table.Count);
        Assert.Equal(new MacEntry("1", "00aa.0000.0012", "DYNAMIC", "Gi1/0/1"), table[0]);
        Assert.Equal("Po1", table[1].Port);
        Assert.Equal("STATIC", table[2].Type);
    }

    [Fact]
    public void Rejected_commands_are_detected()
    {
        Assert.True(LocateParser.IsError("show mac address-table\n                ^\n% Invalid input detected at '^' marker."));
        Assert.False(LocateParser.IsError("Vlan    Mac Address       Type        Ports"));
    }

    private static readonly IReadOnlyList<MacEntry> Core =
    [
        new("1", "00aa.0000.0012", "DYNAMIC", "Gi1/0/1"),
        new("1", "00aa.0000.0013", "DYNAMIC", "Gi1/0/2"),
        new("1", "00aa.0000.0014", "DYNAMIC", "Gi1/0/2"),
        new("20", "0050.5600.0040", "DYNAMIC", "Gi1/0/2"),
        new("20", "0050.5600.0041", "DYNAMIC", "Gi1/0/1"),
        new("30", "0050.5600.0050", "DYNAMIC", "Gi1/0/24"),
    ];

    private static readonly IReadOnlyList<KnownSwitch> Known = [new("ACC1", "00aa.0000.0012"), new("ACC2", "00aa.0000.0013"), new("ACC3", "00aa.0000.0014")];

    [Fact]
    public void A_port_with_known_switches_behind_it_is_an_uplink()
    {
        var d = LocateTracer.Decide(Core, "0050.5600.0040", Known, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CORE" }, "CORE");
        Assert.Equal(HopKind.Uplink, d.Kind);
        Assert.Equal("Gi1/0/2", d.Port);
        Assert.Equal("20", d.Vlan);
        Assert.Equal(["ACC2", "ACC3"], d.Neighbors);
        Assert.Equal(["ACC2", "ACC3"], d.Candidates);
    }

    [Fact]
    public void Visited_switches_are_never_candidates_again()
    {
        var d = LocateTracer.Decide(Core, "0050.5600.0040", Known, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CORE", "ACC2" }, "CORE");
        Assert.Equal(["ACC3"], d.Candidates);
    }

    [Fact]
    public void A_port_without_known_switches_is_where_the_device_is()
    {
        var d = LocateTracer.Decide(Core, "0050.5600.0050", Known, new HashSet<string>(StringComparer.Ordinal), "CORE");
        Assert.Equal(HopKind.Edge, d.Kind);
        Assert.Equal("Gi1/0/24", d.Port);
        Assert.Equal(1, d.MacsOnPort);
    }

    [Fact]
    public void A_mac_missing_from_the_table_is_reported()
    {
        Assert.Equal(HopKind.NotInTable, LocateTracer.Decide(Core, "0050.5600.0099", Known, new HashSet<string>(StringComparer.Ordinal), "CORE").Kind);
    }

    [Theory]
    [InlineData("show ip arp", true)]
    [InlineData("show mac address-table", true)]
    [InlineData("show interfaces Gi1/0/7 status", true)]
    [InlineData("ping 10.20.30.40 repeat 2 timeout 1", true)]
    [InlineData("configure terminal", false)]
    [InlineData("show interfaces Gi1/0/7 status\nconfigure terminal", false)]
    [InlineData("clear mac address-table dynamic", false)]
    [InlineData("ping 10.20.30.40", false)]
    [InlineData("write memory", false)]
    public void Only_read_only_commands_are_sent(string command, bool allowed) => Assert.Equal(allowed, LocateOperation.IsReadOnly(command));

    [Theory]
    [InlineData(3, "Gi1/0/48  Uplink             connected    trunk      a-full a-1000 10/100/1000BaseTX", true)]
    [InlineData(380, "", true)]
    [InlineData(10, "", true)]
    [InlineData(3, "Gi1/0/7   PC-Accounting      connected    20         a-full a-1000 10/100/1000BaseTX", false)]
    public void An_edge_port_that_looks_like_an_uplink(int macs, string status, bool expected) => Assert.Equal(expected, LocateTracer.LooksLikeUplink(macs, status));

    [Fact]
    public void Absent_switches_are_the_ones_missing_from_the_table()
    {
        var table = Core.Where(e => e.Mac != "00aa.0000.0014").ToList();
        Assert.Equal(["ACC3"], LocateTracer.Absent(table, Known, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CORE" }, "CORE"));
        Assert.Empty(LocateTracer.Absent(table, Known, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ACC3" }, "CORE"));
    }
}
