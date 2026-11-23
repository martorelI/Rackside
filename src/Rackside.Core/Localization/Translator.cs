using System.Collections.Frozen;
using System.Reflection;

namespace Rackside.Core.Localization;

/// <summary>
/// Texts are written in English in the code and, when the
/// language is Spanish, translated with the English -> Spanish table. A text without an entry is shown
/// as is. Keys are compared exactly (ordinal).
/// </summary>
public sealed class Translator
{
    private const string ResourceName = "Rackside.Core.Localization.es.tsv";
    private static readonly Lazy<FrozenDictionary<string, string>> SpanishTable = new(LoadSpanishTable);

    private readonly FrozenDictionary<string, string>? table;

    private Translator(string language, FrozenDictionary<string, string>? table)
    {
        Language = language;
        this.table = table;
    }

    /// <summary>English (base language): returns every text unchanged.</summary>
    public static Translator English { get; } = new("en", null);

    /// <summary>Spanish: translates with the embedded table.</summary>
    public static Translator Spanish { get; } = new("es", null);

    /// <summary>"en" or "es".</summary>
    public string Language { get; }

    /// <summary>The English -> Spanish table (shared by every instance).</summary>
    public static IReadOnlyDictionary<string, string> Entries => SpanishTable.Value;

    /// <summary>Translator for a language code from settings.json; anything other than "es" is English.</summary>
    public static Translator For(string? language) =>
        string.Equals(language, "es", StringComparison.OrdinalIgnoreCase) ? Spanish : English;

    /// <summary>Translates a text written in English.</summary>
    public string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || Language != "es")
        {
            return text ?? string.Empty;
        }
        var t = table ?? SpanishTable.Value;
        return t.TryGetValue(text, out var es) ? es : text;
    }

    /// <summary>
    /// Parses a table: one entry per line, English TAB Spanish, with \n and \r
    /// standing for line breaks. Lines without a tab (or starting with one) are ignored; a repeated key
    /// keeps the last value.
    /// </summary>
    public static Dictionary<string, string> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var i = line.IndexOf('\t', StringComparison.Ordinal);
            if (i < 1)
            {
                continue;
            }
            result[Unescape(line[..i])] = Unescape(line[(i + 1)..]);
        }
        return result;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal);

    private static FrozenDictionary<string, string> LoadSpanishTable()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream);
        return Parse(reader).ToFrozenDictionary(StringComparer.Ordinal);
    }
}
