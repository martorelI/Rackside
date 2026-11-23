namespace Rackside.Core.Restore;

/// <summary>
/// Restore security filter. A backup is text applied with privilege 15: a tampered file could bring
/// "do reload", EEM, aliases, extra users, etc. Before any conversion: every top-level line must be in
/// the allow list; blocked commands are always removed, whether header or subline (IOS runs in global
/// mode a subline that does not exist in the submode); lines with control characters, banners without
/// a free delimiter and certificate chains with non-hexadecimal lines are blocked.
/// </summary>
public static class SecurityFilter
{
    /// <summary>Top-level commands allowed from a backup (matched against the line without a leading "no ").</summary>
    public static readonly IReadOnlyList<string> AllowedCommands =
    [
        "^version\\s", "^service\\s", "^hostname\\s", "^boot-(start|end)-marker\\s*$", "^logging\\s", "^logging$",
        "^enable\\s+(secret|password|algorithm-type)\\b", "^username\\s", "^aaa\\s", "^clock\\s", "^system\\s",
        "^switch\\s+\\d+\\s+(provision|priority)\\s", "^vtp\\s", "^vlan\\s", "^ip\\s", "^ipv6\\s", "^interface\\s",
        "^spanning-tree\\s", "^errdisable\\s", "^port-channel\\s", "^udld\\s", "^lldp\\s", "^lldp$", "^cdp\\s", "^cdp$", "^mls\\s",
        "^class-map\\s", "^policy-map\\s", "^table-map\\s", "^access-list\\s", "^mac\\s", "^mac-address-table\\s", "^line\\s",
        "^snmp-server\\s", "^snmp\\s", "^ntp\\s", "^sntp\\s", "^radius\\s", "^radius-server\\s", "^tacacs\\s", "^tacacs-server\\s",
        "^crypto\\s+(pki|engine|ca)\\b", "^license\\s", "^diagnostic\\s", "^memory\\s", "^redundancy\\s*$", "^monitor\\s+session\\s",
        "^call-home\\s*$", "^device-tracking\\b", "^dot1x\\s", "^authentication\\s", "^access-session\\s", "^service-template\\s",
        "^power\\s", "^stack-power\\s", "^transceiver\\s", "^key\\s+chain\\s", "^router\\s", "^route-map\\s", "^track\\s",
        "^control-plane\\s*$", "^login\\s", "^security\\s", "^password\\s+encryption\\s", "^network-policy\\s", "^flow\\s",
        "^sampler\\s", "^subscriber\\s", "^multilink\\s", "^sdm\\s", "^srr-queue\\s", "^wrr-queue\\s", "^queue-set\\s",
        "^parameter-map\\s", "^template\\s", "^vrf\\s", "^object-group\\s", "^time-range\\s", "^rmon\\s", "^auto\\s+qos\\b",
        "^energywise\\s", "^location\\s", "^netconf-yang\\b", "^restconf\\s*$", "^platform\\s", "^lacp\\s", "^pagp\\s",
        "^vmps\\s", "^fhrp\\s", "^bfd\\s", "^qos\\s", "^policer\\s", "^cts\\s", "^identity\\s", "^eap\\s", "^epm\\s", "^ethernet\\s",
        "^dual-active\\b", "^stack-mac\\s", "^mvr\\b", "^rep\\s", "^ptp\\s", "^vstack\\s*$", "^macro\\s+description\\b",
    ];

    /// <summary>(regex, reason). Evaluated against the line and against the line without a leading "no ".</summary>
    public static readonly IReadOnlyList<(string Pattern, string Reason)> BlockedCommands =
    [
        ("^do(\\s|$)", "Runs an exec mode command from configuration mode"),
        ("^(reload|erase|delete|format|copy|write|squeeze|rename|mkdir|rmdir|fsck|verify|undelete|clear|debug|undebug|test|send|configure|telnet|ssh|connect|traceroute|ping|install|request|upgrade|software)\\b", "Exec command: reloads, erases, copies or modifies the device"),
        ("^license\\s+(install|clear|smart\\s+(register|deregister))\\b", "License exec command"),
        ("^(event|kron|scheduler|archive|tclsh|tclquit|shell|guestshell|app-hosting|iox|python|alias|parser|menu|autocommand)\\b", "Automation or command execution (EEM, kron, archive, Tcl, shell, alias, menu, autocommand)"),
        ("^macro\\s+(?!description\\b)", "Macro: runs commands"),
        ("^privilege\\s+(?!level\\b)", "Changes the privilege level of commands"),
        ("^boot(\\s|$)", "Boot parameters: managed in \"Update IOS\", not from a backup"),
        ("^config-register\\b", "Configuration register (can make the startup-config be ignored)"),
        ("^service\\s+(internal|config)\\b", "Enables hidden commands or loads configuration from the network at boot"),
        ("^snmp-server\\s+system-shutdown\\b", "Allows reloading the switch via SNMP"),
        ("^(file|tftp-server|pnp|ztp)\\b", "File prompts, TFTP server or remote provisioning"),
        ("^crypto\\s+key\\b", "Device keys (zeroize / import / generate): the tool generates its own"),
        ("^vstack(\\s|$)", "Smart Install (vulnerable): only \"no vstack\" is allowed"),
        ("^end(\\s|$)", "Leaves configuration mode"),
    ];

