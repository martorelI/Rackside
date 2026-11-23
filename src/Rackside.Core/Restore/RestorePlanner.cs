using System.Text.RegularExpressions;
using Rackside.Core.Localization;
using Rackside.Core.Security;

namespace Rackside.Core.Restore;

/// <summary>How the backup is applied.</summary>
public enum RestoreMode
{
    /// <summary>Same model: applied as is.</summary>
    Direct,
    /// <summary>Different model: interfaces remapped and syntax converted.</summary>
    Convert,
}

/// <summary>The generated configuration and everything the review shows about it.</summary>
public sealed class RestorePlan
{
    public required RestoreMode Mode { get; init; }
    /// <summary>"IOS-XE -> IOS" etc.</summary>
    public required string Direction { get; init; }
    public required IReadOnlyList<ConfigBlock> Blocks { get; init; }
    public required PlanReport Report { get; init; }
    /// <summary>null in DIRECT mode.</summary>
    public required InterfaceMap? Mapping { get; init; }
    /// <summary>Lines that will be sent.</summary>
    public required int Lines { get; init; }

    /// <summary>Internal code of the mode (also the English text shown).</summary>
    public string ModeCode => Mode == RestoreMode.Direct ? "DIRECT" : "CONVERT";
}

/// <summary>Builds the configuration to apply from a backup and the target switch.</summary>
public static class RestorePlanner
{
    private static readonly string[] BannerDelimiters = ["#", "$", "%", "~", "@", "&", "|", "^", "*"];

    public static RestorePlan Build(SourceConfig source, TargetInfo target, RestoreOptions opt, Translator translator, SecretMasker masker)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(masker);

        var report = new PlanReport(translator, masker);
        var blocks = source.Blocks.Select(b => b.Clone()).ToList();
        // Security filter first: whatever does not pass never reaches any conversion
        blocks = SecurityFilter.Apply(blocks, report, opt.ExtraAllowedCommands);
        blocks = UserFilter(blocks, report, opt, source);

        static string Norm(string m) => string.IsNullOrWhiteSpace(m) ? string.Empty : Ci.Replace(m.Trim().ToLowerInvariant(), "^ws-", string.Empty);
        var mSource = Norm(opt.ModelSource);
        var mTarget = Norm(target.Model);
        var direct = mSource.Length > 0 && Ci.Eq(mSource, mTarget);

