using System.Text.RegularExpressions;
using Rackside.Core.Localization;
using Rackside.Core.Restore;

namespace Rackside.Core.Configure;

/// <summary>Readable summary of a switch (no communities or passwords): what the query writes to the list.</summary>
public sealed record SwitchDocument(string Hostname, string Model, string Platform, string Version, string Serial, string LLDP, string CDP, string VTP, string STP,
    string Domain, string SNMP, string NTP, string Syslog, string Banner, string SSH, string IPManagement)
{
    /// <summary>The commands it needs, in order.</summary>
    public static readonly IReadOnlyList<(string Command, int Timeout)> Commands =
    [
        ("show lldp", 30), ("show cdp", 30), ("show vtp status", 30), ("show spanning-tree summary | include mode", 60), ("show ip interface brief | include ^Vlan", 60),
    ];

    /// <summary>Running-config blocks whose header matches the pattern: list of their trimmed sublines.</summary>
    public static List<List<string>> RunningBlocks(string? running, string pattern)
    {
        var res = new List<List<string>>();
        List<string>? current = null;
        foreach (var l in Ci.Split(running, "\\r?\\n"))
        {
            if (Ci.IsMatch(l, "^\\S"))
            {
                current = null;
                if (Ci.IsMatch(l, pattern))
                {
                    current = [];
                    res.Add(current);
                }
            }
            else if (current is not null && l.Trim().Length > 0)
            {
                current.Add(l.Trim());
            }
        }
        return res;
    }

    private static IEnumerable<string> Unique(string text, string pattern) =>
        Regex.Matches(text, pattern).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    public static SwitchDocument Build(TargetInfo version, string? lldp, string? cdp, string? vtp, string? stp, string? running, string? brief, string hostname, Translator t)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(t);
        var run = running ?? string.Empty;
        string State(string? txt, string active) => Ci.IsMatch(txt, "(?i)not enabled") ? t.T("Disabled") : Ci.IsMatch(txt, active) ? t.T("Enabled") : "?";
        var lldpTxt = State(lldp, "(?i)Global LLDP|Status:\\s*ACTIVE|LLDP advertisements");
        var cdpTxt = State(cdp, "(?i)Global CDP|Sending CDP");

        var vMode = Ci.TryMatch(vtp, "(?im)^\\s*VTP Operating Mode\\s*:\\s*(\\S+)", out var m) ? m.Groups[1].Value : string.Empty;
        var vDom = Ci.TryMatch(vtp, "(?im)^\\s*VTP Domain Name\\s*:[ \\t]*(\\S*)", out m) ? m.Groups[1].Value : string.Empty;
        var vVer = Ci.TryMatch(vtp, "(?im)^\\s*VTP version running\\s*:\\s*(\\d)", out m) ? m.Groups[1].Value
            : Ci.TryMatch(vtp, "(?i)running VTP\\s*(\\d)", out m) ? m.Groups[1].Value : string.Empty;
        var vtpTxt = vMode.Length > 0
            ? Ci.F(t.T("{0} / domain {1} / v{2}"), vMode, vDom.Length > 0 ? "\"" + vDom + "\"" : t.T("(none)"), vVer.Length > 0 ? vVer : "?")
            : string.Empty;

        var stpTxt = Ci.TryMatch(stp, "(?i)Switch is in (\\S+) mode", out m) ? m.Groups[1].Value : string.Empty;
        var dom = Ci.TryMatch(run, "(?m)^ip domain[ -]name (\\S+)", out m) ? m.Groups[1].Value : string.Empty;

        var snmp = new List<string>();
        if (Ci.TryMatch(run, "(?m)^snmp-server location (.+)$", out m)) { snmp.Add(Ci.F(t.T("location \"{0}\""), m.Groups[1].Value.Trim())); }
        if (Ci.TryMatch(run, "(?m)^snmp-server contact (.+)$", out m)) { snmp.Add(Ci.F(t.T("contact \"{0}\""), m.Groups[1].Value.Trim())); }
        var nCom = Regex.Matches(run, "(?m)^snmp-server community \\S+").Count;
        if (nCom > 0) { snmp.Add(Ci.F(t.T("{0} v2c community(ies)"), nCom)); }
        var nGrp = Regex.Matches(run, "(?m)^snmp-server group \\S+ v3").Count;
        if (nGrp > 0) { snmp.Add(Ci.F(t.T("{0} v3 group(s)"), nGrp)); }
        var traps = Unique(run, "(?m)^snmp-server host (\\S+)").ToList();
        if (traps.Count > 0) { snmp.Add(Ci.F(t.T("traps to {0}"), string.Join(", ", traps))); }

        var ntpTxt = string.Join(", ", Unique(run, "(?m)^ntp server (?:vrf \\S+ )?(\\S+)"));
        if (Ci.TryMatch(run, "(?m)^clock timezone (.+)$", out m))
        {
            ntpTxt = Ci.F(t.T("{0}{1}zone {2}"), ntpTxt, ntpTxt.Length > 0 ? " / " : string.Empty, m.Groups[1].Value.Trim());
        }
        if (Ci.TryMatch(run, "(?m)^clock summer-time (\\S+)", out m))
        {
            ntpTxt = Ci.F(t.T("{0} (summer {1})"), ntpTxt, m.Groups[1].Value);
        }

        var logTxt = string.Join(", ", Unique(run, "(?m)^logging (?:host )?(\\d{1,3}(?:\\.\\d{1,3}){3})\\b"));
        if (Ci.TryMatch(run, "(?m)^logging trap (\\S+)", out m))
        {
            logTxt = Ci.F(t.T("{0}{1}level {2}"), logTxt, logTxt.Length > 0 ? " / " : string.Empty, m.Groups[1].Value);
        }

        var banners = string.Join(", ", Unique(run, "(?m)^banner (\\S+)"));

        var ssh = new List<string>();
        if (Ci.IsMatch(run, "(?m)^ip ssh version 2\\s*$")) { ssh.Add(t.T("SSH v2")); }
        var transports = new List<string>();
        var acl = string.Empty;
        foreach (var b in RunningBlocks(run, "^line vty "))
        {
            foreach (var h in b)
            {
                if (Ci.TryMatch(h, "^transport input (.+)$", out m)) { transports.Add(m.Groups[1].Value.Trim()); }
                if (Ci.TryMatch(h, "^access-class (\\S+) in", out m)) { acl = m.Groups[1].Value; }
            }
        }
        var tr = transports.Distinct(StringComparer.Ordinal).ToList();
        ssh.Add(tr.Count > 0 ? Ci.F("vty: {0}", string.Join(" | ", tr)) : t.T("vty: default transport"));
        if (acl.Length > 0) { ssh.Add("ACL " + acl); }

        var ipManagement = Ci.TryMatch(brief, "(?m)^Vlan\\d+\\s+(\\d{1,3}(?:\\.\\d{1,3}){3})\\s", out m) ? m.Groups[1].Value : string.Empty;

        return new SwitchDocument(hostname, version.Model, version.OS, version.Version, version.Serial, lldpTxt, cdpTxt, vtpTxt, stpTxt,
            dom, string.Join("; ", snmp), ntpTxt, logTxt, banners, string.Join("; ", ssh), ipManagement);
    }

    /// <summary>Copies the document to an inventory row.</summary>
    public void CopyTo(InventoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row["Hostname"] = Hostname;
        row["Model"] = Model;
        row["Platform"] = Platform;
        row["Version"] = Version;
        row["Serial"] = Serial;
        row["LLDP"] = LLDP;
        row["CDP"] = CDP;
        row["VTP"] = VTP;
        row["STP"] = STP;
        row["Domain"] = Domain;
        row["SNMP"] = SNMP;
        row["NTP"] = NTP;
        row["Syslog"] = Syslog;
        row["Banner"] = Banner;
        row["SSH"] = SSH;
    }
}