    /// <summary>Sublines only: global commands hidden inside another block.</summary>
    public static readonly IReadOnlyList<(string Pattern, string Reason)> BlockedSublines =
    [
        ("^(interface|line|vlan)\\s", "Opens another block from a subline"),
        ("^username\\s", "User hidden as a subline (IOS would create it in global mode)"),
        ("^hostname\\s", "Hostname hidden as a subline"),
        ("^enable\\s+(secret|password)\\b", "Enable hidden as a subline"),
        ("^aaa\\s", "AAA hidden as a subline"),
        ("^snmp-server\\s", "SNMP hidden as a subline"),
        ("^ip\\s+(ssh|http|scp)\\b", "Remote access hidden as a subline"),
        ("^crypto\\s", "Cryptography hidden as a subline"),
    ];

    public const string ControlRegex = "[\\x00-\\x08\\x0B-\\x1F\\x7F]";
    private static readonly string[] BannerDelimiters = ["#", "$", "%", "~", "@", "&", "|", "^", "*"];

    /// <summary>Reason if the line is forbidden; empty if it can be sent.</summary>
    public static string BlockedReason(string line, bool subline = false, string header = "")
    {
        var t = line.Trim();
        if (Ci.IsMatch(t, "^no\\s+(vstack|service\\s+(config|internal))\\s*$"))
        {
            return string.Empty;
        }
        var noNo = Ci.Replace(t, "^no\\s+", string.Empty);
        foreach (var (p, reason) in BlockedCommands)
        {
            if (Ci.IsMatch(t, p) || Ci.IsMatch(noNo, p))
            {
                return reason;
            }
        }
        if (subline)
        {
            foreach (var (p, reason) in BlockedSublines)
            {
                if (Ci.IsMatch(noNo, p))
                {
                    if (p == "^username\\s" && Ci.IsMatch(header, "^ip ssh pubkey-chain"))
                    {
                        continue;
                    }
                    return reason;
                }
            }
        }
        return string.Empty;
    }

    /// <summary>True if the top-level line is in the allow list (or in the extra regexes from settings).</summary>
    public static bool IsAllowed(string header, IEnumerable<string>? extraAllowed = null)
    {
        var noNo = Ci.Replace(header.Trim(), "^no\\s+", string.Empty);
        return AllowedCommands.Concat(extraAllowed ?? []).Any(p => !string.IsNullOrEmpty(p) && Ci.IsMatch(noNo, p));
    }

    /// <summary>Applies the filter. Blocks are changed in place (tabs -> spaces).</summary>
    internal static List<ConfigBlock> Apply(IEnumerable<ConfigBlock> blocks, PlanReport report, IEnumerable<string>? extraAllowed)
    {
        var output = new List<ConfigBlock>();
        foreach (var b in blocks)
        {
            // Tabs autocomplete on the console; they are sent as spaces
            b.Header = b.Header.Replace('\t', ' ');
            for (var i = 0; i < b.Children.Count; i++)
            {
                b.Children[i] = b.Children[i].Replace('\t', ' ');
            }
            var label = b.IsBanner ? "banner " + b.BannerType : b.Header;
            if (new[] { b.Header }.Concat(b.Children).Concat(b.BannerText).Any(x => Ci.IsMatch(x, ControlRegex)))
            {
                report.Add(ReportType.Blocked, Ci.Replace(label, ControlRegex, "?"), "Contains control characters (Ctrl+Z, Ctrl+C, etc.) that can take the console out of configuration mode");
                continue;
            }
            if (b.IsBanner)
            {
                var text = string.Join('\n', b.BannerText);
                if (BannerDelimiters.All(d => text.Contains(d, StringComparison.Ordinal)))
                {
                    report.Add(ReportType.Blocked, label, "The text uses every possible delimiter: the banner could close early and the rest would run as commands");
                    continue;
                }
                output.Add(b);
                continue;
            }
            var extra = b.Children.Count > 0 ? Ci.F(report.T(" (+{0} sublines)"), b.Children.Count) : string.Empty;
            if (Ci.IsMatch(b.Header, "^banner\\b"))
            {
                report.Add(ReportType.Blocked, b.Header + extra, "Banner with a delimiter other than ^C: it cannot be delimited safely");
                continue;
            }
            var reasonH = BlockedReason(b.Header);
            if (reasonH.Length > 0)
            {
                report.Add(ReportType.Blocked, b.Header + extra, reasonH);
                continue;
            }
            if (!IsAllowed(b.Header, extraAllowed))
            {
                report.Add(ReportType.Blocked, b.Header + extra, "Not in the list of allowed commands. If it is legitimate, apply it by hand or add it to ExtraAllowedCommands in settings.json.");
                continue;
            }
            if (Ci.IsMatch(b.Header, "^crypto pki certificate chain "))
            {
                var rare = b.Children.Where(c => !Ci.IsMatch(c.Trim(), "^(certificate\\s+(ca\\s+|self-signed\\s+)?\\S+|quit|[0-9A-Fa-f ]+)$")).ToList();
                if (rare.Count > 0)
                {
                    report.Add(ReportType.Blocked, b.Header + extra, Ci.F(report.T("The certificate chain has lines that are not hexadecimal: {0}"), rare[0].Trim()));
                    continue;
                }
                output.Add(b);
                continue;
            }
            var children = new List<string>();
            foreach (var h in b.Children)
            {
                var mh = BlockedReason(h, subline: true, header: b.Header);
                if (mh.Length > 0)
                {
                    report.Add(ReportType.Blocked, Ci.F("[{0}] {1}", b.Header, h.Trim()), mh);
                    continue;
                }
                children.Add(h);
            }
            b.Children = children;
            output.Add(b);
        }
        return output;
    }
}
