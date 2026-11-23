using System.Text;
using System.Text.RegularExpressions;

namespace Rackside.Core.Restore;

/// <summary>A physical Ethernet interface numbered x/y or x/y/z.</summary>
public sealed record InterfaceInfo(string Long, string Short, string Type, int Member, int Module, int Port, bool IsManagement)
{
    /// <summary>Long type name -> short prefix, in matching order.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Types =
    [
        new("TwentyFiveGigE", "Twe"),
        new("TwoGigabitEthernet", "Tw"),
        new("FiveGigabitEthernet", "Fi"),
        new("TenGigabitEthernet", "Te"),
        new("FortyGigabitEthernet", "Fo"),
        new("HundredGigE", "Hu"),
        new("GigabitEthernet", "Gi"),
        new("FastEthernet", "Fa"),
    ];

    /// <summary>Speed in Mb/s per long type name.</summary>
    public static readonly IReadOnlyDictionary<string, int> Speed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["HundredGigE"] = 100000, ["FortyGigabitEthernet"] = 40000, ["TwentyFiveGigE"] = 25000, ["TenGigabitEthernet"] = 10000,
        ["FiveGigabitEthernet"] = 5000, ["TwoGigabitEthernet"] = 2500, ["GigabitEthernet"] = 1000, ["FastEthernet"] = 100,
    };

    /// <summary>null if the name is not a physical Ethernet interface.</summary>
    public static InterfaceInfo? Parse(string? name)
    {
        if (!Ci.TryMatch(name, "^([A-Za-z]+)\\s?(\\d+(?:/\\d+)+)$", out var m))
        {
            return null;
        }
        var pref = m.Groups[1].Value;
        var nums = m.Groups[2].Value;
        string? longName = null;
        string shortName = string.Empty;
        foreach (var (k, v) in Types)
        {
            if (Ci.Eq(pref, k) || Ci.Eq(pref, v))
            {
                longName = k;
                shortName = v;
                break;
            }
        }
        if (longName is null)
        {
            return null;
        }
        var parts = nums.Split('/').Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        int member, module, port;
        if (parts.Length >= 3)
        {
            (member, module, port) = (parts[0], parts[1], parts[^1]);
        }
        else
        {
            (member, module, port) = (1, parts[0], parts[1]);
        }
        return new InterfaceInfo(longName + nums, shortName + nums, longName, member, module, port,
            longName == "GigabitEthernet" && nums == "0/0");
    }
}

/// <summary>One row of the interface mapping report.</summary>
public sealed record MappingRow(string Source, string Target, string Role, int Member);

/// <summary>Source -> target interface mapping.</summary>
public sealed class InterfaceMap
{
    public const string Access = "access";
    public const string Uplink = "uplink";

    /// <summary>Lower case source long name -> target long name.</summary>
    public required IReadOnlyDictionary<string, string> Map { get; init; }
    public required IReadOnlyList<MappingRow> Rows { get; init; }
    public required IReadOnlyList<string> NoMap { get; init; }
    /// <summary>Target interfaces with nothing mapped to them.</summary>
    public required IReadOnlyList<string> FreeList { get; init; }

    private static readonly Regex RefPattern = new(
        "\\b(TwentyFiveGigE|TwoGigabitEthernet|FiveGigabitEthernet|TenGigabitEthernet|FortyGigabitEthernet|HundredGigE|GigabitEthernet|FastEthernet|Twe|Tw|Fi|Te|Fo|Hu|Gi|Fa)\\s?(\\d+(?:/\\d+)+)\\b");

    private sealed record WithRole(InterfaceInfo Info, string Role);

