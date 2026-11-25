using System.Globalization;
using System.Text;
using Rackside.Core;
using Rackside.Core.Configure;
using Rackside.Core.Localization;
using Rackside.Core.Locate;
using Rackside.Core.Security;
using Rackside.Transport.Console;
using Rackside.Transport.Ssh;

namespace Rackside.Operations.Locate;

/// <summary>Where "Locate IP" writes its evidence (Inventory/Locate) and the SSH fingerprints file.</summary>
public sealed record LocatePaths(string Folder, string Fingerprints);

/// <summary>What to locate and how.</summary>
public sealed record LocateRequest
{
    /// <summary>IPv4 address or MAC address (aabb.ccdd.eeff, aa:bb:cc:dd:ee:ff, aa-bb-cc-dd-ee-ff).</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>Ping from the start switch the addresses missing from its ARP (the target and the switches).</summary>
    public bool RefreshArp { get; init; }

    /// <summary>Accept the SSH fingerprint of switches with none saved without asking.</summary>
    public bool TrustNew { get; init; }
}

public enum LocateOutcome
{
    Found,
    IsSwitch,

    /// <summary>The trail ends on an uplink (trunk or many MACs) with no switch of the list behind it.</summary>
    BehindTrunk,
    NotInArp,
    NotInMacTable,
    Incomplete,
}

public static class LocateOutcomeExtensions
{
    /// <summary>Internal state text (key of the translation table).</summary>
    public static string Code(this LocateOutcome o) => o switch
    {
        LocateOutcome.Found => "FOUND",
        LocateOutcome.IsSwitch => "SWITCH OF THE LIST",
        LocateOutcome.BehindTrunk => "BEHIND A TRUNK",
        LocateOutcome.NotInArp => "NOT IN ARP",
        LocateOutcome.NotInMacTable => "NOT IN MAC TABLE",
        _ => "INCOMPLETE",
    };
}

/// <summary>One switch of the path and what it said.</summary>
public sealed record LocateHop(string Switch, string IP, string Port, string Vlan, string Detail);

/// <summary>Where the address is (or how far the trace got).</summary>
public sealed class LocateResult
{
    public string Target { get; init; } = string.Empty;

    public string Ip { get; set; } = string.Empty;

    public string Mac { get; set; } = string.Empty;

    public string Vlan { get; set; } = string.Empty;

    /// <summary>The switch whose ARP was used.</summary>
    public string ArpSwitch { get; set; } = string.Empty;

    public LocateOutcome Outcome { get; set; } = LocateOutcome.Incomplete;

    public string Switch { get; set; } = string.Empty;

    public string SwitchIP { get; set; } = string.Empty;

    public string Port { get; set; } = string.Empty;

    /// <summary>"show interfaces &lt;port&gt; status" line of the final port.</summary>
    public string PortStatus { get; set; } = string.Empty;

    public int MacsOnPort { get; set; }

    public string Detail { get; set; } = string.Empty;

    public List<string> Notes { get; } = [];

    public List<LocateHop> Hops { get; } = [];

    public string Folder { get; set; } = string.Empty;

    /// <summary>The result as text (result.txt).</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// "Locate IP": from the ARP of a start switch (the gateway of the VLAN) to the MAC, and then from switch to
/// switch along the MAC tables until the port where it is connected. Layer 2 only, read only: it sends
/// nothing but "show" commands (and, if asked, two pings from the start switch).
/// </summary>
public sealed class LocateOperation(LocatePaths paths, Translator t, SecretMasker masker, IOperatorUi ui, CancellationToken cancel = default)
{
    private static readonly string[] AllowedCommands =
    [
        "\\Ashow ip arp\\z",
        "\\Ashow mac address-table\\z",
        "\\Ashow interfaces [A-Za-z][A-Za-z0-9/\\.\\-]* status\\z",
        "\\Aping \\d{1,3}(\\.\\d{1,3}){3} repeat 2 timeout 1\\z",
    ];

