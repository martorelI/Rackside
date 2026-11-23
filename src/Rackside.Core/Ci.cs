using System.Globalization;
using System.Text.RegularExpressions;

namespace Rackside.Core;

/// <summary>
/// Case-insensitive text helpers, the way the switch CLI treats commands: regular expressions and
/// comparisons ignore case, the sort compares with the invariant culture ignoring case, and formatting is
/// invariant (what goes to the switch must not depend on the PC).
/// </summary>
internal static class Ci
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>String sort (invariant culture, case-insensitive).</summary>
    public static readonly StringComparer SortComparer = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);

    /// <summary>Whether the pattern matches.</summary>
    public static bool IsMatch(string? input, string pattern) => Regex.IsMatch(input ?? string.Empty, pattern, MatchOptions);

    /// <summary>Whether the pattern matches, with the match.</summary>
    public static bool TryMatch(string? input, string pattern, out Match match)
    {
        match = Regex.Match(input ?? string.Empty, pattern, MatchOptions);
        return match.Success;
    }

    /// <summary>Replaces every match of the pattern.</summary>
    public static string Replace(string? input, string pattern, string replacement) =>
        Regex.Replace(input ?? string.Empty, pattern, replacement, MatchOptions);

    /// <summary>Splits by a regular expression.</summary>
    public static string[] Split(string? input, string pattern) => Regex.Split(input ?? string.Empty, pattern, MatchOptions);

    /// <summary>Equal ignoring case.</summary>
    public static bool Eq(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>string.Format with the invariant culture.</summary>
    public static string F(string format, params object?[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
}