        var sets = new List<RuleSet> { RuleSets.Always };
        if (opt.RemoveCertificate)
        {
            sets.Add(RuleSets.Certificate);
        }
        var direction = source.OS + " -> " + target.OS;
        InterfaceMap? mapping = null;
        RestoreMode mode;
        if (direct)
        {
            mode = RestoreMode.Direct;
            blocks = ApplyRules(blocks, sets, report);
            // boot system: only if the image exists on the target
            var namesDir = target.Dir.Select(e => e.Name).ToList();
            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                var b = blocks[i];
                if (!b.IsBanner && Ci.TryMatch(b.Header, "^boot system (?:switch all )?(\\S+)", out var m))
                {
                    var path = Ci.Replace(m.Groups[1].Value, "^[A-Za-z0-9\\-]+:/*", string.Empty);
                    var top = path.Split('/')[0];
                    if (!namesDir.Contains(top, StringComparer.OrdinalIgnoreCase))
                    {
                        report.Add(ReportType.Removed, b.Header, Ci.F(translator.T("Image \"{0}\" does not exist on the target"), top));
                        blocks.RemoveAt(i);
                    }
                }
            }
        }
        else
        {
            mode = RestoreMode.Convert;
            sets.Add(RuleSets.Common);
            if (source.OS == "IOS-XE" && target.OS == "IOS")
            {
                sets.Add(RuleSets.XEtoIOS);
            }
            else if (source.OS == "IOS" && target.OS == "IOS-XE")
            {
                sets.Add(RuleSets.IOStoXE);
            }
            blocks = ApplyRules(blocks, sets, report);
            blocks = VtyAdjustment(blocks, report);
            mapping = InterfaceMap.Build(source.Physical, target.Interfaces, source.WithConfig, translator.T("(no equivalent)"));
            blocks = Remap(blocks, mapping, report);
        }

        // Changes from the dialog
        if (opt.Hostname.Length > 0)
        {
            SetGlobalLine(blocks, "^hostname\\s", "hostname " + opt.Hostname, "^(service |no service )", report, translator.T("Hostname from the dialog"));
        }
        if (opt.Domain.Length > 0)
        {
            var cmdDomain = (target.OS == "IOS-XE" ? "ip domain name " : "ip domain-name ") + opt.Domain;
            SetGlobalLine(blocks, "^ip domain[ -]name\\s", cmdDomain, "^hostname\\s", report, translator.T("Domain from the dialog"));
        }
        if (opt.EnableSecret.Length > 0)
        {
            SetGlobalLine(blocks, "^enable (secret|password)\\s", "enable secret " + opt.EnableSecret, "^hostname\\s", report, translator.T("Enable secret from the dialog"), "enable secret ********");
        }
        if (opt.UserAdmin.Length > 0 && opt.PasswordAdmin.Length > 0)
        {
            SetGlobalLine(blocks, "^username " + Regex.Escape(opt.UserAdmin) + "\\s", Ci.F("username {0} privilege 15 secret {1}", opt.UserAdmin, opt.PasswordAdmin),
                "^(enable |hostname\\s)", report, translator.T("Administrator user from the dialog"), Ci.F("username {0} privilege 15 secret ********", opt.UserAdmin));
        }
        var crossOs = mode == RestoreMode.Convert && source.OS != target.OS;
        if (opt.EnableSecret.Length == 0 && crossOs && blocks.Any(b => !b.IsBanner && Ci.IsMatch(b.Header, "^enable secret [89] ")))
        {
            report.Add(ReportType.Warning, translator.T("enable with type 8/9 hash"), "Type 8/9 hash: if the target IOS does not accept it, enable is left without a password. Enter an enable secret in the dialog to avoid it.");
        }
        if (opt.PasswordAdmin.Length == 0 && opt.UserAdmin.Length > 0 && crossOs
            && blocks.Any(b => !b.IsBanner && Ci.IsMatch(b.Header, "^username " + Regex.Escape(opt.UserAdmin) + "\\s.*secret [89] ")))
        {
            report.Add(ReportType.Warning, Ci.F(translator.T("username {0} with type 8/9 hash"), opt.UserAdmin), "Type 8/9 hash: if the target IOS does not accept it, the user is not created. Enter a password in the dialog to avoid it.");
        }
        if (opt.InterfaceManagement.Length > 0 && opt.IP.Length > 0)
        {
            SetManagementInterface(blocks, opt.InterfaceManagement, opt.IP, opt.Mask, report, translator);
        }
        if (opt.Gateway.Length > 0)
        {
            SetGateway(blocks, opt.Gateway, report, translator);
        }
        if (opt.CreateVlans)
        {
            AddMissingVlans(blocks, report);
        }
        if (opt.Hostname.Length > 0 && source.Hostname.Length > 0 && !Ci.Eq(opt.Hostname, source.Hostname))
        {
            // Identifier change: warn about other lines that still mention the previous one
            var pattern = "(?i)(?<![A-Za-z0-9\\-])" + Regex.Escape(source.Hostname) + "(?![A-Za-z0-9\\-])";
            var reason = Ci.F(translator.T("Mentions the previous hostname ({0}). It is not changed automatically: check whether it should carry the new one ({1})."), source.Hostname, opt.Hostname);
            foreach (var b in blocks)
            {
                if (b.IsBanner)
                {
                    foreach (var t in b.BannerText.Where(t => Ci.IsMatch(t, pattern)))
                    {
                        report.Add(ReportType.Warning, Ci.F("[banner {0}] {1}", b.BannerType, t.Trim()), reason);
                    }
                    continue;
                }
                if (!Ci.IsMatch(b.Header, "^hostname\\s") && Ci.IsMatch(b.Header, pattern))
                {
                    report.Add(ReportType.Warning, b.Header, reason);
                }
                foreach (var h in b.Children.Where(h => Ci.IsMatch(h, pattern)))
                {
                    report.Add(ReportType.Warning, Ci.F("[{0}] {1}", b.Header, h.Trim()), reason);
                }
            }
        }
        blocks = AddCleanupLists(blocks, report);
        if (!target.Clean)
        {
            report.Add(ReportType.Warning, Ci.F(translator.T("Target switch is NOT at factory defaults: {0}"), target.Remains),
                "Whatever the switch has that is not in the backup stays (users, ACLs, etc.). If it was already applied without saving, reload it without saving; otherwise, factory reset it before applying.");
        }
        SetBannerDelimiters(blocks);

        var lines = blocks.Sum(b => 1 + b.Children.Count + b.BannerText.Count);
        return new RestorePlan { Mode = mode, Direction = direction, Blocks = blocks, Report = report, Mapping = mapping, Lines = lines };
    }

    /// <summary>
    /// The administrator (the one in the dialog or, if empty, the one detected in the backup) always passes.
    /// Other users and user SSH keys only if the operator confirmed it.
    /// </summary>
    private static List<ConfigBlock> UserFilter(List<ConfigBlock> blocks, PlanReport report, RestoreOptions opt, SourceConfig source)
    {
        var admin = opt.UserAdmin.Length > 0 ? opt.UserAdmin : source.UserAdmin;
        var output = new List<ConfigBlock>();
        foreach (var b in blocks)
        {
            if (!b.IsBanner && Ci.TryMatch(b.Header, "^username\\s+(\\S+)", out var mu) && !string.Equals(mu.Groups[1].Value, admin, StringComparison.Ordinal))
            {
                var name = mu.Groups[1].Value;
                var priv = Ci.TryMatch(b.Header, "\\bprivilege\\s+(\\d+)", out var mp) ? mp.Groups[1].Value : "1";
                var line = Ci.F("username {0} (privilege {1})", name, priv);
                if (opt.IncludeUsers)
                {
                    report.Add(ReportType.Warning, line, "Additional user from the backup: restored because you checked \"Also restore the other users\". Confirm it must exist.");
                    output.Add(b);
                }
                else
                {
                    report.Add(ReportType.Removed, line, "Additional user from the backup: not created without confirmation. If it must exist, check \"Also restore the other users from the backup\" (tab 3).");
                }
                continue;
            }
            if (!b.IsBanner && Ci.IsMatch(b.Header, "^ip ssh pubkey-chain"))
            {
                var line = Ci.F("ip ssh pubkey-chain (+{0} sublines)", b.Children.Count);
                if (opt.IncludeUsers)
                {
                    report.Add(ReportType.Warning, line, "User SSH public keys: they grant access without a password. Restored because you checked \"Also restore the other users\".");
                    output.Add(b);
                }
                else
                {
                    report.Add(ReportType.Removed, line, "User SSH public keys (passwordless access): not restored without confirmation.");
                }
                continue;
            }
            if (!b.IsBanner && Ci.IsMatch(b.Header, "^snmp-server\\s+community\\s+\\S+.*\\bRW\\b"))
            {
                report.Add(ReportType.Warning, b.Header, "Write (RW) SNMP community: allows changing the configuration over SNMP. Check that it is needed.");
            }
            output.Add(b);
        }
        return output;
    }

    private static ConversionRule? FirstMatch(IEnumerable<RuleSet> sets, Func<RuleSet, IReadOnlyList<ConversionRule>> pick, string line)
    {
        foreach (var s in sets)
        {
            var r = pick(s).FirstOrDefault(r => Ci.IsMatch(line, r.P));
            if (r is not null)
            {
                return r;
            }
        }
        return null;
    }

    private static List<ConfigBlock> ApplyRules(List<ConfigBlock> blocks, List<RuleSet> sets, PlanReport report)
    {
        var output = new List<ConfigBlock>();
        foreach (var b in blocks)
        {
            if (b.IsBanner)
            {
                output.Add(b);
                continue;
            }
            var rule = FirstMatch(sets, s => s.RemoveBlock, b.Header);
            if (rule is not null)
            {
                var extra = b.Children.Count > 0 ? Ci.F(report.T(" (+{0} sublines)"), b.Children.Count) : string.Empty;
                report.Add(ReportType.Removed, b.Header + extra, rule.M);
                continue;
            }
            var hdr = b.Header;
            foreach (var r in sets.SelectMany(s => s.Replace))
            {
                if (Ci.IsMatch(hdr, r.P))
                {
                    var newer = Ci.Replace(hdr, r.P, r.R);
                    if (!Ci.Eq(newer, hdr))
                    {
                        report.Add(ReportType.Changed, Ci.F("{0}  ->  {1}", hdr, newer), r.M);
                        hdr = newer;
                    }
                }
            }
            var children = new List<string>();
            foreach (var h in b.Children)
            {
                var ruleH = FirstMatch(sets, s => s.RemoveChild, h);
                if (ruleH is not null)
                {
                    report.Add(ReportType.Removed, Ci.F("[{0}] {1}", hdr, h.Trim()), ruleH.M);
                    continue;
                }
                var hn = h;
                foreach (var r in sets.SelectMany(s => s.Replace))
                {
                    if (Ci.IsMatch(hn, r.P))
                    {
                        var tmp = Ci.Replace(hn, r.P, r.R);
                        if (!Ci.Eq(tmp, hn))
                        {
                            report.Add(ReportType.Changed, Ci.F("[{0}] {1}  ->  {2}", hdr, hn.Trim(), tmp.Trim()), r.M);
                            hn = tmp;
                        }
                    }
                }
                children.Add(hn);
            }
            output.Add(new ConfigBlock(hdr, children));
        }
        return output;
    }

    private static List<ConfigBlock> Remap(List<ConfigBlock> blocks, InterfaceMap map, PlanReport report)
    {
        var output = new List<ConfigBlock>();
        foreach (var b in blocks)
        {
            if (b.IsBanner)
            {
                output.Add(b);
                continue;
            }
            var hdr = b.Header;
            if (Ci.TryMatch(hdr, "^interface (\\S+)$", out var m))
            {
                var info = InterfaceInfo.Parse(m.Groups[1].Value);
                if (info is not null && !info.IsManagement)
                {
                    if (!map.Map.TryGetValue(info.Long.ToLowerInvariant(), out var dest))
                    {
                        if (b.Children.Count > 0)
                        {
                            report.Add(ReportType.NoMapping, Ci.F("{0} (+{1} sublines)", hdr, b.Children.Count), "The target has no equivalent interface");
                        }
                        continue;
                    }
                    hdr = "interface " + dest;
                }
            }
            else
            {
                var converted = map.ConvertReferences(hdr);
                if (converted is null)
                {
                    report.Add(ReportType.NoMapping, hdr, "References an interface with no equivalent on the target");
                    continue;
                }
                hdr = converted;
            }
            var children = new List<string>();
            foreach (var h in b.Children)
            {
                var hn = map.ConvertReferences(h);
                if (hn is null)
                {
                    report.Add(ReportType.NoMapping, Ci.F("[{0}] {1}", hdr, h.Trim()), "References an interface with no equivalent on the target");
                    continue;
                }
                children.Add(hn);
            }
            output.Add(new ConfigBlock(hdr, children));
        }
        return output;
    }

    private static List<ConfigBlock> VtyAdjustment(List<ConfigBlock> blocks, PlanReport report)
    {
        var output = new List<ConfigBlock>();
        foreach (var b in blocks)
        {
            if (!b.IsBanner && Ci.TryMatch(b.Header, "^line vty (\\d+) (\\d+)$", out var m))
            {
                var a = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var z = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (a > 15)
                {
                    report.Add(ReportType.Removed, b.Header, "The target only has vty 0-15");
                    continue;
                }
                if (z > 15)
                {
                    var newer = Ci.F("line vty {0} 15", a);
                    report.Add(ReportType.Changed, Ci.F("{0}  ->  {1}", b.Header, newer), "The target only has vty 0-15");
                    b.Header = newer;
                }
            }
            output.Add(b);
        }
        return output;
    }

    private static int FindBlock(List<ConfigBlock> blocks, string pattern) =>
        blocks.FindIndex(b => !b.IsBanner && Ci.IsMatch(b.Header, pattern));

    /// <summary>Replaces (or inserts) a top-level line. Removes duplicates that match the pattern.</summary>
    private static void SetGlobalLine(List<ConfigBlock> blocks, string pattern, string newEntry, string afterOf, PlanReport report, string reason, string? show = null)
    {
        if (string.IsNullOrEmpty(show))
        {
            show = newEntry;
        }
        var idx = FindBlock(blocks, pattern);
        if (idx >= 0)
        {
            var previous = blocks[idx].Header;
            if (!Ci.Eq(previous, newEntry))
            {
                blocks[idx].Header = newEntry;
                var shown = Ci.IsMatch(previous, "(secret|password)") ? string.Join(' ', Ci.Split(previous, "\\s+").Take(2)) : previous;
                report.Add(ReportType.Changed, Ci.F("{0}  ->  {1}", shown, show), reason);
            }
            for (var j = blocks.Count - 1; j > idx; j--)
            {
                if (!blocks[j].IsBanner && Ci.IsMatch(blocks[j].Header, pattern))
                {
                    blocks.RemoveAt(j);
                }
            }
            return;
        }
        var pos = 0;
        if (!string.IsNullOrEmpty(afterOf))
        {
            var a = FindBlock(blocks, afterOf);
            if (a >= 0)
            {
                pos = a + 1;
            }
        }
        blocks.Insert(pos, new ConfigBlock(newEntry));
        report.Add(ReportType.Added, show, reason);
    }

    private static IEnumerable<int> ExpandVlanList(string text)
    {
        foreach (var p in text.Split(','))
        {
            if (Ci.TryMatch(p, "^(\\d+)-(\\d+)$", out var m))
            {
                var from = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var to = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                for (var v = from; v <= to; v++)
                {
                    yield return v;
                }
            }
            else if (Ci.IsMatch(p, "^\\d+$"))
            {
                yield return int.Parse(p, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    private static void AddMissingVlans(List<ConfigBlock> blocks, PlanReport report)
    {
        var defined = new HashSet<int>();
        var referenced = new HashSet<int>();
        static int Int(Match m) => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var b in blocks.Where(b => !b.IsBanner))
        {
            if (Ci.TryMatch(b.Header, "^vlan ([\\d,\\-]+)$", out var mv))
            {
                defined.UnionWith(ExpandVlanList(mv.Groups[1].Value));
            }
            if (Ci.TryMatch(b.Header, "^interface Vlan(\\d+)$", out var mi))
            {
                referenced.Add(Int(mi));
            }
            foreach (var h in b.Children)
            {
                if (Ci.TryMatch(h, "^\\s+switchport (?:access|voice) vlan (\\d+)\\s*$", out var ma))
                {
                    referenced.Add(Int(ma));
                }
                else if (Ci.TryMatch(h, "^\\s+switchport trunk native vlan (\\d+)\\s*$", out var mn))
                {
                    referenced.Add(Int(mn));
                }
            }
        }
        var missing = referenced.Where(v => v != 1 && (v < 1002 || v > 1005) && !defined.Contains(v)).Order().ToList();
        if (missing.Count == 0)
        {
            return;
        }
        var pos = FindBlock(blocks, "^interface ");
        if (pos < 0)
        {
            pos = blocks.Count;
        }
        foreach (var v in missing.OrderDescending())
        {
            blocks.Insert(pos, new ConfigBlock(Ci.F("vlan {0}", v)));
        }
        report.Add(ReportType.Added, "vlan " + string.Join(", ", missing), "VLANs used on interfaces but not defined in the file (VTP server/client keeps VLANs in vlan.dat)");
    }

    private static void SetManagementInterface(List<ConfigBlock> blocks, string iface, string ip, string mask, PlanReport report, Translator translator)
    {
        var idx = FindBlock(blocks, "^interface " + Regex.Escape(iface) + "$");
        if (idx < 0)
        {
            var pos = FindBlock(blocks, "^interface Vlan");
            if (pos < 0)
            {
                pos = FindBlock(blocks, "^(ip default-gateway|ip route|ip http|line )");
            }
            if (pos < 0)
            {
                pos = blocks.Count;
            }
            blocks.Insert(pos, new ConfigBlock("interface " + iface, [Ci.F(" ip address {0} {1}", ip, mask), " no shutdown"]));
            report.Add(ReportType.Added, Ci.F("interface {0} / ip address {1} {2}", iface, ip, mask), "Management interface from the dialog");
            return;
        }
        var b = blocks[idx];
        var currentIP = string.Empty;
        foreach (var h in b.Children)
        {
            if (Ci.TryMatch(h, "^\\s+ip address (\\d+\\.\\d+\\.\\d+\\.\\d+ \\d+\\.\\d+\\.\\d+\\.\\d+)\\s*$", out var m))
            {
                currentIP = m.Groups[1].Value;
            }
        }
        var newEntry = ip + " " + mask;
        var children = new List<string>();
        var hadShutdown = false;
        foreach (var h in b.Children)
        {
            if (!Ci.Eq(currentIP, newEntry) && Ci.IsMatch(h, "^\\s+(no )?ip address"))
            {
                continue;
            }
            if (Ci.IsMatch(h, "^\\s+shutdown\\s*$"))
            {
                hadShutdown = true;
                continue;
            }
            if (Ci.IsMatch(h, "^\\s+no shutdown\\s*$"))
            {
                continue;
            }
            children.Add(h);
        }
        if (!Ci.Eq(currentIP, newEntry))
        {
            children.Insert(0, " ip address " + newEntry);
            report.Add(ReportType.Changed, Ci.F("[interface {0}] ip address {1}  ->  {2}", iface, currentIP.Length > 0 ? currentIP : translator.T("(none)"), newEntry), "Management interface from the dialog");
        }
        children.Add(" no shutdown");
        if (hadShutdown)
        {
            report.Add(ReportType.Changed, Ci.F("[interface {0}] shutdown  ->  no shutdown", iface), "The management interface must be up");
        }
        b.Children = children;
    }

    private static void SetGateway(List<ConfigBlock> blocks, string gateway, PlanReport report, Translator translator)
    {
        SetGlobalLine(blocks, "^ip default-gateway\\s", "ip default-gateway " + gateway, "^interface ", report, translator.T("Gateway from the dialog"));
        // If there is a default route (switch with ip routing), change the next hop too
        foreach (var b in blocks.Where(b => !b.IsBanner))
        {
            if (Ci.TryMatch(b.Header, "^ip route 0\\.0\\.0\\.0 0\\.0\\.0\\.0 (\\d+\\.\\d+\\.\\d+\\.\\d+)(.*)$", out var m) && !Ci.Eq(m.Groups[1].Value, gateway))
            {
                var newer = Ci.F("ip route 0.0.0.0 0.0.0.0 {0}{1}", gateway, m.Groups[2].Value);
                report.Add(ReportType.Changed, Ci.F("{0}  ->  {1}", b.Header, newer), "Gateway from the dialog");
                b.Header = newer;
            }
        }
    }

    /// <summary>
    /// Before each ACL / prefix-list the list is deleted, so it ends up identical to the backup even if the
    /// switch already has it.
    /// </summary>
    private static List<ConfigBlock> AddCleanupLists(List<ConfigBlock> blocks, PlanReport report)
    {
        var output = new List<ConfigBlock>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in blocks)
        {
            if (!b.IsBanner)
            {
                string? cmd = null;
                if (Ci.TryMatch(b.Header, "^(ip|mac) access-list (standard|extended)\\s+(\\S+)\\s*$", out var m1))
                {
                    cmd = Ci.F("no {0} access-list {1} {2}", m1.Groups[1].Value, m1.Groups[2].Value, m1.Groups[3].Value);
                }
                else if (Ci.TryMatch(b.Header, "^ipv6 access-list (\\S+)\\s*$", out var m2))
                {
                    cmd = "no ipv6 access-list " + m2.Groups[1].Value;
                }
                else if (Ci.TryMatch(b.Header, "^access-list (\\d+)\\s", out var m3))
                {
                    cmd = "no access-list " + m3.Groups[1].Value;
                }
                else if (Ci.TryMatch(b.Header, "^ip prefix-list (\\S+)\\s", out var m4))
                {
                    cmd = "no ip prefix-list " + m4.Groups[1].Value;
                }
                if (cmd is not null && seen.Add(cmd))
                {
                    output.Add(new ConfigBlock(cmd));
                }
            }
            output.Add(b);
        }
        if (seen.Count > 0)
        {
            var list = seen.Order(Ci.SortComparer).ToList();
            var sample = string.Join("; ", list.Take(4));
            if (list.Count > 4)
            {
                sample += Ci.F(report.T("; ... ({0} in total)"), list.Count);
            }
            report.Add(ReportType.Added, sample, "Deletes each list right before loading it so it ends up identical to the backup (avoids \"Duplicate sequence number\" and duplicate entries if the switch already had it)");
        }
        return output;
    }

    private static void SetBannerDelimiters(List<ConfigBlock> blocks)
    {
        foreach (var b in blocks.Where(b => b.IsBanner))
        {
            var text = string.Join('\n', b.BannerText);
            b.Delimiter = BannerDelimiters.FirstOrDefault(c => !text.Contains(c, StringComparison.Ordinal)) ?? "#";
        }
    }
}
