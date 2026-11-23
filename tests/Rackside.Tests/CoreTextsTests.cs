using System.Text.RegularExpressions;
using Rackside.Core.Localization;

namespace Rackside.Tests;

/// <summary>
/// The operations write their texts in English with T("..."): each one needs Spanish in
/// src/Rackside.Core/Localization/es.tsv (or is listed in tests/fixtures/untranslated.txt), and the table
/// keeps only texts the code uses, with the same placeholders and HTML tags.
/// </summary>
public class CoreTextsTests
{
    private static readonly string[] Sources = Directory.GetFiles(RepoPaths.Combine("src"), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("\\obj\\", StringComparison.Ordinal)
            && !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("\\bin\\", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToArray();

    private static string AllCode() => string.Concat(Sources.Select(File.ReadAllText));

    private static HashSet<string> Untranslated() =>
        File.ReadAllLines(RepoPaths.Combine("tests", "fixtures", "untranslated.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> TLiterals() =>
        Regex.Matches(AllCode(), "\\.T\\(\"((?:[^\"\\\\]|\\\\.)*)\"\\)").Select(m => Regex.Unescape(m.Groups[1].Value));

    [Fact]
    public void Every_text_has_spanish_or_is_listed_as_untranslated()
    {
        var untranslated = Untranslated();
        var missing = TLiterals().Where(k => !Translator.Entries.ContainsKey(k) && !untranslated.Contains(k)).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Without Spanish (add them to es.tsv):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void The_untranslated_list_has_no_stale_entries()
    {
        var used = TLiterals().ToHashSet(StringComparer.Ordinal);
        var stale = Untranslated().Where(k => !used.Contains(k) || Translator.Entries.ContainsKey(k)).ToList();
        Assert.True(stale.Count == 0, "Remove from tests/fixtures/untranslated.txt:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void The_table_only_has_texts_the_code_uses()
    {
        var code = AllCode();
        var unused = Translator.Entries.Keys.Where(k =>
        {
            var literal = "\"" + k.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\"";
            return !code.Contains(literal, StringComparison.Ordinal);
        }).ToList();
        Assert.True(unused.Count == 0, "Not used by the code (remove them from es.tsv):\n" + string.Join("\n", unused.Take(20)));
    }

    [Fact]
    public void The_table_keeps_the_placeholders_and_the_html()
    {
        var wrong = new List<string>();
        foreach (var (en, es) in Translator.Entries)
        {
            if (!Same(en, es, "\\{\\d+(?::[^}]*)?\\}") || !Same(en, es, Html))
            {
                wrong.Add(en);
            }
        }
        Assert.True(wrong.Count == 0, "Placeholders or HTML differ:\n" + string.Join("\n", wrong.Take(20)));
    }

    private const string Html = "</?(?:p|div|span|table|tr|td|th|thead|tbody|h1|h2|h3|b|i|em|strong|code|pre|br|ul|ol|li|a|img|button|style|head|body|html|meta|title|small)\\b";

    private static bool Same(string en, string es, string pattern) =>
        Regex.Matches(en, pattern).Select(m => m.Value).Order(StringComparer.Ordinal)
            .SequenceEqual(Regex.Matches(es, pattern).Select(m => m.Value).Order(StringComparer.Ordinal), StringComparer.Ordinal);
}