    /// <summary>Stop button of the current run.</summary>
    public CancellationToken Cancellation { get; set; } = cancel;

    public OperationLog Log { get; } = new(t, masker);

    /// <summary>The only commands this operation may send after logging in (read only).</summary>
    public static bool IsReadOnly(string command) => AllowedCommands.Any(p => Ci.IsMatch(command, p));

    private static string Now(string format) => DateTime.Now.ToString(format, CultureInfo.InvariantCulture);

    private string Run(SwitchConnection c, string command, int timeout)
    {
        if (!IsReadOnly(command))
        {
            throw new InvalidOperationException("Locate only sends read-only commands: " + command);
        }
        var output = c.Session.RunCommand(command, timeout);
        if (LocateParser.IsError(output))
        {
            throw new SwitchException(Ci.F(t.T("{0} did not accept \"{1}\" (is it a Cisco switch?)."), c.Hostname, command));
        }
        return output;
    }

    private SwitchConnection Connect(LocateRequest req, Credentials cred, ConfTarget target, string folder)
    {
        if (!SwitchConnection.TcpOpen(target.IP, target.Port, 4000))
        {
            throw new SwitchException(Ci.F(t.T("No response at {0}:{1} (powered off, no network, ACL or SSH disabled)."), target.IP, target.Port));
        }
        var request = new FingerprintRequest(target.IP, target.Port) { TrustFirstUse = req.TrustNew, Hostname = target.Name };
        return SwitchConnection.OverSsh(request, cred, Path.Combine(folder, Inventory.SafeName(Ci.F("{0}_{1}", target.Name, target.IP))),
            new FingerprintStore(paths.Fingerprints), t, masker, ui, Log, Cancellation);
    }

    /// <summary>Errors that end the connection to one switch (and not the whole application).</summary>
    private static bool IsOperational(Exception ex) =>
        ex is SwitchException or OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException
            or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or FormatException;

    /// <summary>
    /// Locates <see cref="LocateRequest.Target"/> starting at <paramref name="start"/> (it should be the gateway of the
    /// VLAN: its ARP gives the MAC of the target and of the other switches of <paramref name="switches"/>).
    /// </summary>
    public LocateResult Locate(LocateRequest req, IReadOnlyList<ConfTarget> switches, ConfTarget start, Credentials cred)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(cred);
        var input = req.Target.Trim();
        var byIp = Validators.IsIPv4(input);
        var targetMac = string.Empty;
        if (!byIp && !MacAddress.TryNormalize(input, out targetMac))
        {
            throw new FormatException(t.T("Enter an IPv4 address (10.1.2.3) or a MAC address (aabb.ccdd.eeff, aa:bb:cc:dd:ee:ff or aa-bb-cc-dd-ee-ff)."));
        }
        if (switches.Count == 0)
        {
            throw new FormatException(t.T("The switch list is empty: add the switches in \"Configure switches\" (tab 1) and save the list."));
        }
        masker.Register(cred.Password, cred.Enable);

        var r = new LocateResult { Target = input, Ip = byIp ? input : string.Empty, Mac = targetMac };
        r.Folder = Path.Combine(paths.Folder, "LOCATE_" + Now("yyyyMMdd_HHmmss") + "_" + Inventory.SafeName(input));
        Directory.CreateDirectory(r.Folder);
        Log.Paths.Clear();
        Log.Paths.Add(Path.Combine(r.Folder, "log.txt"));
        Log.Info(Ci.F(t.T("===== Locate {0} starting at {1} ({2}), {3} switch(es) in the list ====="), input, start.Name, start.IP, switches.Count));

        SwitchConnection? c = null;
        try
        {
            ui.Status(Ci.F(t.T("Connecting to {0}..."), start.Name), 5);
            c = Connect(req, cred, start, r.Folder);
            Trace(req, cred, switches, start, c, r, out c);
        }
        catch (Exception ex) when (IsOperational(ex))
        {
            r.Outcome = LocateOutcome.Incomplete;
            r.Detail = Cancellation.IsCancellationRequested ? t.T("Stopped by the operator") : masker.Protect(ex.Message);
            Log.Error(r.Detail);
        }
        finally
        {
            c?.Dispose();
        }