    /// <summary>Classifies the physical interfaces as access and uplink per stack member.</summary>
    private static List<WithRole> Classify(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var infos = new List<InterfaceInfo>();
        foreach (var n in names)
        {
            var x = InterfaceInfo.Parse(n);
            if (x is not null && !x.IsManagement && seen.Add(x.Long.ToLowerInvariant()))
            {
                infos.Add(x);
            }
        }
        // Ordered groups: member|type|module
        var groupKeys = new List<string>();
        var groups = new Dictionary<string, List<InterfaceInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in infos)
        {
            var k = Ci.F("{0}|{1}|{2}", x.Member, x.Type, x.Module);
            if (!groups.TryGetValue(k, out var g))
            {
                groups[k] = g = [];
                groupKeys.Add(k);
            }
            g.Add(x);
        }
        var result = new List<WithRole>();
        foreach (var m in infos.Select(x => x.Member).Distinct().Order())
        {
            var keys = groupKeys.Where(k => k.StartsWith(Ci.F("{0}|", m), StringComparison.OrdinalIgnoreCase)).ToList();
            var kAccess = keys.OrderByDescending(k => groups[k].Count).ThenBy(k => k, Ci.SortComparer).First();
            foreach (var k in keys)
            {
                var list = groups[k].OrderBy(x => x.Port).ToList();
                if (Ci.Eq(k, kAccess))
                {
                    var c = list.Count;
                    var nAccess = c;
                    int[] std = [48, 24, 16, 12, 8];
                    var s = std.Where(v => v <= c).Cast<int?>().FirstOrDefault();
                    if (s is not null && (c - s.Value) is 1 or 2 or 4)
                    {
                        nAccess = s.Value;
                    }
                    for (var i = 0; i < c; i++)
                    {
                        result.Add(new WithRole(list[i], i < nAccess ? Access : Uplink));
                    }
                }
                else
                {
                    result.AddRange(list.Select(x => new WithRole(x, Uplink)));
                }
            }
        }
        return result;
    }

    private static List<InterfaceInfo> Sorted(List<WithRole> withRole, int member, string role)
    {
        var sel = withRole.Where(w => w.Info.Member == member && w.Role == role).Select(w => w.Info);
        return role == Access
            ? [.. sel.OrderBy(x => x.Module).ThenBy(x => x.Port)]
            : [.. sel.OrderByDescending(x => InterfaceInfo.Speed[x.Type]).ThenBy(x => x.Module).ThenBy(x => x.Port)];
    }

    /// <summary>
    /// Access: position by position. Uplinks: first the ones with configuration, looking in the target for
    /// one of the same type and, if there is none, the next free one.
    /// </summary>
    public static InterfaceMap Build(IEnumerable<string> source, IEnumerable<string> target, IReadOnlySet<string> withConfig, string noEquivalentText)
    {
        var ro = Classify(source);
        var rd = Classify(target);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<MappingRow>();
        var noMap = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ro.Select(w => w.Info.Member).Distinct().Order())
        {
            var lo = Sorted(ro, m, Access);
            var ld = Sorted(rd, m, Access);
            for (var i = 0; i < lo.Count; i++)
            {
                if (i < ld.Count)
                {
                    map[lo[i].Long.ToLowerInvariant()] = ld[i].Long;
                    used.Add(ld[i].Long.ToLowerInvariant());
                    rows.Add(new MappingRow(lo[i].Long, ld[i].Long, Access, m));
                }
                else
                {
                    noMap.Add(lo[i].Long);
                    rows.Add(new MappingRow(lo[i].Long, noEquivalentText, Access, m));
                }
            }
            var uo = ro.Where(w => w.Info.Member == m && w.Role == Uplink).Select(w => w.Info)
                .OrderByDescending(x => withConfig.Contains(x.Long.ToLowerInvariant()) ? 1 : 0)
                .ThenByDescending(x => InterfaceInfo.Speed[x.Type]).ThenBy(x => x.Module).ThenBy(x => x.Port).ToList();
            var ud = Sorted(rd, m, Uplink);
            foreach (var x in uo)
            {
                var cand = ud.FirstOrDefault(y => !used.Contains(y.Long.ToLowerInvariant()) && Ci.Eq(y.Type, x.Type))
                    ?? ud.FirstOrDefault(y => !used.Contains(y.Long.ToLowerInvariant()));
                if (cand is not null)
                {
                    map[x.Long.ToLowerInvariant()] = cand.Long;
                    used.Add(cand.Long.ToLowerInvariant());
                    rows.Add(new MappingRow(x.Long, cand.Long, Uplink, m));
                }
                else
                {
                    noMap.Add(x.Long);
                    rows.Add(new MappingRow(x.Long, noEquivalentText, Uplink, m));
                }
            }
        }
        var free = rd.Where(w => !used.Contains(w.Info.Long.ToLowerInvariant())).Select(w => w.Info.Long).ToList();
        return new InterfaceMap { Map = map, Rows = rows, NoMap = noMap, FreeList = free };
    }

    /// <summary>
    /// Replaces references to physical interfaces inside a line (keeping the short or long form). Returns
    /// null if any reference has no equivalent on the target.
    /// </summary>
    public string? ConvertReferences(string line)
    {
        if (Ci.IsMatch(line, "^\\s*description\\s"))
        {
            return line;
        }
        var ms = RefPattern.Matches(line);
        if (ms.Count == 0)
        {
            return line;
        }
        var sb = new StringBuilder();
        var pos = 0;
        foreach (Match m in ms)
        {
            var info = InterfaceInfo.Parse(m.Groups[1].Value + m.Groups[2].Value);
            var replacement = m.Value;
            if (info is not null && !info.IsManagement)
            {
                if (!Map.TryGetValue(info.Long.ToLowerInvariant(), out var targetName))
                {
                    return null;
                }
                var dest = InterfaceInfo.Parse(targetName)!;
                replacement = m.Groups[1].Value.Length <= 3 ? dest.Short : dest.Long;
            }
            sb.Append(line, pos, m.Index - pos).Append(replacement);
            pos = m.Index + m.Length;
        }
        sb.Append(line, pos, line.Length - pos);
        return sb.ToString();
    }
}
