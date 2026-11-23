namespace Rackside.Core.Push;

/// <summary>
/// What the operator wants to push to every switch, grouped like tab 2 of "Configure switches". List
/// values are internal codes ("No", "Enable", "transparent"...).
/// </summary>
public sealed class PushParameters
{
    public LldpGroup Lldp { get; init; } = new();
    public BannerGroup Banner { get; init; } = new();
    public VtpGroup Vtp { get; init; } = new();
    public SnmpGroup Snmp { get; init; } = new();
    public NtpGroup Ntp { get; init; } = new();
    public SyslogGroup Syslog { get; init; } = new();
    public DnsGroup Dns { get; init; } = new();
    public SshGroup Ssh { get; init; } = new();
    public ServicesGroup Services { get; init; } = new();
    public StpGroup Stp { get; init; } = new();
    public UserGroup User { get; init; } = new();
    public ExtraGroup Extra { get; init; } = new();

    /// <summary>Every secret the parameters carry (to register them in the masker).</summary>
    public IEnumerable<string> Secrets => [Vtp.Password, Snmp.Community, Snmp.V3Auth, Snmp.V3Priv, User.Password, User.Enable];

    /// <summary>"a, b  c;d" -> a b c d.</summary>
    public static IReadOnlyList<string> SplitList(string? text) =>
        Ci.Split(text, "[,;\\s]+").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    public sealed class LldpGroup { public bool Apply { get; set; } public string Lldp { get; set; } = "No"; public string Cdp { get; set; } = "No"; }
    public sealed class BannerGroup { public bool ApplyLogin { get; set; } public string Login { get; set; } = string.Empty; public bool ApplyMotd { get; set; } public string Motd { get; set; } = string.Empty; }
    public sealed class VtpGroup { public bool Apply { get; set; } public string Mode { get; set; } = "transparent"; public string Domain { get; set; } = string.Empty; public string Version { get; set; } = "No"; public string Password { get; set; } = string.Empty; }

    public sealed class SnmpGroup
    {
        public bool Apply { get; set; }
        public string Location { get; set; } = string.Empty;
        public string Contact { get; set; } = string.Empty;
        public string Community { get; set; } = string.Empty;
        public string Acl { get; set; } = string.Empty;
        public IReadOnlyList<string> Traps { get; set; } = [];
        public bool EnableTraps { get; set; }
        public bool RemoveOthers { get; set; }
        public string V3User { get; set; } = string.Empty;
        public string V3Group { get; set; } = string.Empty;
        public string V3Auth { get; set; } = string.Empty;
        public string V3Priv { get; set; } = string.Empty;
    }

    public sealed class NtpGroup
    {
        public bool Apply { get; set; }
        public IReadOnlyList<string> Servers { get; set; } = [];
        public string Source { get; set; } = string.Empty;
        public bool RemoveOthers { get; set; }
        public string Zone { get; set; } = string.Empty;
        public string Offset { get; set; } = string.Empty;
        public string Summer { get; set; } = string.Empty;
    }

    public sealed class SyslogGroup
    {
        public bool Apply { get; set; }
        public IReadOnlyList<string> Servers { get; set; } = [];
        public string Level { get; set; } = "No";
        public string Source { get; set; } = string.Empty;
        public string Buffer { get; set; } = string.Empty;
        public bool RemoveOthers { get; set; }
    }

    public sealed class DnsGroup { public bool Apply { get; set; } public string Domain { get; set; } = string.Empty; public IReadOnlyList<string> Servers { get; set; } = []; public bool NoLookup { get; set; } }

    public sealed class SshGroup
    {
        public bool Apply { get; set; }
        public bool V2 { get; set; }
        public string Timeout { get; set; } = string.Empty;
        public string Retries { get; set; } = string.Empty;
        public string VtyTimeout { get; set; } = string.Empty;
        public bool OnlySsh { get; set; }
        public string VtyAcl { get; set; } = string.Empty;
        /// <summary>Console exec-timeout.</summary>
        public string WithTimeout { get; set; } = string.Empty;
    }

    public sealed class ServicesGroup
    {
        public bool Apply { get; set; }
        public bool Encrypt { get; set; }
        public bool Timestamps { get; set; }
        public bool NoHttp { get; set; }
        public bool NoPad { get; set; }
        public bool Keepalives { get; set; }
        public bool NoSourceRoute { get; set; }
        public bool LoginLog { get; set; }
    }

    public sealed class StpGroup { public bool Apply { get; set; } public string Mode { get; set; } = "rapid-pvst"; public bool Loopguard { get; set; } }

    public sealed class UserGroup
    {
        public bool Apply { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Password2 { get; set; } = string.Empty;
        public string Enable { get; set; } = string.Empty;
        public string Enable2 { get; set; } = string.Empty;
    }

    public sealed class ExtraGroup { public bool Apply { get; set; } public string Text { get; set; } = string.Empty; }
}

/// <summary>
/// A line to send and how to check it afterwards (Verif): regex, absent, block, show, hidden, none, and
/// two kinds that are not sent: note (decided on each switch) and warning (result calculated there).
/// </summary>
public sealed record PushItem(string Group, string Line, string Verif = "regex", string Pattern = "", bool Default = false, bool IsDefault = false,
    string Parent = "", string Command = "", string Block = "", string Result = "", string Detail = "",
    bool IsBanner = false, string BannerType = "", IReadOnlyList<string>? BannerText = null)
{
    /// <summary>Text that identifies the item in the record.</summary>
    public string Label => IsBanner ? "banner " + BannerType : Parent.Length > 0 ? Ci.F("[{0}] {1}", Parent, Line) : Line;
}