        r.Text = Summary(r);
        try
        {
            File.WriteAllText(Path.Combine(r.Folder, "result.txt"), Ci.Replace(r.Text, "\\r?\\n", "\r\n"), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(Ci.F(t.T("Could not write {0}: {1}"), "result.txt", ex.Message));
        }
        var level = r.Outcome is LocateOutcome.Found or LocateOutcome.IsSwitch ? "OK" : "WARN";
        Log.Write(level, Ci.F(t.T("===== {0}: {1}"), t.T(r.Outcome.Code()), r.Detail));
        ui.Status(Ci.F("{0}: {1}", t.T(r.Outcome.Code()), r.Detail), 100);
        return r;
    }

    private (List<ArpEntry> Arp, List<KnownSwitch> Known, List<ConfTarget> Missing) ReadArp(SwitchConnection c, IReadOnlyList<ConfTarget> switches, ConfTarget start)
    {
        var arp = LocateParser.Arp(Run(c, "show ip arp", 120)).ToList();
        var known = new List<KnownSwitch>();
        var missing = new List<ConfTarget>();
        foreach (var s in switches.Where(s => !Ci.Eq(s.Name, start.Name)))
        {
            var e = arp.FirstOrDefault(a => a.Ip == s.IP);
            if (e is null)
            {
                missing.Add(s);
            }
            else
            {
                known.Add(new KnownSwitch(s.Name, e.Mac));
            }
        }
        return (arp, known, missing);
    }

    private void Trace(LocateRequest req, Credentials cred, IReadOnlyList<ConfTarget> switches, ConfTarget start, SwitchConnection first, LocateResult r, out SwitchConnection? open)
    {
        open = first;
        r.ArpSwitch = start.Name;
        ui.Status(Ci.F(t.T("{0}: reading the ARP table..."), start.Name), 10);
        var (arp, known, missing) = ReadArp(first, switches, start);
        var target = r.Ip.Length > 0 ? arp.FirstOrDefault(a => a.Ip == r.Ip) : arp.FirstOrDefault(a => a.Mac == r.Mac);
        var pinged = false;
        if (req.RefreshArp && ((r.Ip.Length > 0 && target is null) || missing.Count > 0))
        {
            var ips = new List<string>();
            if (r.Ip.Length > 0 && target is null)
            {
                ips.Add(r.Ip);
            }
            ips = ips.Concat(missing.Select(m => m.IP)).Distinct(StringComparer.Ordinal).ToList();
            Log.Info(Ci.F(t.T("{0}: pinging {1} to refresh the ARP table..."), start.Name, string.Join(", ", ips)));
            foreach (var ip in ips)
            {
                Run(first, Ci.F("ping {0} repeat 2 timeout 1", ip), 30);
            }
            pinged = r.Ip.Length > 0;
            (arp, known, missing) = ReadArp(first, switches, start);
            target = r.Ip.Length > 0 ? arp.FirstOrDefault(a => a.Ip == r.Ip) : arp.FirstOrDefault(a => a.Mac == r.Mac);
        }
        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Select(m => Ci.F("{0} ({1})", m.Name, m.IP)));
            Log.Warn(Ci.F(t.T("Management MAC unknown for {0}: not in the ARP table of {1}."), names, start.Name));
            r.Notes.Add(Ci.F(t.T("Management MAC unknown for {0} (not in the ARP table of {1}): a port toward them looks like the device's own port. \"Refresh the ARP\" pings them."), names, start.Name));
        }
        if (target is not null)
        {
            r.Mac = target.Mac;
            r.Ip = target.Ip;
            r.Vlan = LocateParser.VlanOf(target.Interface);
            Log.Ok(Ci.F(t.T("ARP of {0}: {1} -> MAC {2} ({3})"), start.Name, target.Ip, target.Mac, target.Interface));
        }
        else if (r.Ip.Length > 0)
        {
            r.Outcome = LocateOutcome.NotInArp;
            r.Detail = Ci.F(t.T("{0} is not in the ARP table of {1}: it is not on a VLAN routed by that switch, it has not talked recently or it is off. Check \"Refresh the ARP\" or start at the switch that is its gateway."), r.Ip, start.Name);
            return;
        }
        else
        {
            Log.Info(Ci.F(t.T("MAC {0} entered directly: it is not in the ARP table of {1} (the ARP is only used to know the switches)."), r.Mac, start.Name));
        }

