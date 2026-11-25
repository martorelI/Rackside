using System.Globalization;

namespace Rackside.Core.Locate;

/// <summary>A row of "show ip arp".</summary>
public sealed record ArpEntry(string Ip, string Mac, string Interface);

/// <summary>A row of "show mac address-table" with a single physical or port-channel port.</summary>
public sealed record MacEntry(string Vlan, string Mac, string Type, string Port);

/// <summary>MAC addresses in the switch format (aaaa.bbbb.cccc).</summary>
public static class MacAddress
{
    /// <summary>
    /// Accepts aa:bb:cc:dd:ee:ff, aa-bb-cc-dd-ee-ff, aabb.ccdd.eeff or aabbccddeeff (any case) and returns
    /// aabb.ccdd.eeff in lowercase.
    /// </summary>
    public static bool TryNormalize(string? text, out string mac)
    {
        mac = string.Empty;
        var s = (text ?? string.Empty).Trim();
        if (!Ci.IsMatch(s, "^([0-9a-f]{2}[:-]){5}[0-9a-f]{2}$|^[0-9a-f]{4}\\.[0-9a-f]{4}\\.[0-9a-f]{4}$|^[0-9a-f]{12}$"))
        {
            return false;
        }
        var hex = Ci.Replace(s, "[^0-9a-f]", string.Empty).ToLowerInvariant();
        mac = string.Join('.', hex[..4], hex[4..8], hex[8..]);
        return true;
    }
}

/// <summary>Reads the outputs of the switch used to locate an address.</summary>
public static class LocateParser
{
    /// <summary>The switch rejected the command (or it does not exist on this platform).</summary>
    public static bool IsError(string output) => Ci.IsMatch(output, "(?m)^\\s*% ?(Invalid input|Incomplete command|Ambiguous command|Unknown command)");

    /// <summary>"show ip arp": Internet entries with a MAC (incomplete ones are skipped).</summary>
    public static IReadOnlyList<ArpEntry> Arp(string output)
    {
        var list = new List<ArpEntry>();
        foreach (var line in Ci.Split(output, "\\r?\\n"))
        {
            if (Ci.TryMatch(line, "^\\s*Internet\\s+(\\d{1,3}(?:\\.\\d{1,3}){3})\\s+\\S+\\s+([0-9a-f]{4}\\.[0-9a-f]{4}\\.[0-9a-f]{4})\\s+\\S+\\s*(\\S*)", out var m))
            {
                list.Add(new ArpEntry(m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant(), m.Groups[3].Value));
            }
        }
        return list;
    }

    /// <summary>
    /// "show mac address-table": entries learned on one port (CPU, Router, Drop and multi-port static entries
    /// are skipped). Same layout on classic IOS and IOS-XE.
    /// </summary>
    public static IReadOnlyList<MacEntry> MacTable(string output)
    {
        var list = new List<MacEntry>();
        foreach (var line in Ci.Split(output, "\\r?\\n"))
        {
            if (!Ci.TryMatch(line, "^\\s*\\*?\\s*(\\d+|All)\\s+([0-9a-f]{4}\\.[0-9a-f]{4}\\.[0-9a-f]{4})\\s+(\\S+)\\s+(?:\\S+\\s+)*?(\\S+)\\s*$", out var m))
            {
                continue;
            }
            var port = m.Groups[4].Value;
            if (!IsPort(port))
            {
                continue;
            }
            list.Add(new MacEntry(m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant(), m.Groups[3].Value.ToUpperInvariant(), port));
        }
        return list;
    }

    /// <summary>An interface name as the switch prints it (Gi1/0/7, Fa0/1, Te1/1/1, Po1...): never CPU or a list.</summary>
    public static bool IsPort(string text) =>
        Ci.IsMatch(text, "^[A-Za-z]{2,}[A-Za-z\\-]*\\d+(/\\d+)*(\\.\\d+)?$") && !Ci.IsMatch(text, "^(CPU|Router|Switch|Drop|Vlan)");

    /// <summary>The VLAN number of an ARP interface ("Vlan20" -> "20"), empty if it is not an SVI.</summary>
    public static string VlanOf(string arpInterface) =>
        Ci.TryMatch(arpInterface, "^Vlan(\\d+)$", out var m) ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : string.Empty;
}
