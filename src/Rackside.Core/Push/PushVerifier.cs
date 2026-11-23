using Rackside.Core.Configure;
using Rackside.Core.Localization;
using Rackside.Core.Security;

namespace Rackside.Core.Push;

/// <summary>One check of the verification: Result is OK, NOTE or FAIL (internal codes, translated only to show them).</summary>
public sealed record PushCheck(string Check, string Result, string Detail);

/// <summary>Compares what was applied against the later running-config and the show outputs.</summary>
public static class PushVerifier
{
    /// <param name="errors">Lines the switch rejected while applying (label, answer).</param>
    /// <param name="shows">Output of each "show" command of the items.</param>
    public static IReadOnlyList<PushCheck> Verify(IEnumerable<PushItem> items, IEnumerable<(string Line, string Answer)> errors, string? running,
        IReadOnlyDictionary<string, string> shows, Translator t, SecretMasker masker)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(shows);
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(masker);
        var list = items.ToList();
        var res = new List<PushCheck>();
        var byLine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (line, answer) in errors)
        {
            byLine.TryAdd(line, answer);
        }
        var showsCi = new Dictionary<string, string>(shows, StringComparer.OrdinalIgnoreCase);
        void Add(string check, string result, string detail) => res.Add(new PushCheck(masker.Protect(check), result, masker.Protect(detail)));
        foreach (var i in list)
        {
            if (i.Verif == "note")
            {
                continue;
            }
            var lbl = i.Label;
            if (i.Verif == "warning")
            {
                Add(lbl, i.Result, i.Detail);
                continue;
            }
            if (byLine.TryGetValue(lbl, out var rejected))
            {
                Add(lbl, "FAIL", Ci.F(t.T("The switch rejected it: {0}"), rejected));
                continue;
            }
            var missingRes = "FAIL";
            var missingTxt = t.T("Not found in the running-config");
            if (i.IsDefault)
            {
                missingRes = "OK";
                missingTxt = t.T("It is the IOS default value (not shown in the running-config)");
            }
            else if (i.Default)
            {
                missingRes = "NOTE";
                missingTxt = t.T("Not found in the running-config: normal if it is the IOS default value");
            }
            switch (i.Verif)
            {
                case "none":
                    Add(lbl, "OK", t.T("Accepted without error (additional command: no automatic check)"));
                    break;
                case "hidden":
                    Add(lbl, "OK", t.T("Accepted without error (IOS does not show it in clear text)"));
                    break;
                case "regex":
                    if (Ci.IsMatch(running, i.Pattern)) { Add(lbl, "OK", t.T("Present in the running-config")); }
                    else { Add(lbl, missingRes, missingTxt); }
                    break;
                case "absent":
                    if (Ci.IsMatch(running, i.Pattern)) { Add(lbl, "FAIL", t.T("Still in the running-config")); }
                    else { Add(lbl, "OK", t.T("No longer in the running-config")); }
                    break;
                case "show":
                    var txt = showsCi.TryGetValue(i.Command, out var o) ? o : string.Empty;
                    if (Ci.IsMatch(txt, i.Pattern)) { Add(lbl, "OK", Ci.F(t.T("Confirmed with \"{0}\""), i.Command)); }
                    else { Add(lbl, "FAIL", Ci.F(t.T("\"{0}\" does not confirm it"), i.Command)); }
                    break;
                case "block":
                    var bl = SwitchDocument.RunningBlocks(running, i.Block);
                    var total = bl.Count;
                    var with = bl.Count(b => b.Any(h => Ci.IsMatch(h, i.Pattern)));
                    var name = Ci.Replace(i.Block, "[\\^\\\\]", string.Empty).Trim();
                    if (total > 0 && with == total) { Add(lbl, "OK", Ci.F(t.T("Present in {0} \"{1}\" block(s)"), total, name)); }
                    else if (with > 0) { Add(lbl, "NOTE", Ci.F(t.T("Present in {0} of {1} \"{2}\" blocks"), with, total, name)); }
                    else { Add(lbl, missingRes, missingTxt); }
                    break;
                default:
                    Add(lbl, "NOTE", t.T("No check"));
                    break;
            }
        }
        // Rejections that do not belong to a specific line (e.g. a block header)
        var labels = new HashSet<string>(list.Select(i => i.Label), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in byLine)
        {
            if (!labels.Contains(k))
            {
                Add(k, "FAIL", Ci.F(t.T("The switch rejected it: {0}"), v));
            }
        }
        return res;
    }

    /// <summary>Lines a set of blocks sends.</summary>
    public static int LineCount(IEnumerable<Restore.ConfigBlock> blocks) => blocks.Sum(b => 1 + b.Children.Count + b.BannerText.Count);
}
