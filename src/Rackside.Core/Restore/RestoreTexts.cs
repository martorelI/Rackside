using System.Text;
using Rackside.Core.Localization;
using Rackside.Core.Security;

namespace Rackside.Core.Restore;

/// <summary>The texts the review shows and saves as evidence: configuration, changes and mapping.</summary>
public static class RestoreTexts
{
    private static readonly ReportType[] Order = [ReportType.Blocked, ReportType.NoMapping, ReportType.Warning, ReportType.Removed, ReportType.Changed, ReportType.Added];

    /// <summary>Configuration as it will be sent, with secrets hidden.</summary>
    public static string Config(RestorePlan plan, SecretMasker masker, params string?[] hide)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(masker);
        var sb = new StringBuilder();
        foreach (var b in plan.Blocks)
        {
            if (b.IsBanner)
            {
                Line(sb, Ci.F("banner {0} {1}", b.BannerType, b.Delimiter));
                foreach (var t in b.BannerText)
                {
                    Line(sb, t);
                }
                Line(sb, b.Delimiter);
            }
            else
            {
                Line(sb, b.Header);
                foreach (var h in b.Children)
                {
                    Line(sb, h);
                }
            }
            Line(sb, "!");
        }
        Line(sb, "end");
        // Copy to show and save as evidence: no passwords, hashes, keys or communities
        masker.Register(hide);
        return masker.Protect(sb.ToString());
    }

    /// <summary>The change report grouped by type (BLOCKED, NO_MAPPING, WARNING, REMOVED, CHANGED, ADDED).</summary>
    public static string Changes(RestorePlan plan, Translator translator)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(translator);
        var sb = new StringBuilder();
        foreach (var type in Order)
        {
            var items = plan.Report.Items.Where(i => i.Type == type).ToList();
            if (items.Count == 0)
            {
                continue;
            }
            Line(sb, Ci.F("===== {0} ({1}) =====", translator.T(ReportItem.Code(type)), items.Count));
            foreach (var (line, reason) in Grouped(items))
            {
                Line(sb, line);
                Line(sb, "    -> " + reason);
            }
            Line(sb, string.Empty);
        }
        if (sb.Length == 0)
        {
            Line(sb, translator.T("No changes: the file is applied as is."));
        }
        return sb.ToString();
    }

    /// <summary>The interface mapping (or the DIRECT mode note).</summary>
    public static string Mapping(RestorePlan plan, Translator translator)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(translator);
        var sb = new StringBuilder();
        if (plan.Mapping is null)
        {
            Line(sb, translator.T("DIRECT mode: same model, interfaces are applied with their original names."));
            return sb.ToString();
        }
        Line(sb, Ci.F("{0,-28} {1,-28} {2}", translator.T("SOURCE"), translator.T("TARGET"), translator.T("ROLE")));
        foreach (var r in plan.Mapping.Rows)
        {
            Line(sb, Ci.F(translator.T("{0,-28} {1,-28} {2} (member {3})"), r.Source, r.Target, translator.T(r.Role), r.Member));
        }
        if (plan.Mapping.FreeList.Count > 0)
        {
            Line(sb, string.Empty);
            Line(sb, Ci.F(translator.T("Target interfaces with no source configuration (left at defaults): {0}"), string.Join(", ", plan.Mapping.FreeList)));
        }
        return sb.ToString();
    }

    /// <summary>Groups identical entries that only differ by interface: "[interface X] line".</summary>
    public static IReadOnlyList<(string Line, string Reason)> Grouped(IEnumerable<ReportItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var order = new List<(string Text, string Reason, List<string>? Interfaces)>();
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (Ci.TryMatch(it.Line, "^\\[interface (\\S+)\\] (.*)$", out var m))
            {
                var k = m.Groups[2].Value + "|" + it.Reason;
                if (!byKey.TryGetValue(k, out var i))
                {
                    byKey[k] = i = order.Count;
                    order.Add((m.Groups[2].Value, it.Reason, []));
                }
                order[i].Interfaces!.Add(m.Groups[1].Value);
            }
            else
            {
                order.Add((it.Line, it.Reason, null));
            }
        }
        return order.Select(g => g.Interfaces switch
        {
            null => (g.Text, g.Reason),
            { Count: <= 3 } ifs => (Ci.F("[{0}] {1}", string.Join(", ", ifs), g.Text), g.Reason),
            var ifs => (Ci.F("[{0} interfaces: {1} ... {2}] {3}", ifs.Count, ifs[0], ifs[^1], g.Text), g.Reason),
        }).ToList();
    }

    // These texts use LF line ends.
    private static void Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
