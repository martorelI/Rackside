using System.Globalization;
using Rackside.Core.Localization;

namespace Rackside.App.Ui;

/// <summary>
/// Texts of the window. They are written in English; in Spanish they come from Resources/ui.es.tsv (this
/// window) or, if not there, from the Core table (the texts shared with the operations).
/// </summary>
internal static class Text
{
    private static Translator translator = Translator.English;
    private static Dictionary<string, string> table = new(StringComparer.Ordinal);

    public static Translator Translator => translator;

    public static void Initialize(Translator t)
    {
        translator = t;
        table = Load();
    }

    /// <summary>The window table; empty (texts then come from the Core table or stay in English) if it is missing.</summary>
    public static Dictionary<string, string> Load()
    {
        using var s = typeof(Text).Assembly.GetManifestResourceStream("Rackside.App.ui.es.tsv");
        if (s is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        using var r = new StreamReader(s);
        return Translator.Parse(r);
    }

    /// <summary>The text in the current language.</summary>
    public static string S(string en) =>
        translator.Language == "es" && table.TryGetValue(en, out var es) ? es : translator.T(en);

    public static string F(string en, params object?[] args) => string.Format(CultureInfo.CurrentCulture, S(en), args);
}