        var self = known.FirstOrDefault(k => k.Mac == r.Mac);
        if (self is not null)
        {
            var sw = switches.First(s => Ci.Eq(s.Name, self.Name));
            r.Outcome = LocateOutcome.IsSwitch;
            r.Switch = sw.Name;
            r.SwitchIP = sw.IP;
            r.Detail = Ci.F(t.T("{0} is the management address of the switch {1} ({2})."), r.Ip.Length > 0 ? r.Ip : r.Mac, sw.Name, sw.IP);
            return;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.Name };
        var current = start;
        var conn = first;
        var pending = new List<ConfTarget>();
        (string Switch, string Port) lastUplink = (string.Empty, string.Empty);
        var hop = 0;
        while (hop++ <= switches.Count)
        {
            Cancellation.ThrowIfCancellationRequested();
            ui.Status(Ci.F(t.T("{0}: reading the MAC table..."), current.Name), Math.Min(90, 15 + (hop * 75 / Math.Max(1, switches.Count))));
            var table = LocateParser.MacTable(Run(conn, "show mac address-table", 180));
            var d = LocateTracer.Decide(table, r.Mac, known, visited, current.Name);
            if (d.Kind == HopKind.NotInTable && current == start && req.RefreshArp && r.Ip.Length > 0 && !pinged)
            {
                Log.Info(Ci.F(t.T("{0}: the MAC is not in the table; pinging {1} so it is learned again..."), start.Name, r.Ip));
                Run(conn, Ci.F("ping {0} repeat 2 timeout 1", r.Ip), 30);
                pinged = true;
                table = LocateParser.MacTable(Run(conn, "show mac address-table", 180));
                d = LocateTracer.Decide(table, r.Mac, known, visited, current.Name);
            }
            var status = d.Kind == HopKind.Edge ? PortStatus(conn, d.Port) : string.Empty;
            if (d.Kind == HopKind.Edge && LocateTracer.LooksLikeUplink(d.MacsOnPort, status))
            {
                // An uplink with no switch of the list learned on it: their management MACs may have aged out of this
                // table (about 5 minutes without traffic). Pinging them from here makes this switch learn them again.
                var absent = LocateTracer.Absent(table, known, visited, current.Name).Select(n => switches.First(s => Ci.Eq(s.Name, n))).ToList();
                if (absent.Count > 0)
                {
                    Log.Info(Ci.F(t.T("{0}: {1} looks like an uplink ({2} MACs) but no switch of the list is learned there; pinging the {3} switch(es) of the list missing from its MAC table so they are learned again..."),
                        current.Name, d.Port, d.MacsOnPort, absent.Count));
                    foreach (var s in absent)
                    {
                        Cancellation.ThrowIfCancellationRequested();
                        ui.Status(Ci.F(t.T("{0}: ping {1} ({2})..."), current.Name, s.Name, s.IP), null);
                        Run(conn, Ci.F("ping {0} repeat 2 timeout 1", s.IP), 30);
                    }
                    table = LocateParser.MacTable(Run(conn, "show mac address-table", 180));
                    d = LocateTracer.Decide(table, r.Mac, known, visited, current.Name);
                    status = d.Kind == HopKind.Edge ? PortStatus(conn, d.Port) : string.Empty;
                }
            }
            switch (d.Kind)
            {
                case HopKind.Edge when LocateTracer.LooksLikeUplink(d.MacsOnPort, status):
                {
                    var detail = Ci.F(t.T("MAC on {0}, VLAN {1}: {2} MAC(s) on the port and no switch of the list behind it"), d.Port, d.Vlan, d.MacsOnPort);
                    r.Hops.Add(new LocateHop(current.Name, current.IP, d.Port, d.Vlan, detail));
                    Log.Warn(Ci.F("{0}: {1}", current.Name, detail));
                    SetPlace(r, current, d, status);
                    r.Outcome = LocateOutcome.BehindTrunk;
                    r.Detail = Ci.F(t.T("{0} is behind port {1} of {2} ({3} MACs learned there{4}), but no switch of the list is learned on that port: the switch behind it is not in the list, does not answer ping or is managed out of band (its MAC is not in the data VLANs)."),
                        r.Ip.Length > 0 ? r.Ip : r.Mac, d.Port, current.Name, d.MacsOnPort, Ci.IsMatch(status, "\\strunk(\\s|$)") ? t.T(", trunk") : string.Empty);
                    return;
                }
                case HopKind.Edge:
                {
                    var detail = Ci.F(t.T("MAC on {0}, VLAN {1}: connected there ({2} MAC(s) on the port)"), d.Port, d.Vlan, d.MacsOnPort);
                    r.Hops.Add(new LocateHop(current.Name, current.IP, d.Port, d.Vlan, detail));
                    Log.Ok(Ci.F("{0}: {1}", current.Name, detail));
                    SetPlace(r, current, d, status);
                    r.Outcome = LocateOutcome.Found;
                    r.Detail = Ci.F(t.T("{0} is on {1} ({2}), port {3}, VLAN {4}."), r.Ip.Length > 0 ? r.Ip : r.Mac, current.Name, current.IP, d.Port, d.Vlan);
                    if (d.MacsOnPort > 1)
                    {
                        r.Notes.Add(Ci.F(t.T("{0} MACs are learned on {1}: there may be an IP phone with a PC, an access point, or a switch that is not in the list (or whose MAC is unknown)."), d.MacsOnPort, d.Port));
                    }
                    if (Ci.IsMatch(d.Port, "^Po"))
                    {
                        r.Notes.Add(Ci.F(t.T("{0} is a port-channel: the device is behind one of its member ports (show etherchannel summary)."), d.Port));
                    }
                    return;
                }
                case HopKind.Uplink:
                {
                    var detail = Ci.F(t.T("MAC on {0}, VLAN {1} -> behind it: {2}"), d.Port, d.Vlan, string.Join(", ", d.Neighbors));
                    r.Hops.Add(new LocateHop(current.Name, current.IP, d.Port, d.Vlan, detail));
                    Log.Info(Ci.F("{0}: {1}", current.Name, detail));
                    if (d.Candidates.Count == 0)
                    {
                        r.Outcome = LocateOutcome.Incomplete;
                        r.Detail = Ci.F(t.T("On {0} the MAC points back to switches already queried ({1}): the tables may be changing (a loop or a device that moves). Try again."), current.Name, string.Join(", ", d.Neighbors));
                        return;
                    }
                    lastUplink = (current.Name, d.Port);
                    pending = d.Candidates.Select(n => switches.First(s => Ci.Eq(s.Name, n))).ToList();
                    break;
                }
                default:
                {
                    var detail = t.T("The MAC is not in its table");
                    r.Hops.Add(new LocateHop(current.Name, current.IP, string.Empty, string.Empty, detail));
                    Log.Warn(Ci.F("{0}: {1}", current.Name, detail));
                    if (current == start)
                    {
                        r.Outcome = LocateOutcome.NotInMacTable;
                        r.Detail = Ci.F(t.T("MAC {0} is not in the MAC table of {1} (it ages out after about 5 minutes without traffic). Check \"Refresh the ARP\" so the pings generate traffic, and try again."), r.Mac, start.Name);
                        return;
                    }
                    break;
                }
            }

            conn.Dispose();
            open = null;
            SwitchConnection? next = null;
            while (next is null && pending.Count > 0)
            {
                var cand = pending[0];
                pending.RemoveAt(0);
                visited.Add(cand.Name);
                try
                {
                    ui.Status(Ci.F(t.T("Connecting to {0}..."), cand.Name), null);
                    next = Connect(req, cred, cand, r.Folder);
                    current = cand;
                }
                catch (Exception ex) when (IsOperational(ex) && ex is not OperationCanceledException)
                {
                    var why = Ci.F(t.T("Could not be queried: {0}"), masker.Protect(ex.Message));
                    r.Hops.Add(new LocateHop(cand.Name, cand.IP, string.Empty, string.Empty, why));
                    r.Notes.Add(Ci.F("{0}: {1}", cand.Name, why));
                    Log.Warn(Ci.F("{0}: {1}", cand.Name, why));
                }
            }
            if (next is null)
            {
                r.Outcome = LocateOutcome.Incomplete;
                r.Detail = Ci.F(t.T("The MAC is behind port {0} of {1}, but the switches behind it could not be queried or do not have it in their table."), lastUplink.Port, lastUplink.Switch);
                return;
            }
            conn = next;
            open = next;
        }
        r.Outcome = LocateOutcome.Incomplete;
        r.Detail = t.T("The trace went through more switches than the list has: the tables may be changing. Try again.");
    }

    private static void SetPlace(LocateResult r, ConfTarget sw, HopDecision d, string status)
    {
        r.Switch = sw.Name;
        r.SwitchIP = sw.IP;
        r.Port = d.Port;
        r.MacsOnPort = d.MacsOnPort;
        r.PortStatus = status;
        if (r.Vlan.Length == 0)
        {
            r.Vlan = d.Vlan;
        }
    }

    /// <summary>The "show interfaces &lt;port&gt; status" line of the port (empty if the switch does not answer it).</summary>
    private string PortStatus(SwitchConnection c, string port)
    {
        try
        {
            var text = Run(c, Ci.F("show interfaces {0} status", port), 30);
            return Ci.Split(text, "\\r?\\n").FirstOrDefault(l => l.StartsWith(port, StringComparison.OrdinalIgnoreCase))?.Trim() ?? string.Empty;
        }
        catch (SwitchException)
        {
            return string.Empty;
        }
    }

    /// <summary>The result as text: what was found, the path switch by switch and the notes.</summary>
    public string Summary(LocateResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        void Line(string label, string value)
        {
            if (value.Length > 0)
            {
                sb.Append(t.T(label)).Append(": ").Append(value).Append('\n');
            }
        }
        Line("Searched", r.Target);
        Line("IP", r.Ip);
        Line("MAC", r.Mac);
        Line("VLAN", r.Vlan);
        Line("Result", t.T(r.Outcome.Code()));
        Line("Switch", r.Switch.Length > 0 ? Ci.F("{0} ({1})", r.Switch, r.SwitchIP) : string.Empty);
        Line("Port", r.Port);
        Line("Port status", r.PortStatus);
        if (r.Detail.Length > 0)
        {
            sb.Append('\n').Append(r.Detail).Append('\n');
        }
        if (r.Hops.Count > 0)
        {
            sb.Append('\n').Append(t.T("Path:")).Append('\n');
            var i = 0;
            foreach (var h in r.Hops)
            {
                sb.Append(Ci.F("{0,2}. {1} ({2}): {3}\n", ++i, h.Switch, h.IP, h.Detail));
            }
        }
        if (r.Notes.Count > 0)
        {
            sb.Append('\n').Append(t.T("Notes:")).Append('\n');
            foreach (var n in r.Notes)
            {
                sb.Append(" - ").Append(n).Append('\n');
            }
        }
        sb.Append('\n').Append(Ci.F(t.T("Evidence: {0}"), r.Folder)).Append('\n');
        return masker.Protect(sb.ToString());
    }
}
