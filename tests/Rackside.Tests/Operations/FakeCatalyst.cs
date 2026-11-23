using System.Text;
using System.Text.RegularExpressions;
using Rackside.Transport.Console;

namespace Rackside.Tests.Operations;

/// <summary>
/// An in-memory 2960 console for the restore. Echo, modes, interfaces that
/// exist, banners, ACLs with sequence numbers, a rejected "enable secret 9" and "login on-success log", RSA
/// keys and a syslog message in the middle of the apply.
/// </summary>
internal sealed class FakeCatalyst : IConsoleChannel
{
    private readonly StringBuilder output = new();
    private readonly object gate = new();
    private string input = string.Empty;
    private string banner = string.Empty;
    private int syslog;
    private string curAcl = string.Empty;
    private readonly Dictionary<string, Dictionary<string, string>> acl = [];

    public string Host { get; private set; } = "Switch";
    public string Mode { get; private set; } = "user";
    public List<string> Cfg { get; } = [];
    public required IReadOnlyList<string> Ifs { get; init; }
    public required string Ver { get; init; }

    public string ReadExisting()
    {
        lock (gate)
        {
            var s = output.ToString();
            output.Clear();
            return s;
        }
    }

    private void P(string s)
    {
        lock (gate)
        {
            output.Append(s);
        }
    }

    private string Pr() => Mode switch
    {
        "user" => "\r\n" + Host + ">",
        "exec" => "\r\n" + Host + "#",
        _ => "\r\n" + Host + "(" + Mode + ")#",
    };

