using System.Text.RegularExpressions;
using Rackside.Core.Localization;

namespace Rackside.Tests;

public partial class TranslatorTests
{
    [Fact]
    public void English_returns_the_same_text()
    {
        Assert.Equal("Log", Translator.English.T("Log"));
        Assert.Equal("Anything not in the table", Translator.Spanish.T("Anything not in the table"));
    }

    [Fact]
    public void Spanish_translates_with_the_table()
    {
        Assert.Equal("Desarrollado por {0}", Translator.Spanish.T("Developed by {0}"));
        Assert.Equal("Soporte: {0}", Translator.Spanish.T("Support: {0}"));
    }

    [Fact]
    public void Keys_are_case_sensitive()
    {
        Assert.Equal("Yes", Translator.English.T("Yes"));
        Assert.NotEqual(Translator.Spanish.T("Yes"), Translator.Spanish.T("yes"));
    }

    [Theory]
    [InlineData("es", "es")]
    [InlineData("ES", "es")]
    [InlineData("en", "en")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    public void For_picks_the_language(string? code, string expected) =>
        Assert.Equal(expected, Translator.For(code).Language);

    [Fact]
    public void Parse_handles_escapes_and_ignores_lines_without_a_tab()
    {
        var t = Translator.Parse(new StringReader("A\\nB\tC\\rD\nno tab here\n\tstarts with tab\nX\tY"));
        Assert.Equal(2, t.Count);
        Assert.Equal("C\rD", t["A\nB"]);
        Assert.Equal("Y", t["X"]);
    }

    [Fact]
    public void Table_is_loaded_and_keeps_placeholders()
    {
        Assert.True(Translator.Entries.Count > 1000);
        var bad = Translator.Entries
            .Where(e => Placeholders(e.Key) != Placeholders(e.Value))
            .Select(e => e.Key)
            .ToList();
        Assert.Empty(bad);
    }

    private static string Placeholders(string s) =>
        string.Join(' ', PlaceholderRegex().Matches(s).Select(m => m.Value).Order(StringComparer.Ordinal));

    [GeneratedRegex(@"\{\d+(?::[^}]*)?(?:,-?\d+)?\}")]
    private static partial Regex PlaceholderRegex();
}
