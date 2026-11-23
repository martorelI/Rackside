using System.Text.RegularExpressions;
using Rackside.Core.Localization;

namespace Rackside.Tests;

/// <summary>
/// The window (src/Rackside.App) writes its texts in English with S("...") / F("..."): each one needs Spanish in
/// Resources/ui.es.tsv or in the Core table.
/// </summary>
public class AppTextsTests
{
    private static Dictionary<string, string> UiTable()
    {
        using var r = new StreamReader(RepoPaths.Combine("src", "Rackside.App", "Resources", "ui.es.tsv"));
        return Translator.Parse(r);
    }

    [Fact]
    public void Every_window_text_has_spanish()
    {
        var ui = UiTable();
        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(RepoPaths.Combine("src", "Rackside.App"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("\\obj\\", StringComparison.Ordinal)))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\\b[SF]\\(\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                var text = Regex.Unescape(m.Groups[1].Value);
                if (!ui.ContainsKey(text) && !Translator.Entries.ContainsKey(text))
                {
                    missing.Add(Path.GetFileName(file) + ": " + text);
                }
            }
        }
        Assert.True(missing.Count == 0, "Without Spanish:\n" + string.Join("\n", missing.Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_window_table_is_embedded_in_the_exe()
    {
        // A ".es." in the file name makes MSBuild treat it as a culture resource (satellite assembly) unless WithCulture="false".
        var csproj = File.ReadAllText(RepoPaths.Combine("src", "Rackside.App", "Rackside.App.csproj"));
        Assert.Matches("<EmbeddedResource Include=\"Resources\\\\ui\\.es\\.tsv\"[^>]*WithCulture=\"false\"", csproj);
    }

    [Fact]
    public void The_window_table_keeps_the_placeholders()
    {
        foreach (var (en, es) in UiTable())
        {
            var a = Regex.Matches(en, "\\{\\d+\\}").Select(m => m.Value).Order(StringComparer.Ordinal);
            var b = Regex.Matches(es, "\\{\\d+\\}").Select(m => m.Value).Order(StringComparer.Ordinal);
            Assert.True(a.SequenceEqual(b), en);
        }
    }
}
