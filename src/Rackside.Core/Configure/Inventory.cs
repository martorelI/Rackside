using System.Text;
using Rackside.Core.Localization;

namespace Rackside.Core.Configure;

/// <summary>A switch of the list (Inventory/switches.csv). Column names are case-insensitive.</summary>
public sealed class InventoryRow
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Sel" column: checked for the sweep. Not saved to the CSV.</summary>
    public bool Selected { get; set; }

    /// <summary>The value, trimmed; empty if missing.</summary>
    public string this[string column]
    {
        get => values.TryGetValue(column, out var v) ? v.Trim() : column == "Port" ? "22" : string.Empty;
        set => values[column] = value ?? string.Empty;
    }
}

public sealed record ImportResult(int Added, int Updated, int Skipped);

/// <summary>A switch of the list the operation connects to over SSH (Row = null only for a switch outside the list).</summary>
public sealed record ConfTarget(InventoryRow? Row, string Name, string IP, int Port);

/// <summary>The switch list of "Configure switches".</summary>
public sealed class Inventory
{
    public static readonly IReadOnlyList<string> Columns =
        ["Name", "IP", "Port", "Hostname", "Model", "Platform", "Version", "Serial", "LLDP", "CDP", "VTP", "STP", "Domain", "SNMP", "NTP", "Syslog", "Banner", "SSH", "Status", "Date"];

    public List<InventoryRow> Rows { get; } = [];

    private static string Key(string ip, string port) => Ci.F("{0}:{1}", ip, port.Length == 0 ? "22" : port);

    /// <summary>
    /// Reads a CSV separated by commas or semicolons. Recognizes Name / Nombre / Hostname / Switch and
    /// IP / Address / Direccion; if a row with the same IP and port exists, it updates its name.
    /// </summary>
    public ImportResult Import(string path, Translator t, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(t);
        var text = File.ReadAllText(path, Encoding.UTF8);
        var first = text.Split('\n')[0];
        var delim = first.Contains(';', StringComparison.Ordinal) && !first.Contains(',', StringComparison.Ordinal) ? ';' : ',';
        var table = Csv.Parse(text, delim);
        if (replace)
        {
            Rows.Clear();
        }
        if (table.Count <= 1)
        {
            return new ImportResult(0, 0, 0);
        }
        var props = table[0].Select(h => h.TrimStart('\uFEFF').Trim()).ToList();
        int Find(string pattern) => props.FindIndex(n => Ci.IsMatch(n, pattern));
        var colName = Find("^(?i)(nombre|name|hostname|host\\s*name|switch|equipo|dispositivo|device)$");
        var colIP = Find("^(?i)(ip|ip\\s*address|ipaddress|direccion(\\s*ip)?|direcci.n(\\s*ip)?|address|ip\\s*de\\s*gestion|gestion|mgmt(\\s*ip)?)$");
        var colPort = Find("^(?i)(puerto(\\s*ssh)?|port|ssh\\s*port)$");
        if (colIP < 0)
        {
            throw new FormatException(Ci.F(t.T("The CSV has no IP column (IP, Direccion or Address). Columns found: {0}"), string.Join(", ", props)));
        }
        var byIP = new Dictionary<string, InventoryRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Rows)
        {
            byIP[Key(f["IP"], f["Port"])] = f;
        }
        int added = 0, updated = 0, skipped = 0;
        foreach (var r in table.Skip(1).Where(r => r.Count > 1 || (r.Count == 1 && r[0].Trim().Length > 0)))
        {
            string Cell(int i) => i >= 0 && i < r.Count ? r[i].Trim() : string.Empty;
            var ip = Cell(colIP);
            if (ip.Length == 0)
            {
                skipped++;
                continue;
            }
            var name = Cell(colName);
            if (name.Length == 0)
            {
                name = ip;
            }
            var portCsv = Cell(colPort);
            var k = Key(ip, portCsv);
            if (byIP.TryGetValue(k, out var f))
            {
                updated++;
            }
            else
            {
                f = new InventoryRow();
                Rows.Add(f);
                byIP[k] = f;
                added++;
            }
            f["Name"] = name;
            f["IP"] = ip;
            if (portCsv.Length > 0)
            {
                f["Port"] = portCsv;
            }
            // What was documented (if the CSV comes from this tool)
            foreach (var n in Columns.Where(n => n is not ("Name" or "IP" or "Port")))
            {
                var i = props.FindIndex(p => Ci.Eq(p, n));
                if (i >= 0)
                {
                    f[n] = i < r.Count ? r[i] : string.Empty;
                }
            }
        }
        return new ImportResult(added, updated, skipped);
    }

    /// <summary>Saves it (every field quoted, UTF-8 with BOM, CRLF).</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var sb = new StringBuilder();
        sb.Append(Csv.Line(Columns)).Append("\r\n");
        foreach (var f in Rows)
        {
            sb.Append(Csv.Line(Columns.Select(c => f[c]))).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Switches it will connect to over SSH: the checked ones.</summary>
    public IReadOnlyList<ConfTarget> Targets(Translator t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var list = new List<ConfTarget>();
        var problems = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Rows.Where(f => f.Selected))
        {
            var name = f["Name"];
            var ip = f["IP"];
            var port = 22;
            var txtPort = f["Port"];
            if (txtPort.Length > 0)
            {
                if (int.TryParse(txtPort, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tmp) && tmp >= 1 && tmp <= 65535)
                {
                    port = tmp;
                }
                else
                {
                    problems.Add(Ci.F(t.T("{0}: invalid SSH port \"{1}\""), name, txtPort));
                }
            }
            if (name.Length == 0)
            {
                name = ip;
            }
            if (!Validators.IsIPv4(ip))
            {
                problems.Add(Ci.F(t.T("{0}: invalid IP \"{1}\""), name, ip));
                continue;
            }
            var key = Ci.F("{0}:{1}", ip, port);
            if (seen.TryGetValue(key, out var other))
            {
                problems.Add(Ci.F(t.T("{0}: IP {1} is repeated (also {2})"), name, key, other));
                continue;
            }
            seen[key] = name;
            list.Add(new ConfTarget(f, name, ip, port));
        }
        if (problems.Count > 0)
        {
            throw new FormatException(Ci.F(t.T("Fix the switch list (tab 1):{0}{1}"), "\r\n", string.Join("\r\n", problems)));
        }
        if (list.Count == 0)
        {
            throw new FormatException(t.T("Check in tab 1 (Sel column) the switches to connect to over SSH."));
        }
        return list;
    }

    /// <summary>
    /// Every switch of the list with a valid IP and SSH port, checked or not (for locating an address).
    /// Rows without a valid IP are left out silently; the first row wins if an IP:port is repeated.
    /// </summary>
    public IReadOnlyList<ConfTarget> AllWithIp()
    {
        var list = new List<ConfTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Rows)
        {
            var ip = f["IP"];
            if (!Validators.IsIPv4(ip)
                || !int.TryParse(f["Port"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port)
                || port < 1 || port > 65535 || !seen.Add(Ci.F("{0}:{1}", ip, port)))
            {
                continue;
            }
            list.Add(new ConfTarget(f, f["Name"].Length > 0 ? f["Name"] : ip, ip, port));
        }
        return list;
    }

    /// <summary>Folder-safe name.</summary>
    public static string SafeName(string? text)
    {
        var s = Ci.Replace(text, "[^A-Za-z0-9_\\.\\-]", "_").Trim('_');
        if (s.Length == 0)
        {
            s = "switch";
        }
        return s.Length > 60 ? s[..60] : s;
    }
}
