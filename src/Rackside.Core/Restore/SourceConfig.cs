namespace Rackside.Core.Restore;

/// <summary>A management SVI found in the backup.</summary>
public sealed record Svi(string Name, string IP, string Mask);

/// <summary>A backup (running-config) parsed into blocks, plus what can be read from it.</summary>
public sealed class SourceConfig
{
    public required string Path { get; init; }
    public required IReadOnlyList<ConfigBlock> Blocks { get; init; }
    /// <summary>"IOS" or "IOS-XE".</summary>
    public required string OS { get; init; }
    public required string Version { get; init; }
    public required string Model { get; init; }
    public required string Hostname { get; init; }
    public required string Domain { get; init; }
    public required string Gateway { get; init; }
    public required IReadOnlyList<Svi> Svis { get; init; }
    /// <summary>Physical interfaces (long names) in the order they appear.</summary>
    public required IReadOnlyList<string> Physical { get; init; }
    /// <summary>Physical interfaces (lower case long names) with useful configuration.</summary>
    public required IReadOnlySet<string> WithConfig { get; init; }
    public required IReadOnlyList<string> Users { get; init; }
    public required string UserAdmin { get; init; }

    /// <summary>Reads a backup file.</summary>
    public static SourceConfig ReadFile(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Parses the text of a backup.</summary>
    /// <exception cref="FormatException">The text does not look like an IOS / IOS-XE configuration.</exception>
    public static SourceConfig Parse(string text, string path = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var blocks = new List<ConfigBlock>();
        ConfigBlock? current = null;
        var i = 0;
        while (i < lines.Length)
        {
            var l = lines[i].TrimEnd();
            i++;
            if (string.IsNullOrWhiteSpace(l))
            {
                continue;
            }
            if (Ci.IsMatch(l, "^!"))
            {
                current = null;
                continue;
            }
            if (Ci.IsMatch(l, "^(Building configuration|Current configuration|Last configuration change|NVRAM config last updated|Using \\d+ out of)"))
            {
                continue;
            }
            if (Ci.IsMatch(l, "^end\\s*$"))
            {
                break;
            }
            if (Ci.TryMatch(l, "^banner\\s+(\\S+)\\s+\\^C(.*)$", out var mb))
            {
                var typeB = mb.Groups[1].Value;
                var rest = mb.Groups[2].Value;
                var txt = new List<string>();
                if (Ci.TryMatch(rest, "^(.*?)\\^C", out var m1))
                {
                    if (m1.Groups[1].Value.Length > 0)
                    {
                        txt.Add(m1.Groups[1].Value);
                    }
                }
                else
                {
                    if (rest.Length > 0)
                    {
                        txt.Add(rest);
                    }
                    while (i < lines.Length)
                    {
                        var b = lines[i].TrimEnd();
                        i++;
                        if (Ci.TryMatch(b, "^(.*?)\\^C", out var m2))
                        {
                            if (m2.Groups[1].Value.Length > 0)
                            {
                                txt.Add(m2.Groups[1].Value);
                            }
                            break;
                        }
                        txt.Add(b);
                    }
                }
                blocks.Add(new ConfigBlock("banner " + typeB, isBanner: true, bannerText: txt, bannerType: typeB));
                current = null;
                continue;
            }
            if (Ci.IsMatch(l, "^\\s"))
            {
                if (Ci.IsMatch(l, "^\\s*!"))
                {
                    continue;
                }
                current?.Children.Add(l);
                continue;
            }
            current = new ConfigBlock(l);
            blocks.Add(current);
        }

        var headers = blocks.Where(b => !b.IsBanner).Select(b => b.Header).ToList();
        var all = string.Join('\n', headers);
        if (!Ci.IsMatch(all, "(?m)^hostname\\s") && !Ci.IsMatch(all, "(?m)^version\\s"))
        {
            throw new FormatException("The file does not look like a Cisco IOS / IOS-XE configuration (it has no \"hostname\" or \"version\").");
        }

        var os = "IOS";
        var version = string.Empty;
        if (Ci.TryMatch(all, "(?m)^version\\s+(\\d+)\\.(\\d+)", out var mv))
        {
            version = mv.Groups[1].Value + "." + mv.Groups[2].Value;
            if (int.Parse(mv.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 16)
            {
                os = "IOS-XE";
            }
        }
        else if (Ci.IsMatch(all, "(?m)^(license boot level|vrf definition Mgmt-vrf)"))
        {
            os = "IOS-XE";
        }

        var model = Ci.TryMatch(all, "(?m)^switch\\s+\\d+\\s+provision\\s+(\\S+)", out var mm) ? mm.Groups[1].Value : string.Empty;
        var hostname = Ci.TryMatch(all, "(?m)^hostname\\s+(\\S+)", out var mh) ? mh.Groups[1].Value : string.Empty;
        var domain = Ci.TryMatch(all, "(?m)^ip domain[ -]name\\s+(\\S+)", out var md) ? md.Groups[1].Value : string.Empty;
        var gateway = string.Empty;
        if (Ci.TryMatch(all, "(?m)^ip default-gateway\\s+(\\S+)", out var mg))
        {
            gateway = mg.Groups[1].Value;
        }
        else if (Ci.TryMatch(all, "(?m)^ip route (?:vrf \\S+ )?0\\.0\\.0\\.0 0\\.0\\.0\\.0 (\\d+\\.\\d+\\.\\d+\\.\\d+)", out var mr))
        {
            gateway = mr.Groups[1].Value;
        }

        var svis = new List<Svi>();
        var physical = new List<string>();
        var withConfig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in blocks.Where(b => !b.IsBanner))
        {
            if (Ci.TryMatch(b.Header, "^interface (Vlan\\d+)$", out var ms))
            {
                var ip = string.Empty;
                var mask = string.Empty;
                foreach (var h in b.Children)
                {
                    if (Ci.TryMatch(h, "^\\s+ip address (\\d+\\.\\d+\\.\\d+\\.\\d+) (\\d+\\.\\d+\\.\\d+\\.\\d+)\\s*$", out var mi))
                    {
                        ip = mi.Groups[1].Value;
                        mask = mi.Groups[2].Value;
                    }
                }
                svis.Add(new Svi(ms.Groups[1].Value, ip, mask));
            }
            else if (Ci.TryMatch(b.Header, "^interface (\\S+)$", out var mf))
            {
                var info = InterfaceInfo.Parse(mf.Groups[1].Value);
                if (info is not null && !info.IsManagement)
                {
                    physical.Add(info.Long);
                    if (b.Children.Any(h => !Ci.IsMatch(h, "^\\s+(shutdown|no ip address|negotiation auto|no switchport)\\s*$")))
                    {
                        withConfig.Add(info.Long.ToLowerInvariant());
                    }
                }
            }
        }

        var users = headers.Where(h => Ci.IsMatch(h, "^username\\s+\\S+")).Select(h => Ci.Split(h, "\\s+")[1]).ToList();
        var priv15 = headers.Where(h => Ci.IsMatch(h, "^username\\s+\\S+\\s+privilege 15")).ToList();
        var userAdmin = priv15.Count > 0 ? Ci.Split(priv15[0], "\\s+")[1] : users.Count > 0 ? users[0] : string.Empty;

        return new SourceConfig
        {
            Path = path,
            Blocks = blocks,
            OS = os,
            Version = version,
            Model = model,
            Hostname = hostname,
            Domain = domain,
            Gateway = gateway,
            Svis = svis,
            Physical = physical,
            WithConfig = withConfig,
            Users = users,
            UserAdmin = userAdmin,
        };
    }
}