    public void Write(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                var l = input;
                input = string.Empty;
                Line(l);
            }
            else
            {
                input += ch;
                P(ch.ToString());
            }
        }
    }

    private static bool M(string l, string pattern, out Match m)
    {
        m = Regex.Match(l, pattern, RegexOptions.IgnoreCase);
        return m.Success;
    }

    private void Line(string l)
    {
        if (banner.Length > 0)
        {
            if (l == banner) { banner = string.Empty; P(Pr()); } else { P("\r\n"); }
            return;
        }
        var o = string.Empty;
        if (Mode == "user")
        {
            if (l == "enable") { Mode = "exec"; }
            P(Pr());
            return;
        }
        if (Mode == "exec")
        {
            if (M(l, "^terminal", out _)) { }
            else if (l == "show version") { o = Ver; }
            else if (l == "show ip interface brief")
            {
                o = "Interface              IP-Address      OK? Method Status                Protocol\r\n" + string.Join("\r\n", Ifs.Select(i => $"{i,-22} unassigned      YES unset  down                  down"));
            }
            else if (M(l, "^show ip interface brief \\| include (\\S+)", out var mb))
            {
                var n = mb.Groups[1].Value;
                var ip = "unassigned";
                foreach (var c in Cfg)
                {
                    if (M(c, "^IP (\\S+) (\\S+)$", out var mc) && mc.Groups[1].Value == n) { ip = mc.Groups[2].Value; }
                }
                o = $"{n,-22} {ip,-15} YES manual up                    up";
            }
            else if (l == "dir") { o = "Directory of flash:/\r\n\r\n    3  drwx         512   Mar 1 1993 00:08:51 +00:00  c2960x-universalk9-mz.152-7.E3"; }
            else if (l == "configure terminal") { Mode = "config"; o = "Enter configuration commands, one per line.  End with CNTL/Z."; }
            else if (M(l, "^show running-config \\| include", out _)) { o = string.Join("\r\n", Cfg.Where(c => M(c, "^(hostname|username|enable|ip domain)", out _))); }
            else if (l == "write memory") { o = "Building configuration...\r\n[OK]"; }
            else if (l.Length == 0) { }
            else { o = "% Invalid input detected at '^' marker."; }
            if (o.Length > 0) { P("\r\n" + o); }
            P(Pr());
            return;
        }
        // configuration modes
        if (l == "end") { Mode = "exec"; P(Pr()); return; }
        if (l == "exit") { Mode = Mode == "config" ? "exec" : "config"; P(Pr()); return; }
        if (l.Length == 0) { P(Pr()); return; }
        if (M(l, "^no ip access-list (extended|standard) (\\S+)$", out var ma)) { acl.Remove(ma.Groups[2].Value); Mode = "config"; P(Pr()); return; }
        if (M(l, "^ip access-list (extended|standard) (\\S+)$", out ma))
        {
            curAcl = ma.Groups[2].Value;
            acl.TryAdd(curAcl, []);
            Cfg.Add(l);
            Mode = "config-ext-nacl";
            P(Pr());
            return;
        }
        if (Mode == "config-ext-nacl")
        {
            if (M(l, "^(\\d+)\\s+remark", out _)) { P("\r\n                    ^\r\n% Invalid input detected at '^' marker." + Pr()); return; }
            if (M(l, "^(\\d+)\\s+(permit|deny)", out var ms))
            {
                if (acl[curAcl].ContainsKey(ms.Groups[1].Value)) { P("\r\n% Duplicate sequence number" + Pr()); return; }
                acl[curAcl][ms.Groups[1].Value] = l;
                P(Pr());
                return;
            }
            if (M(l, "^(remark|permit|deny)", out _)) { P(Pr()); return; }
        }
        var top = !(Mode != "config" && M(l, "^(switchport|spanning-tree portfast|spanning-tree bpduguard|description|shutdown|no shutdown|ip address|name|login|transport|stopbits|channel-group|power inline|storm-control|no ip address|exec-timeout|password|logging synchronous)", out _));
        if (!top)
        {
            if (M(l, "^ip address (\\S+) (\\S+)$", out _))
            {
                var cur = string.Empty;
                foreach (var c in Cfg)
                {
                    if (M(c, "^CURIF (\\S+)$", out var mc)) { cur = mc.Groups[1].Value; }
                }
                Cfg.Add($"IP {cur} {l.Split(' ')[2]}");
            }
            P(Pr());
            return;
        }
        if (M(l, "^hostname (\\S+)$", out var mh)) { Host = mh.Groups[1].Value; Cfg.Add(l); Mode = "config"; }
        else if (M(l, "^interface (\\S+)$", out var mi))
        {
            var n = mi.Groups[1].Value;
            if (M(n, "^(Vlan|Port-channel|Loopback)", out _) || Ifs.Contains(n, StringComparer.OrdinalIgnoreCase))
            {
                Mode = "config-if";
                Cfg.Add("CURIF " + n);
                if (n == "GigabitEthernet1/0/10") { syslog = 1; }
            }
            else { o = "                    ^\r\n% Invalid input detected at '^' marker."; }
        }
        else if (M(l, "^vlan \\d+$", out _)) { Mode = "config-vlan"; }
        else if (M(l, "^line ", out _)) { Mode = "config-line"; }
        else if (M(l, "^banner (\\S+) (\\S)$", out var mbn)) { banner = mbn.Groups[2].Value; P($"\r\nEnter TEXT message.  End with the character '{mbn.Groups[2].Value}'.\r\n"); return; }
        else if (M(l, "^enable secret 9 ", out _)) { o = "% Invalid encrypted password: 9 not supported"; }
        else if (M(l, "^username (\\S+) privilege 15 secret (?!9 )", out _)) { Cfg.Add(l); }
        else if (M(l, "^login on-success log$", out _)) { o = "                ^\r\n% Invalid input detected at '^' marker."; }
        else if (M(l, "^crypto key generate rsa", out _))
        {
            Thread.Sleep(1500);
            o = $"The name for the keys will be: {Host}.lab.local\r\n% The key modulus size is 2048 bits\r\n% Generating 2048 bit RSA keys, keys will be non-exportable...\r\n[OK] (elapsed time was 1 seconds)";
        }
        else if (M(l, "^ip domain-name", out _)) { Cfg.Add(l); }
        var block = M(l, "^(interface|vlan|line) ", out _);
        Mode = (Mode is "config-if" or "config-vlan" or "config-line" && block) ? Mode : (block && o.Length == 0) ? Mode : "config";
        if (o.Length > 0) { P("\r\n" + o); }
        P(Pr());
        if (syslog == 1)
        {
            syslog = 2;
            P("\r\n*Mar  1 00:10:00.123: %LINK-3-UPDOWN: Interface GigabitEthernet1/0/10, changed state to down");
        }
    }

    public bool IsOpen => true;

    public bool IsSsh => false;

    public void Dispose()
    {
    }
}
