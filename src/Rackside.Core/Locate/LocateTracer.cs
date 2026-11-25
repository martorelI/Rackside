namespace Rackside.Core.Locate;

/// <summary>What one switch says about the MAC being located.</summary>
public enum HopKind
{
    /// <summary>The MAC is not in this switch's table (aged out, or the trail is wrong).</summary>
    NotInTable,

    /// <summary>Learned on a port with no known switch behind it: the device is connected there.</summary>
    Edge,

    /// <summary>Learned on a port with one or more known switches behind it: the next hop is one of them.</summary>
    Uplink,
}

/// <summary>The decision on one switch: port, VLAN, how many MACs share that port and which switches are behind it.</summary>
public sealed record HopDecision(HopKind Kind, string Port, string Vlan, int MacsOnPort, IReadOnlyList<string> Neighbors, IReadOnlyList<string> Candidates);

/// <summary>A switch of the list whose management MAC is known.</summary>
public sealed record KnownSwitch(string Name, string Mac);

/// <summary>
/// Layer 2 trace: on each switch, the port where the MAC is learned tells whether it is connected there or
/// behind another switch of the list (whose management MAC is learned on the same port). No CDP/LLDP.
/// </summary>
public static class LocateTracer
{
    /// <summary>
    /// Decides on the switch <paramref name="self"/> from its MAC table. <paramref name="known"/> are the other
    /// switches with a known MAC; <paramref name="visited"/> the ones already queried (never a candidate again).
    /// </summary>
    public static HopDecision Decide(IReadOnlyList<MacEntry> table, string targetMac, IReadOnlyList<KnownSwitch> known, IReadOnlySet<string> visited, string self)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(visited);
        var hit = table.FirstOrDefault(e => e.Mac == targetMac && e.Type.StartsWith("DYNAMIC", StringComparison.Ordinal))
            ?? table.FirstOrDefault(e => e.Mac == targetMac);
        if (hit is null)
        {
            return new HopDecision(HopKind.NotInTable, string.Empty, string.Empty, 0, [], []);
        }
        var onPort = table.Where(e => Ci.Eq(e.Port, hit.Port)).ToList();
        var macs = onPort.Select(e => e.Mac).Distinct(StringComparer.Ordinal).Count();
        var neighbors = known.Where(k => !Ci.Eq(k.Name, self) && onPort.Any(e => e.Mac == k.Mac)).Select(k => k.Name).ToList();
        if (neighbors.Count == 0)
        {
            return new HopDecision(HopKind.Edge, hit.Port, hit.Vlan, macs, [], []);
        }
        var candidates = neighbors.Where(n => !visited.Contains(n)).ToList();
        return new HopDecision(HopKind.Uplink, hit.Port, hit.Vlan, macs, neighbors, candidates);
    }

    /// <summary>From this many MACs on a port, it is taken as an uplink even if it is not a trunk.</summary>
    public const int UplinkMacs = 10;

    /// <summary>
    /// A port where the MAC is learned with no known switch behind it, but that looks like an uplink: a trunk
    /// ("show interfaces ... status" says trunk) or many MACs. Then a switch is probably behind it whose
    /// management MAC aged out of the table (or is not in the list).
    /// </summary>
    public static bool LooksLikeUplink(int macsOnPort, string portStatusLine) =>
        macsOnPort >= UplinkMacs || Ci.IsMatch(portStatusLine ?? string.Empty, "\\strunk(\\s|$)");

    /// <summary>The known switches (not visited, not <paramref name="self"/>) whose MAC is nowhere in the table.</summary>
    public static IReadOnlyList<string> Absent(IReadOnlyList<MacEntry> table, IReadOnlyList<KnownSwitch> known, IReadOnlySet<string> visited, string self)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(visited);
        return known.Where(k => !Ci.Eq(k.Name, self) && !visited.Contains(k.Name) && table.All(e => e.Mac != k.Mac)).Select(k => k.Name).ToList();
    }
}
