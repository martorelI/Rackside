using System.Text;
using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Core.Security;

namespace Rackside.Tests;

/// <summary>A valid parameter set (every group), used for push/preview.txt.</summary>
internal static class PushScenarios
{
    public static PushParameters Parameters() => new()
    {
        Lldp = { Apply = true, Lldp = "Enable", Cdp = "No" },
        Banner =
        {
            ApplyLogin = true,
            Login = new string('*', 20) + "\r\nRESTRICTED ACCESS: authorized personnel only.\r\nActivity is logged.\r\n" + new string('*', 20),
            ApplyMotd = true,
            Motd = string.Empty,
        },
        Vtp = { Apply = true, Mode = "transparent", Domain = "LAB", Version = "2", Password = "VtpPass123" },
        Snmp =
        {
            Apply = true, Location = "Building 2 IDF1", Contact = "IT Support ext 1234", Community = "MonitorRO2026", Acl = "MANAGEMENT",
            Traps = PushParameters.SplitList("10.0.0.50"), EnableTraps = true, RemoveOthers = true,
            V3User = "monitor", V3Group = "GRPRO", V3Auth = "AuthPass123", V3Priv = "PrivPass123",
        },
        Ntp = { Apply = true, Servers = PushParameters.SplitList("10.0.0.5, 10.0.0.6"), RemoveOthers = true, Zone = "PST", Offset = "-8", Summer = "PDT" },
        Syslog = { Apply = true, Servers = PushParameters.SplitList("10.0.0.8"), Level = "warnings", Buffer = "64000", RemoveOthers = true },
        Dns = { Apply = true, Domain = "lab.local", Servers = PushParameters.SplitList("10.0.0.2 10.0.0.3"), NoLookup = true },
        Ssh = { Apply = true, V2 = true, Timeout = "60", Retries = "3", VtyTimeout = "15", OnlySsh = true, VtyAcl = "MANAGEMENT", WithTimeout = "10" },
        Services = { Apply = true, Encrypt = true, Timestamps = true, NoHttp = true, NoPad = true, Keepalives = true, NoSourceRoute = true, LoginLog = true },
        Stp = { Apply = true, Mode = "rapid-pvst", Loopguard = true },
        User = { Apply = true, Name = "support", Password = "Supp0rt!2026", Password2 = "Supp0rt!2026", Enable = "NewEn4ble!", Enable2 = "NewEn4ble!" },
        Extra =
        {
            Apply = true,
            Text = "errdisable recovery cause all\r\nerrdisable recovery interval 300\r\nhostname OTHER\r\ninterface Vlan1\r\n ip address 1.1.1.1 255.0.0.0\r\ndo reload\r\nevent manager applet X\r\nudld enable",
        },
    };

    /// <summary>An invalid parameter set: every group breaks at least one rule.</summary>
    public static PushParameters Invalid() => new()
    {
        Lldp = { Apply = true, Lldp = "No", Cdp = "No" },
        Banner = { ApplyLogin = true, Login = "Every delimiter: # $ % ~ @ & | ^ *\r\nBell\a", ApplyMotd = true, Motd = new string('x', 2001) },
        Vtp = { Apply = true, Mode = "server", Domain = string.Empty, Version = "3", Password = "short" },
        Snmp =
        {
            Apply = true, Location = "Room ?", Community = "Public", Acl = "bad acl!", Traps = PushParameters.SplitList("10.0.0.300, 10.0.0.51"),
            RemoveOthers = true, V3User = "monitor", V3Auth = "short", V3Priv = "has space1",
        },
        Ntp = { Apply = true, Servers = PushParameters.SplitList("ntp.local"), Source = "bad if", Zone = "TOOLONGZONE", Offset = "20", Summer = "P1" },
        Syslog = { Apply = true, Level = "No", Source = "Vlan10", Buffer = "100", RemoveOthers = true },
        Dns = { Apply = true, Domain = "-bad.local", Servers = PushParameters.SplitList("10.0.0.1 10.0.0.2 10.0.0.3 10.0.0.4 10.0.0.5 10.0.0.6 10.0.0.7") },
        Ssh = { Apply = true, Timeout = "200", Retries = "x", VtyTimeout = "0", VtyAcl = "a b", WithTimeout = "-1" },
        Services = { Apply = true },
        Stp = { Apply = true, Mode = "pvst" },
        User = { Apply = true, Name = "bad name", Password = "abc", Password2 = "abd", Enable = "short", Enable2 = "other" },
        Extra = { Apply = true, Text = "  \r\n " },
    };

    internal static SecretMasker Masker(PushParameters p)
    {
        var masker = new SecretMasker();
        masker.Register([.. p.Secrets, "NewPass123", "Enable123"]);
        return masker;
    }

    public static string Validation()
    {
        // The scenario registers the valid set first and then the invalid one (same masker).
        var masker = Masker(Parameters());
        var p = Invalid();
        masker.Register([.. p.Secrets]);
        var t = Translator.English;
        return masker.Protect(PushPlanner.ValidationMessage(PushPlanner.Validate(p, t), t));
    }

    /// <summary>Items with their check, then the text sent.</summary>
    public static string Lines(string platform, bool withAcl)
    {
        var p = Parameters();
        var masker = Masker(p);
        var t = Translator.English;
        var running = File.ReadAllText(RepoPaths.Combine("tests", "fixtures", "running", "push_running.txt"));
        if (!withAcl)
        {
            running = Ci.Replace(Ci.Replace(running, "(?m)^ip access-list standard MANAGEMENT\\r?\\n( .*\\r?\\n)*", string.Empty), "(?m)^access-list 15 .*\\r?\\n", string.Empty);
        }
        var items = PushPlanner.Lines(p, platform, running, t, masker);
        var sb = new StringBuilder();
        foreach (var i in items)
        {
            sb.Append(Ci.F("{0} | {1} | {2} | default={3} isdefault={4} | {5} | {6} | {7} | {8}", i.Verif, i.Label, i.Pattern, i.Default ? "True" : "False", i.IsDefault ? "True" : "False", i.Command, i.Block, i.Result, i.Detail)).Append('\n');
        }
        sb.Append("----\n");
        sb.Append(PushPlanner.PlatformText(items, false, t, masker));
        return masker.Protect(sb.ToString());
    }

    public static string Preview()
    {
        var p = Parameters();
        var masker = Masker(p);
        var t = Translator.English;
        var opt = new PushRunOptions(t.T("SSH (sweep of the checked switches)"), Save: true, SaveWithErrors: false);
        return PushPlanner.Preview(p, opt, t, masker);
    }
}
