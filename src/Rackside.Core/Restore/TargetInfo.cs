namespace Rackside.Core.Restore;

/// <summary>An entry of "dir".</summary>
public sealed record DirEntry(string Name, bool IsDir);

/// <summary>What is read from the target switch before building the plan.</summary>
public sealed class TargetInfo
{
    public required string OS { get; init; }
    public required string Model { get; init; }
    public required string Serial { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> Interfaces { get; init; }
    public required IReadOnlyList<DirEntry> Dir { get; init; }
    /// <summary>False if the switch is not at factory defaults.</summary>
    public required bool Clean { get; init; }
    /// <summary>What it has that a factory switch would not (hostname, users, enable, ACLs).</summary>
    public required string Remains { get; init; }

    /// <summary>Builds the information from the outputs of the commands the tool runs on the switch.</summary>
    /// <param name="showVersion">show version</param>
    /// <param name="showIpInterfaceBrief">show ip interface brief</param>
    /// <param name="dir">dir</param>
    /// <param name="runningInclude">show running-config | include ^hostname|^username|^enable|^ip access-list|^access-list</param>
    /// <param name="t">translator for the "remains" text</param>
    public static TargetInfo Parse(string showVersion, string showIpInterfaceBrief, string dir, string runningInclude, Func<string, string>? t = null)
    {
        t ??= s => s;
        if (!Ci.IsMatch(showVersion, "(?i)cisco"))
        {
            throw new FormatException(t("The connected device does not look like a Cisco switch."));
        }
        var os = Ci.IsMatch(showVersion, "(?i)IOS[ -]XE") ? "IOS-XE" : "IOS";
        var model = Ci.TryMatch(showVersion, "(?im)^\\s*Model Number\\s*:\\s*(\\S+)", out var m1) ? m1.Groups[1].Value
            : Ci.TryMatch(showVersion, "(?im)^\\s*cisco\\s+(\\S+)\\s+\\(", out var m2) ? m2.Groups[1].Value : string.Empty;
        var serial = Ci.TryMatch(showVersion, "(?im)^\\s*System Serial Number\\s*:\\s*(\\S+)", out var m3) ? m3.Groups[1].Value
            : Ci.TryMatch(showVersion, "(?im)Processor board ID\\s+(\\S+)", out var m4) ? m4.Groups[1].Value : string.Empty;
        var version = Ci.TryMatch(showVersion, "(?i)Version\\s+([^,\\s]+)", out var m5) ? m5.Groups[1].Value : string.Empty;

        var interfaces = new List<string>();
        foreach (var l in Ci.Split(showIpInterfaceBrief, "\\r?\\n"))
        {
            if (Ci.TryMatch(l, "^(\\S+)\\s+\\S+\\s+(?:YES|NO)\\s+\\S+\\s+(?:up|down|administratively down|deleted)\\s+(?:up|down)\\s*$", out var mi))
            {
                interfaces.Add(mi.Groups[1].Value);
            }
        }

        var entries = new List<DirEntry>();
        foreach (var l in Ci.Split(dir, "\\r?\\n"))
        {
            if (Ci.TryMatch(l, "^\\s*\\d+\\s+([d-][rwx-]+)\\s+(\\d+)\\s+.*\\s(\\S+)\\s*$", out var md))
            {
                entries.Add(new DirEntry(md.Groups[3].Value, md.Groups[1].Value.StartsWith('d')));
            }
        }

        // Is it factory default? (hostname "Switch", no users, no enable, no ACLs)
        var linesRun = Ci.Split(runningInclude, "\\r?\\n").Where(l => Ci.IsMatch(l, "^(hostname|username|enable|ip access-list|access-list)\\s")).ToList();
        var hostDest = string.Empty;
        foreach (var l in linesRun)
        {
            if (Ci.TryMatch(l, "^hostname\\s+(\\S+)", out var mh))
            {
                hostDest = mh.Groups[1].Value;
            }
        }
        var nUsers = linesRun.Count(l => Ci.IsMatch(l, "^username\\s"));
        var nEnable = linesRun.Count(l => Ci.IsMatch(l, "^enable\\s"));
        var nAcl = linesRun.Count(l => Ci.IsMatch(l, "^ip access-list\\s"))
            + linesRun.Where(l => Ci.IsMatch(l, "^access-list\\s")).Select(l => Ci.Split(l, "\\s+")[1]).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var parts = new List<string>();
        if (hostDest.Length > 0 && !Ci.Eq(hostDest, "Switch")) { parts.Add("hostname " + hostDest); }
        if (nUsers > 0) { parts.Add(Ci.F(t("{0} user(s)"), nUsers)); }
        if (nEnable > 0) { parts.Add(t("enable configured")); }
        if (nAcl > 0) { parts.Add(Ci.F("{0} ACL(s)", nAcl)); }

        return new TargetInfo
        {
            OS = os,
            Model = model,
            Serial = serial,
            Version = version,
            Interfaces = interfaces,
            Dir = entries,
            Clean = parts.Count == 0,
            Remains = string.Join(", ", parts),
        };
    }
}
