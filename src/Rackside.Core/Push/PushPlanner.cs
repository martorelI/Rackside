using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rackside.Core.Localization;
using Rackside.Core.Restore;
using Rackside.Core.Security;

namespace Rackside.Core.Push;

/// <summary>A switch the push will connect to (for the preview).</summary>
public sealed record PushTarget(string Name, string IP, int Port);

/// <summary>How the push connects and saves (the part of the options the preview shows).</summary>
public sealed record PushRunOptions(string ConnectionText, bool Save, bool SaveWithErrors, IReadOnlyList<PushTarget>? Targets = null);

/// <summary>Additional commands after the restore filter and the push rules.</summary>
public sealed record FilteredExtra(IReadOnlyList<ConfigBlock> Blocks, IReadOnlyList<ReportItem> Report);

/// <summary>
/// Push generation (Configure switches): groups, validation, lines per platform, preview and risks.
/// Only commands common to classic IOS (2960) and IOS-XE (9200); never hostname or IP addresses.
/// </summary>
public static class PushPlanner
{
    private static readonly string[] Delimiters = ["#", "$", "%", "~", "@", "&", "|", "^", "*"];

    /// <summary>Names of the groups that will be applied.</summary>
    public static IReadOnlyList<string> Groups(PushParameters p, Translator t)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(t);
        var g = new List<string>();
        if (p.Lldp.Apply) { g.Add(t.T("LLDP and CDP")); }
        if (p.Banner.ApplyLogin) { g.Add(t.T("Login banner")); }
        if (p.Banner.ApplyMotd) { g.Add(t.T("MOTD banner")); }
        if (p.Vtp.Apply) { g.Add("VTP"); }
        if (p.Snmp.Apply) { g.Add("SNMP"); }
        if (p.Ntp.Apply) { g.Add(t.T("NTP and time")); }
        if (p.Syslog.Apply) { g.Add("Syslog"); }
        if (p.Dns.Apply) { g.Add("DNS"); }
        if (p.Ssh.Apply) { g.Add(t.T("SSH and lines")); }
        if (p.Services.Apply) { g.Add(t.T("Services")); }
        if (p.Stp.Apply) { g.Add("Spanning-tree"); }
        if (p.User.Apply) { g.Add(t.T("Local user / enable")); }
        if (p.Extra.Apply) { g.Add(t.T("Additional commands")); }
        return g;
    }

    /// <summary>Validates before connecting. Empty list = OK.</summary>
    public static IReadOnlyList<string> Validate(PushParameters p, Translator t)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(t);
        var e = new List<string>();
        void Bad(string s) => e.Add(s);
        void Ip(IEnumerable<string> l, string what) { foreach (var x in l.Where(x => !Validators.IsIPv4(x))) { Bad(Ci.F(t.T("{0}: \"{1}\" is not an IPv4 address."), what, x)); } }
        void NoQuestion(string v, string what) { if (v.Contains('?', StringComparison.Ordinal)) { Bad(Ci.F(t.T("{0}: cannot contain \"?\" (the console takes it as help)."), what)); } }
        void Word(string v, string what, int max) { if (v.Length > 0 && !Ci.IsMatch(v, "^[^\\s\\?\"]{1," + max.ToString(CultureInfo.InvariantCulture) + "}$")) { Bad(Ci.F(t.T("{0}: no spaces, quotes or \"?\" (maximum {1} characters)."), what, max)); } }
        void Iface(string v, string what) { if (v.Length > 0 && !Ci.IsMatch(v, "^[A-Za-z][A-Za-z\\-]*\\s?\\d+(/\\d+)*(\\.\\d+)?$")) { Bad(Ci.F(t.T("{0}: invalid interface \"{1}\" (e.g. Vlan10)."), what, v)); } }
        void Range(string v, string what, long min, long max)
        {
            if (v.Length > 0 && (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min || n > max))
            {
                Bad(Ci.F(t.T("{0}: must be a number from {1} to {2}."), what, min, max));
            }
        }
        void Acl(string v, string what) { if (v.Length > 0 && !Ci.IsMatch(v, "^[A-Za-z0-9_\\-]{1,64}$")) { Bad(Ci.F(t.T("{0}: invalid ACL name or number."), what)); } }

        if (Groups(p, t).Count == 0)
        {
            Bad(t.T("Check at least one group in tab 2 (\"Apply ...\")."));
        }
        if (p.Lldp.Apply && p.Lldp.Lldp == "No" && p.Lldp.Cdp == "No")
        {
            Bad(t.T("LLDP and CDP: choose to enable or disable at least one."));
        }
        foreach (var (apply, text, name) in new[] { (p.Banner.ApplyLogin, p.Banner.Login, t.T("Login banner")), (p.Banner.ApplyMotd, p.Banner.Motd, t.T("MOTD banner")) })
        {
            if (!apply)
            {
                continue;
            }
            var clean = text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
            if (Ci.IsMatch(clean, SecurityFilter.ControlRegex)) { Bad(Ci.F(t.T("{0}: contains control characters."), name)); }
            if (Delimiters.All(d => text.Contains(d, StringComparison.Ordinal))) { Bad(Ci.F(t.T("{0}: uses every possible delimiter (# $ % ~ @ & | ^ *); remove one."), name)); }
            if (text.Length > 2000) { Bad(Ci.F(t.T("{0}: too long (maximum 2000 characters)."), name)); }
        }
        if (p.Vtp.Apply)
        {
            var v = p.Vtp;
            if (!new[] { "transparent", "server", "client", "off" }.Contains(v.Mode, StringComparer.OrdinalIgnoreCase)) { Bad(t.T("VTP: invalid mode.")); }
            Word(v.Domain, t.T("VTP domain"), 32);
            if (v.Password.Length > 0 && (v.Password.Length < 8 || v.Password.Length > 64 || Ci.IsMatch(v.Password, "[\\s\\?]"))) { Bad(t.T("VTP password: 8 to 64 characters, no spaces or \"?\".")); }
            if (v.Version == "3" && v.Domain.Length == 0) { Bad(t.T("VTP version 3 requires the domain.")); }
        }
        if (p.Snmp.Apply)
        {
            var s = p.Snmp;
            if (!(s.Location.Length > 0 || s.Contact.Length > 0 || s.Community.Length > 0 || s.Traps.Count > 0 || s.EnableTraps || s.V3User.Length > 0)) { Bad(t.T("SNMP: there is nothing to apply.")); }
            NoQuestion(s.Location, t.T("SNMP location"));
            NoQuestion(s.Contact, t.T("SNMP contact"));
            Word(s.Community, t.T("SNMP community"), 32);
            if (Ci.IsMatch(s.Community, "^(?i)(public|private)$")) { Bad(t.T("SNMP community: \"public\" / \"private\" are the factory defaults and the first ones tried; use another one.")); }
            Acl(s.Acl, t.T("SNMP ACL"));
            if (s.Acl.Length > 0 && s.Community.Length == 0) { Bad(t.T("SNMP: the ACL applies to the community; enter the community.")); }
            Ip(s.Traps, t.T("SNMP traps"));
            if (s.Traps.Count > 0 && s.Community.Length == 0 && s.V3User.Length == 0) { Bad(t.T("SNMP traps: enter the community or a v3 user to send them.")); }
            if (s.RemoveOthers && s.Community.Length == 0 && s.V3User.Length == 0) { Bad(t.T("SNMP: \"Remove the others\" without a community or v3 user would leave the switch without SNMP.")); }
            if (s.V3User.Length > 0 || s.V3Group.Length > 0 || s.V3Auth.Length > 0 || s.V3Priv.Length > 0)
            {
                Word(s.V3User, t.T("SNMPv3 user"), 32);
                Word(s.V3Group, t.T("SNMPv3 group"), 32);
                if (s.V3User.Length == 0 || s.V3Group.Length == 0) { Bad(t.T("SNMPv3: enter user and group.")); }
                if (s.V3Auth.Length < 8 || Ci.IsMatch(s.V3Auth, "[\\s\\?]")) { Bad(t.T("SNMPv3: the authentication key (SHA) must have 8 or more characters, no spaces or \"?\".")); }
                if (s.V3Priv.Length > 0 && (s.V3Priv.Length < 8 || Ci.IsMatch(s.V3Priv, "[\\s\\?]"))) { Bad(t.T("SNMPv3: the encryption key (AES 128) must have 8 or more characters, no spaces or \"?\".")); }
            }
        }
        if (p.Ntp.Apply)
        {
            var n = p.Ntp;
            if (!(n.Servers.Count > 0 || n.Zone.Length > 0 || n.Summer.Length > 0 || n.Source.Length > 0)) { Bad(t.T("NTP and time: there is nothing to apply.")); }
            Ip(n.Servers, t.T("NTP"));
            Iface(n.Source, t.T("NTP source-interface"));
            if (n.RemoveOthers && n.Servers.Count == 0) { Bad(t.T("NTP: \"Remove the others\" requires at least one server.")); }
            if (n.Zone.Length > 0 && !Ci.IsMatch(n.Zone, "^[A-Za-z]{1,7}$")) { Bad(t.T("Time zone: name of 1 to 7 letters (e.g. PST).")); }
            if (n.Zone.Length > 0) { Range(n.Offset, t.T("Time zone: offset from UTC"), -12, 14); }
            if (n.Zone.Length > 0 && n.Offset.Length == 0) { Bad(t.T("Time zone: enter the offset from UTC (e.g. -8).")); }
            if (n.Summer.Length > 0 && !Ci.IsMatch(n.Summer, "^[A-Za-z]{1,7}$")) { Bad(t.T("Summer time: name of 1 to 7 letters (e.g. PDT).")); }
        }
        if (p.Syslog.Apply)
        {
            var l = p.Syslog;
            if (!(l.Servers.Count > 0 || l.Level != "No" || l.Source.Length > 0 || l.Buffer.Length > 0)) { Bad(t.T("Syslog: there is nothing to apply.")); }
            Ip(l.Servers, t.T("Syslog"));
            Iface(l.Source, t.T("Syslog source interface"));
            Range(l.Buffer, t.T("Syslog buffer"), 4096, 2147483647);
            if (l.RemoveOthers && l.Servers.Count == 0) { Bad(t.T("Syslog: \"Remove the others\" requires at least one server.")); }
        }
        if (p.Dns.Apply)
        {
            var d = p.Dns;
            if (!(d.Domain.Length > 0 || d.Servers.Count > 0 || d.NoLookup)) { Bad(t.T("DNS: there is nothing to apply.")); }
            if (d.Domain.Length > 0 && !Ci.IsMatch(d.Domain, "^[A-Za-z0-9]([A-Za-z0-9\\-]*[A-Za-z0-9])?(\\.[A-Za-z0-9]([A-Za-z0-9\\-]*[A-Za-z0-9])?)*$")) { Bad(t.T("DNS: invalid domain.")); }
            Ip(d.Servers, t.T("DNS"));
            if (d.Servers.Count > 6) { Bad(t.T("DNS: maximum 6 servers.")); }
        }
        if (p.Ssh.Apply)
        {
            var h = p.Ssh;
            if (!(h.V2 || h.Timeout.Length > 0 || h.Retries.Length > 0 || h.VtyTimeout.Length > 0 || h.OnlySsh || h.VtyAcl.Length > 0 || h.WithTimeout.Length > 0)) { Bad(t.T("SSH and lines: there is nothing to apply.")); }
            Range(h.Timeout, t.T("SSH time-out (seconds)"), 1, 120);
            Range(h.Retries, t.T("SSH retries"), 0, 5);
            Range(h.VtyTimeout, t.T("VTY exec-timeout (minutes)"), 0, 35791);
            Range(h.WithTimeout, t.T("Console exec-timeout (minutes)"), 0, 35791);
            Acl(h.VtyAcl, t.T("VTY ACL"));
            if (h.VtyTimeout == "0") { Bad(t.T("VTY exec-timeout 0 leaves remote sessions open forever; use 5 to 15 minutes.")); }
        }
        if (p.Services.Apply)
        {
            var v = p.Services;
            if (!(v.Encrypt || v.Timestamps || v.NoHttp || v.NoPad || v.Keepalives || v.NoSourceRoute || v.LoginLog)) { Bad(t.T("Services: check at least one.")); }
        }
        if (p.Stp.Apply && !new[] { "rapid-pvst", "pvst" }.Contains(p.Stp.Mode, StringComparer.OrdinalIgnoreCase))
        {
            Bad(t.T("Spanning-tree: invalid mode."));
        }
        if (p.User.Apply)
        {
            var us = p.User;
            if (us.Name.Length == 0 && us.Enable.Length == 0) { Bad(t.T("User / enable: enter a user or an enable secret.")); }
            if (us.Name.Length > 0)
            {
                if (!Ci.IsMatch(us.Name, "^[A-Za-z0-9_\\.\\-]{1,32}$")) { Bad(t.T("User: only letters, numbers, dot, hyphen and underscore.")); }
                if (us.Password.Length == 0) { Bad(t.T("User: the password is missing.")); }
                if (!string.Equals(us.Password, us.Password2, StringComparison.Ordinal)) { Bad(t.T("User: the passwords do not match.")); }
                if (us.Password.Length > 0 && (us.Password.Length < 8 || Ci.IsMatch(us.Password, "[\\s\\?]"))) { Bad(t.T("User: password of 8 or more characters, no spaces or \"?\".")); }
            }
            if (us.Enable.Length > 0 || us.Enable2.Length > 0)
            {
                if (!string.Equals(us.Enable, us.Enable2, StringComparison.Ordinal)) { Bad(t.T("Enable secret: they do not match.")); }
                if (us.Enable.Length < 8 || Ci.IsMatch(us.Enable, "[\\s\\?]")) { Bad(t.T("Enable secret: 8 or more characters, no spaces or \"?\".")); }
            }
        }
        if (p.Extra.Apply && p.Extra.Text.Trim().Length == 0)
        {
            Bad(t.T("Additional commands: the box is empty."));
        }
        return e;
    }

    /// <summary>The message shown when validation fails.</summary>
    public static string ValidationMessage(IReadOnlyList<string> errors, Translator t)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(t);
        return Ci.F(t.T("Check the parameters (tab 2):{0}- {1}"), "\r\n", string.Join("\r\n- ", errors));
    }

    /// <summary>Free text -> blocks: indented lines are sublines of the previous non-indented one.</summary>
    public static List<ConfigBlock> TextBlocks(string text)
    {
        var blocks = new List<ConfigBlock>();
        ConfigBlock? current = null;
        foreach (var raw in (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var l = raw.TrimEnd();
            if (l.Trim().Length == 0 || Ci.IsMatch(l.Trim(), "^!"))
            {
                if (Ci.IsMatch(l.Trim(), "^!")) { current = null; }
                continue;
            }
            if (Ci.IsMatch(l, "^\\s"))
            {
                if (current is not null)
                {
                    current.Children.Add(l);
                    continue;
                }
                l = l.Trim();
            }
            current = new ConfigBlock(l);
            blocks.Add(current);
        }
        return blocks;
    }

    /// <summary>Additional commands with the restore filter and rules specific to the mass push.</summary>
    public static FilteredExtra FilterExtra(string text, Translator t, SecretMasker masker, IEnumerable<string>? extraAllowed = null)
    {
        var report = new PlanReport(t, masker);
        var prior = new List<ConfigBlock>();
        foreach (var b in TextBlocks(text))
        {
            var c = b.Header.Trim();
            if (Ci.IsMatch(c, "^(?i)(exit|quit|configure)(\\s|$)"))
            {
                report.Add(ReportType.Blocked, c, "Not needed: the tool enters and leaves configuration mode");
                continue;
            }
            if (Ci.IsMatch(c, "^(no\\s+)?hostname\\s"))
            {
                report.Add(ReportType.Blocked, c, "The hostname is specific to each switch");
                continue;
            }
            var withIp = b.Children.FirstOrDefault(h => Ci.IsMatch(h.Trim(), "^ip address\\s+\\d"));
            if (withIp is not null)
            {
                report.Add(ReportType.Blocked, Ci.F("[{0}] {1}", c, withIp.Trim()), "An IP address is specific to each switch: it is not pushed");
                continue;
            }
            prior.Add(b);
        }
        var ok = SecurityFilter.Apply(prior, report, extraAllowed);
        return new FilteredExtra(ok, report.Items);
    }

    private static string Exact(string l) => "(?m)^" + Regex.Escape(l) + "\\s*$";
    private static string Esc(string x) => Regex.Escape(x);
    private static int ToInt(string s) => (int)Math.Round(double.Parse(s, CultureInfo.InvariantCulture), MidpointRounding.ToEven);

    /// <summary>
    /// Lines for one platform. With the switch's running-config it adds what depends on what it already has
    /// (removing extra communities, NTP or syslog servers, checking ACLs); without it, it leaves notes.
    /// </summary>
    public static IReadOnlyList<PushItem> Lines(PushParameters p, string platform, string? running, Translator t, SecretMasker masker, IEnumerable<string>? extraAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(t);
        var xe = platform == "IOS-XE";
        var hasRun = !string.IsNullOrEmpty(running);
        var run = running ?? string.Empty;
        var it = new List<PushItem>();

        if (p.Lldp.Apply)
        {
            var g = t.T("LLDP and CDP");
            if (p.Lldp.Lldp == "Enable") { it.Add(new(g, "lldp run", "show", "(?i)Global LLDP|Status:\\s*ACTIVE|LLDP advertisements", Command: "show lldp")); }
            else if (p.Lldp.Lldp == "Disable") { it.Add(new(g, "no lldp run", "show", "(?i)not enabled", Command: "show lldp")); }
            if (p.Lldp.Cdp == "Enable") { it.Add(new(g, "cdp run", "show", "(?i)Global CDP|Sending CDP", Command: "show cdp")); }
            else if (p.Lldp.Cdp == "Disable") { it.Add(new(g, "no cdp run", "show", "(?i)not enabled", Command: "show cdp")); }
        }

        foreach (var (apply, text, type, group) in new[] { (p.Banner.ApplyLogin, p.Banner.Login, "login", t.T("Login banner")), (p.Banner.ApplyMotd, p.Banner.Motd, "motd", t.T("MOTD banner")) })
        {
            if (!apply)
            {
                continue;
            }
            var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            if (lines.Count == 0 || string.Concat(lines).Trim().Length == 0)
            {
                it.Add(new(group, "no banner " + type, "absent", "(?m)^banner " + type + "\\b"));
            }
            else
            {
                var pat = "(?s)(?:^|\\n)banner " + type + " [^\\n]*" + string.Concat(lines.Where(l => l.Trim().Length > 0).Select(l => ".*?" + Esc(l.Trim())));
                it.Add(new(group, "banner " + type, "regex", pat, IsBanner: true, BannerType: type, BannerText: lines));
            }
        }

        if (p.Vtp.Apply)
        {
            var v = p.Vtp;
            var g = t.T("VTP");
            var modeItem = new PushItem(g, "vtp mode " + v.Mode, "show", Ci.F("(?im)^\\s*VTP Operating Mode\\s*:\\s*{0}\\b", v.Mode), Command: "show vtp status");
            if (v.Mode is "transparent" or "off") { it.Add(modeItem); }
            if (v.Domain.Length > 0) { it.Add(new(g, "vtp domain " + v.Domain, "show", Ci.F("(?im)^\\s*VTP Domain Name\\s*:\\s*{0}\\s*$", Esc(v.Domain)), Command: "show vtp status")); }
            if (Ci.IsMatch(v.Version, "^\\d$")) { it.Add(new(g, "vtp version " + v.Version, "show", Ci.F("(?im)^\\s*VTP version running\\s*:\\s*{0}\\b|running VTP\\s*{0}", v.Version), Command: "show vtp status")); }
            if (v.Password.Length > 0) { it.Add(new(g, "vtp password " + v.Password, "hidden")); }
            if (v.Mode is "server" or "client") { it.Add(modeItem); }
        }

        if (p.Snmp.Apply)
        {
            var s = p.Snmp;
            var g = t.T("SNMP");
            if (s.Location.Length > 0) { var l = "snmp-server location " + s.Location; it.Add(new(g, l, Pattern: Exact(l))); }
            if (s.Contact.Length > 0) { var l = "snmp-server contact " + s.Contact; it.Add(new(g, l, Pattern: Exact(l))); }
            if (s.Community.Length > 0)
            {
                var l = Ci.F("snmp-server community {0} RO", s.Community);
                if (s.Acl.Length > 0) { l += " " + s.Acl; }
                it.Add(new(g, l, Pattern: Exact(l)));
                if (s.Acl.Length > 0 && hasRun && !AclExists(run, s.Acl))
                {
                    it.Add(new(g, Ci.F(t.T("ACL {0} of the community"), s.Acl), "warning", Result: "NOTE", Detail: t.T("The ACL does not exist on this switch: the community has no source restriction until you create it.")));
                }
            }
            if (s.V3User.Length > 0)
            {
                var level = s.V3Priv.Length > 0 ? "priv" : "auth";
                var lg = Ci.F("snmp-server group {0} v3 {1}", s.V3Group, level);
                it.Add(new(g, lg, Pattern: "(?m)^" + Esc(lg) + "\\b"));
                var lu = Ci.F("snmp-server user {0} {1} v3 auth sha {2}", s.V3User, s.V3Group, s.V3Auth);
                if (s.V3Priv.Length > 0) { lu += Ci.F(t.T(" priv aes 128 {0}"), s.V3Priv); }
                it.Add(new(g, lu, "show", Ci.F("(?im)^\\s*User name:\\s*{0}\\s*$", Esc(s.V3User)), Command: "show snmp user"));
            }
            foreach (var h in s.Traps)
            {
                string l, pt;
                if (s.Community.Length > 0)
                {
                    l = Ci.F("snmp-server host {0} version 2c {1}", h, s.Community);
                    pt = Ci.F("(?m)^snmp-server host {0} (?:traps )?version 2c {1}(\\s|$)", Esc(h), Esc(s.Community));
                }
                else
                {
                    l = Ci.F("snmp-server host {0} version 3 {1} {2}", h, s.V3Priv.Length > 0 ? "priv" : "auth", s.V3User);
                    pt = Ci.F("(?m)^snmp-server host {0} (?:traps )?version 3 \\S+ {1}(\\s|$)", Esc(h), Esc(s.V3User));
                }
                it.Add(new(g, l, Pattern: pt));
            }
            if (s.EnableTraps) { it.Add(new(g, "snmp-server enable traps", Pattern: "(?m)^snmp-server enable traps\\b")); }
            if (s.RemoveOthers)
            {
                if (hasRun)
                {
                    foreach (Match m in Regex.Matches(run, "(?m)^snmp-server community (\\S+).*$"))
                    {
                        var nom = m.Groups[1].Value;
                        if (string.Equals(nom, s.Community, StringComparison.Ordinal)) { continue; }
                        it.Add(new(g, "no snmp-server community " + nom, "absent", Ci.F("(?m)^snmp-server community {0}(\\s|$)", Esc(nom))));
                    }
                    foreach (Match m in Regex.Matches(run, "(?m)^snmp-server host (\\S+) .*$"))
                    {
                        var line = m.Value.TrimEnd();
                        if (s.Traps.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                        {
                            var own = s.Community.Length > 0 ? Ci.IsMatch(line, Ci.F("\\s{0}(\\s|$)", Esc(s.Community))) : Ci.IsMatch(line, Ci.F("\\s{0}(\\s|$)", Esc(s.V3User)));
                            if (own) { continue; }
                        }
                        it.Add(new(g, "no " + line, "absent", Exact(line)));
                    }
                }
                else
                {
                    it.Add(new(g, t.T("! The communities and trap destinations the switch has that are not listed here are removed (read from its running-config)"), "note"));
                }
            }
        }

        if (p.Ntp.Apply)
        {
            var n = p.Ntp;
            var g = t.T("NTP and time");
            if (n.Zone.Length > 0)
            {
                var off = ToInt(n.Offset);
                it.Add(new(g, Ci.F("clock timezone {0} {1} 0", n.Zone, off), Pattern: Ci.F("(?m)^clock timezone {0} {1}( 0)?\\s*$", Esc(n.Zone), off)));
            }
            if (n.Summer.Length > 0) { it.Add(new(g, Ci.F("clock summer-time {0} recurring", n.Summer), Pattern: Ci.F("(?m)^clock summer-time {0} recurring\\b", Esc(n.Summer)))); }
            if (n.Source.Length > 0) { var l = "ntp source " + n.Source; it.Add(new(g, l, Pattern: Exact(l))); }
            foreach (var x in n.Servers) { it.Add(new(g, "ntp server " + x, Pattern: Ci.F("(?m)^ntp server (?:vrf \\S+ )?{0}(\\s|$)", Esc(x)))); }
            if (n.RemoveOthers)
            {
                if (hasRun)
                {
                    foreach (Match m in Regex.Matches(run, "(?m)^ntp server (vrf \\S+ )?(\\S+).*$"))
                    {
                        if (n.Servers.Contains(m.Groups[2].Value, StringComparer.OrdinalIgnoreCase)) { continue; }
                        it.Add(new(g, Ci.F("no ntp server {0}{1}", m.Groups[1].Value, m.Groups[2].Value), "absent", Ci.F("(?m)^ntp server (?:vrf \\S+ )?{0}(\\s|$)", Esc(m.Groups[2].Value))));
                    }
                }
                else
                {
                    it.Add(new(g, t.T("! The NTP servers the switch has that are not listed here are removed"), "note"));
                }
            }
        }

        if (p.Syslog.Apply)
        {
            var s = p.Syslog;
            var g = t.T("Syslog");
            if (s.Source.Length > 0) { var l = "logging source-interface " + s.Source; it.Add(new(g, l, Pattern: Exact(l))); }
            if (s.Level.Length > 0 && s.Level != "No") { it.Add(new(g, "logging trap " + s.Level, Pattern: Ci.F("(?m)^logging trap {0}\\s*$", s.Level), IsDefault: s.Level == "informational")); }
            if (s.Buffer.Length > 0) { it.Add(new(g, "logging buffered " + s.Buffer, Pattern: Ci.F("(?m)^logging buffered {0}\\b", s.Buffer), Default: true)); }
            foreach (var x in s.Servers) { it.Add(new(g, "logging host " + x, Pattern: Ci.F("(?m)^logging (?:host )?{0}(\\s|$)", Esc(x)))); }
            if (s.RemoveOthers)
            {
                if (hasRun)
                {
                    foreach (Match m in Regex.Matches(run, "(?m)^logging (host )?(\\d{1,3}(?:\\.\\d{1,3}){3})\\b.*$"))
                    {
                        if (s.Servers.Contains(m.Groups[2].Value, StringComparer.OrdinalIgnoreCase)) { continue; }
                        it.Add(new(g, Ci.F("no logging {0}{1}", m.Groups[1].Value, m.Groups[2].Value), "absent", Ci.F("(?m)^logging (?:host )?{0}(\\s|$)", Esc(m.Groups[2].Value))));
                    }
                }
                else
                {
                    it.Add(new(g, t.T("! The syslog servers the switch has that are not listed here are removed"), "note"));
                }
            }
        }

        if (p.Dns.Apply)
        {
            var d = p.Dns;
            var g = t.T("DNS");
            if (d.Domain.Length > 0) { it.Add(new(g, (xe ? "ip domain name " : "ip domain-name ") + d.Domain, Pattern: Ci.F("(?m)^ip domain[ -]name {0}\\s*$", Esc(d.Domain)))); }
            foreach (var x in d.Servers) { it.Add(new(g, "ip name-server " + x, Pattern: Ci.F("(?m)^ip name-server (?:vrf \\S+ )?(?:\\S+ )*{0}(\\s|$)", Esc(x)))); }
            if (d.NoLookup) { it.Add(new(g, xe ? "no ip domain lookup" : "no ip domain-lookup", Pattern: "(?m)^no ip domain[ -]lookup\\s*$")); }
        }

        if (p.Ssh.Apply)
        {
            var h = p.Ssh;
            var g = t.T("SSH and lines");
            if (h.V2) { it.Add(new(g, "ip ssh version 2", Pattern: "(?m)^ip ssh version 2\\s*$")); }
            if (h.Timeout.Length > 0) { var v = ToInt(h.Timeout); it.Add(new(g, Ci.F("ip ssh time-out {0}", v), Pattern: Ci.F("(?m)^ip ssh time-out {0}\\s*$", v), IsDefault: v == 120)); }
            if (h.Retries.Length > 0) { var v = ToInt(h.Retries); it.Add(new(g, Ci.F("ip ssh authentication-retries {0}", v), Pattern: Ci.F("(?m)^ip ssh authentication-retries {0}\\s*$", v), IsDefault: v == 3)); }
            var aclOk = true;
            if (h.VtyAcl.Length > 0 && hasRun)
            {
                aclOk = AclExists(run, h.VtyAcl);
                if (!aclOk)
                {
                    it.Add(new(g, Ci.F("access-class {0} in (line vty 0 15)", h.VtyAcl), "warning", Result: "FAIL", Detail: t.T("The ACL does not exist on this switch: it was not applied. Create it first (additional commands) and push again.")));
                }
            }
            if (h.VtyTimeout.Length > 0) { var v = ToInt(h.VtyTimeout); it.Add(new(g, Ci.F("exec-timeout {0} 0", v), "block", Ci.F("^exec-timeout {0} 0$", v), Parent: "line vty 0 15", Block: "^line vty ", IsDefault: v == 10)); }
            if (h.OnlySsh) { it.Add(new(g, "transport input ssh", "block", "^transport input ssh$", Default: xe, Parent: "line vty 0 15", Block: "^line vty ")); }
            if (h.VtyAcl.Length > 0 && aclOk) { it.Add(new(g, Ci.F("access-class {0} in", h.VtyAcl), "block", Ci.F("^access-class {0} in\\b", Esc(h.VtyAcl)), Parent: "line vty 0 15", Block: "^line vty ")); }
            if (h.VtyAcl.Length > 0 && !hasRun) { it.Add(new(g, Ci.F(t.T("! access-class {0} is only applied on switches where the ACL already exists"), h.VtyAcl), "note")); }
            if (h.WithTimeout.Length > 0) { var v = ToInt(h.WithTimeout); it.Add(new(g, Ci.F("exec-timeout {0} 0", v), "block", Ci.F("^exec-timeout {0} 0$", v), Parent: "line con 0", Block: "^line con 0", IsDefault: v == 10)); }
        }

        if (p.Services.Apply)
        {
            var v = p.Services;
            var g = t.T("Services");
            void Sv(string l) => it.Add(new(g, l, Pattern: Exact(l)));
            void No(string l) => it.Add(new(g, "no " + l, "absent", Exact(l)));
            if (v.Encrypt) { Sv("service password-encryption"); }
            if (v.Timestamps)
            {
                Sv("service timestamps debug datetime msec localtime show-timezone");
                Sv("service timestamps log datetime msec localtime show-timezone");
            }
            if (v.NoHttp)
            {
                No("ip http server");
                No("ip http secure-server");
            }
            if (v.NoPad) { No("service pad"); }
            if (v.Keepalives)
            {
                Sv("service tcp-keepalives-in");
                Sv("service tcp-keepalives-out");
            }
            if (v.NoSourceRoute) { No("ip source-route"); }
            if (v.LoginLog)
            {
                Sv("login on-failure log");
                Sv("login on-success log");
            }
        }

        if (p.Stp.Apply)
        {
            var g = t.T("Spanning-tree");
            it.Add(new(g, "spanning-tree mode " + p.Stp.Mode, "show", Ci.F("(?i)Switch is in {0} mode", Esc(p.Stp.Mode)), Command: "show spanning-tree summary | include mode"));
            if (p.Stp.Loopguard) { it.Add(new(g, "spanning-tree loopguard default", Pattern: Exact("spanning-tree loopguard default"))); }
        }

        if (p.User.Apply)
        {
            var us = p.User;
            var g = t.T("Local user / enable");
            if (us.Name.Length > 0) { it.Add(new(g, Ci.F("username {0} privilege 15 secret {1}", us.Name, us.Password), Pattern: Ci.F("(?m)^username {0} privilege 15 ", Esc(us.Name)))); }
            if (us.Enable.Length > 0) { it.Add(new(g, "enable secret " + us.Enable, Pattern: "(?m)^enable secret ")); }
        }

        if (p.Extra.Apply)
        {
            var g = t.T("Additional commands");
            foreach (var b in FilterExtra(p.Extra.Text, t, masker, extraAllowed).Blocks)
            {
                if (b.Children.Count == 0) { it.Add(new(g, b.Header, "none")); }
                foreach (var h in b.Children) { it.Add(new(g, h.Trim(), "none", Parent: b.Header)); }
            }
        }
        return it;
    }

    private static bool AclExists(string running, string acl) =>
        Ci.IsMatch(running, Ci.F("(?m)^ip access-list (standard|extended) {0}\\s*$", Esc(acl))) || Ci.IsMatch(running, Ci.F("(?m)^access-list {0}\\s", Esc(acl)));

    private sealed class PushBlock(string group, ConfigBlock block)
    {
        public string Group { get; } = group;
        public ConfigBlock Block { get; } = block;
    }

    /// <summary>Items -> blocks to send (notes and warnings are not sent).</summary>
    private static List<PushBlock> ToBlocks(IEnumerable<PushItem> items)
    {
        var blocks = new List<PushBlock>();
        PushBlock? current = null;
        foreach (var i in items)
        {
            if (i.Verif is "note" or "warning")
            {
                continue;
            }
            if (i.IsBanner)
            {
                blocks.Add(new PushBlock(i.Group, new ConfigBlock("banner " + i.BannerType, isBanner: true, bannerText: i.BannerText, bannerType: i.BannerType)));
                current = null;
                continue;
            }
            if (i.Parent.Length > 0)
            {
                if (current is null || !Ci.Eq(current.Block.Header, i.Parent))
                {
                    current = new PushBlock(i.Group, new ConfigBlock(i.Parent));
                    blocks.Add(current);
                }
                current.Block.Children.Add(" " + i.Line);
                continue;
            }
            blocks.Add(new PushBlock(i.Group, new ConfigBlock(i.Line)));
            current = null;
        }
        foreach (var b in blocks.Where(b => b.Block.IsBanner))
        {
            var text = string.Join('\n', b.Block.BannerText);
            b.Block.Delimiter = Delimiters.FirstOrDefault(c => !text.Contains(c, StringComparison.Ordinal)) ?? "#";
        }
        return blocks;
    }

    /// <summary>Blocks to send to one switch, in order.</summary>
    public static IReadOnlyList<ConfigBlock> Blocks(IEnumerable<PushItem> items) => ToBlocks(items).Select(b => b.Block).ToList();

    /// <summary>What is sent to one platform, as the preview shows it (already masked).</summary>
    public static string PlatformText(IEnumerable<PushItem> items, bool save, Translator t, SecretMasker masker)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(masker);
        var list = items.ToList();
        var sb = new StringBuilder();
        Line(sb, "configure terminal");
        var group = string.Empty;
        foreach (var pb in ToBlocks(list))
        {
            var b = pb.Block;
            if (!Ci.Eq(pb.Group, group))
            {
                group = pb.Group;
                Line(sb, "! --- " + group);
            }
            if (b.IsBanner)
            {
                Line(sb, Ci.F("banner {0} {1}", b.BannerType, b.Delimiter));
                foreach (var x in b.BannerText) { Line(sb, x); }
                Line(sb, b.Delimiter);
                continue;
            }
            Line(sb, b.Header);
            foreach (var h in b.Children) { Line(sb, h); }
        }
        Line(sb, "end");
        if (save)
        {
            Line(sb, t.T("write memory      (only if there were no failures, unless you check save with errors)"));
        }
        var notes = list.Where(i => i.Verif == "note").ToList();
        if (notes.Count > 0)
        {
            Line(sb, string.Empty);
            Line(sb, t.T("Decided on each switch, with its running-config:"));
            foreach (var n in notes) { Line(sb, "  " + Ci.Replace(n.Line, "^!\\s*", string.Empty)); }
        }
        return masker.Protect(sb.ToString());
    }

    /// <summary>Text of the preview tab (already masked: passwords and communities as ********).</summary>
    public static string Preview(PushParameters p, PushRunOptions opt, Translator t, SecretMasker masker, IEnumerable<string>? extraAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(masker);
        var sb = new StringBuilder();
        Line(sb, t.T("PUSH PREVIEW (passwords and communities are shown as ********)"));
        Line(sb, string.Empty);
        Line(sb, Ci.F(t.T("Connection : {0}"), opt.ConnectionText));
        if (opt.Targets is not null)
        {
            Line(sb, Ci.F(t.T("Switches   : {0}"), opt.Targets.Count));
            foreach (var d in opt.Targets) { Line(sb, Ci.F("           {0} ({1}{2})", d.Name, d.IP, d.Port != 22 ? ":" + d.Port.ToString(CultureInfo.InvariantCulture) : string.Empty)); }
        }
        Line(sb, Ci.F(t.T("Groups     : {0}"), string.Join(", ", Groups(p, t))));
        Line(sb, Ci.F(t.T("Save       : {0}"), opt.Save ? (opt.SaveWithErrors ? t.T("yes, even with failures") : t.T("yes, only if there were no failures")) : t.T("no (stays only in the running-config)")));
        Line(sb, t.T("Before changing anything, each switch's running-config is saved in its evidence folder."));
        var tIos = PlatformText(Lines(p, "IOS", null, t, masker, extraAllowed), opt.Save, t, masker);
        var tXe = PlatformText(Lines(p, "IOS-XE", null, t, masker, extraAllowed), opt.Save, t, masker);
        Line(sb, string.Empty);
        if (string.Equals(tIos, tXe, StringComparison.Ordinal))
        {
            Line(sb, t.T("---- Classic IOS (2960 / 2960-X / 2960-XR) and IOS-XE (9200): same lines ----"));
            sb.Append(tIos);
        }
        else
        {
            Line(sb, t.T("---- Classic IOS (Catalyst 2960 / 2960-X / 2960-XR) ----"));
            sb.Append(tIos);
            Line(sb, string.Empty);
            Line(sb, t.T("---- IOS-XE (Catalyst 9200 / 9200L) ----"));
            sb.Append(tXe);
        }
        if (p.Extra.Apply)
        {
            var ex = FilterExtra(p.Extra.Text, t, masker, extraAllowed);
            if (ex.Report.Count > 0)
            {
                Line(sb, string.Empty);
                Line(sb, t.T("---- Additional commands that are NOT sent ----"));
                foreach (var r in ex.Report)
                {
                    Line(sb, "  " + r.Line);
                    Line(sb, "      " + r.Reason);
                }
            }
        }
        var risks = Risks(p, t);
        if (risks.Count > 0)
        {
            Line(sb, string.Empty);
            Line(sb, t.T("---- Before pushing, consider ----"));
            foreach (var r in risks) { Line(sb, "- " + r); }
        }
        return masker.Protect(sb.ToString());
    }

    /// <summary>Warnings shown before pushing.</summary>
    public static IReadOnlyList<string> Risks(PushParameters p, Translator t)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(t);
        var r = new List<string>();
        if (p.Lldp.Apply && (p.Lldp.Lldp == "Disable" || p.Lldp.Cdp == "Disable"))
        {
            r.Add(t.T("IP phones, APs and some devices get the voice VLAN or PoE power via CDP / LLDP-MED: disabling them may affect them. The neighbor view is also lost."));
        }
        if (p.Vtp.Apply && p.Vtp.Mode is "server" or "client")
        {
            r.Add(Ci.F(t.T("VTP {0}: if a switch with a higher revision number joins the domain, it can DELETE or REPLACE the VLANs of every switch in the domain (classic VTP incident). Recommended: transparent."), p.Vtp.Mode));
        }
        if (p.Vtp.Apply && p.Vtp.Domain.Length > 0)
        {
            r.Add(t.T("Changing the VTP domain resets the revision number; in client/server mode the switch stops syncing with the previous domain."));
        }
        if (p.Snmp.Apply && p.Snmp.RemoveOthers) { r.Add(t.T("SNMP: the communities and trap destinations not listed here are deleted. Monitoring that uses another community will stop receiving data.")); }
        if (p.Ntp.Apply && p.Ntp.RemoveOthers) { r.Add(t.T("NTP: the servers not listed here are deleted.")); }
        if (p.Syslog.Apply && p.Syslog.RemoveOthers) { r.Add(t.T("Syslog: the servers not listed here are deleted.")); }
        if (p.Ssh.Apply && p.Ssh.OnlySsh)
        {
            r.Add(t.T("VTY SSH only: telnet is disabled. Switches without RSA keys or without \"ip ssh\" would lose remote access (the SSH sweep already confirms SSH works on the ones it connects to)."));
        }
        if (p.Ssh.Apply && p.Ssh.VtyAcl.Length > 0)
        {
            r.Add(Ci.F(t.T("access-class {0} on the VTYs: if the ACL does not permit the IP of your computer, your VPN or the backup server, remote access is lost (the current session continues). Check the ACL first."), p.Ssh.VtyAcl));
        }
        if (p.Services.Apply && p.Services.NoHttp) { r.Add(t.T("No HTTP/HTTPS server: on the 9200 the WebUI is turned off.")); }
        if (p.Stp.Apply)
        {
            r.Add(Ci.F(t.T("Changing the spanning-tree mode to {0} restarts STP on all VLANs: 1 to 50 second outages on each switch. Do it in a maintenance window and starting with the root."), p.Stp.Mode));
        }
        if (p.User.Apply) { r.Add(t.T("The user password and/or enable is changed on all switches. Record it in the password vault before saving.")); }
        if (p.Extra.Apply && Ci.IsMatch(p.Extra.Text, "(?im)^\\s*(no\\s+)?(line\\s+vty|aaa\\s|ip access-list|access-list|interface\\s+vlan|ip ssh|username\\s)"))
        {
            r.Add(t.T("The additional commands touch remote access (vty, AAA, ACL, SVI, SSH or users): review them twice, a mistake can lock you out of the switches."));
        }
        return r;
    }

    private static void Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
