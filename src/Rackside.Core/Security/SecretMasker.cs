using System.Text.RegularExpressions;

namespace Rackside.Core.Security;

/// <summary>
/// Hides secrets in everything written to logs, transcripts, previews and records: the values typed by the operator, anywhere in the text, and
/// the secrets that follow a keyword (secret, password, key, community, snmp-server user...) even when
/// they come from a backup.
/// </summary>
public sealed class SecretMasker
{
    public const string Mask = "********";

    // Applied line by line. Group 1 = what is kept, last group = what is hidden.
    private static readonly Regex[] Patterns =
    [
        new(@"(?i)(\b(?:secret|password)\s+(?:level\s+\d+\s+)?(?:\d{1,2}\s+)?)(?!encryption\b)(\S+)"),
        new(@"(?i)(\b(?:password|passcode)\s*:\s*)(\S+)"),
        new(@"(?i)(\bsnmp-server\s+community\s+)(\S+)"),
        new(@"(?i)(\bsnmp-server\s+host\s+\S+\s+(?:(?:informs|traps)\s+)?(?:version\s+(?:1|2c|3\s+(?:auth|noauth|priv))\s+)?)(?!version\b|informs\b|traps\b)(\S+)"),
        new(@"(?i)(\bauth\s+(?:md5|sha\S*)\s+)(\S+)"),
        new(@"(?i)(\bpriv\s+(?:des\S*|3des|aes(?:\s+\d+)?)\s+)(\S+)"),
        new(@"(?i)((?:^|\s)(?:key|key-string)\s+(?:\d\s+)?)(?!(?:generate|zeroize|chain|config-key|storage|import|export|rsa|ec|exchange|ring|hash|data|name|type|pair|is|was|modulus|label|size)\b)(\S+)"),
        new(@"(?i)(\bauthentication-key\s+(?:\d+\s+)?(?:md5\s+|sha\S*\s+)?(?:\d\s+)?)(\S+)"),
        new(@"(?i)(\bmessage-digest-key\s+\d+\s+md5\s+(?:\d\s+)?)(\S+)"),
    ];

    private readonly List<string> secrets = [];

    /// <summary>Registers values typed by the operator (empty values are ignored).</summary>
    public void Register(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v) && !secrets.Contains(v, StringComparer.Ordinal))
            {
                secrets.Add(v);
            }
        }
    }

    /// <summary>Returns the text with every known secret replaced by ********.</summary>
    public string Protect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }
        var t = text;
        foreach (var v in secrets.OrderByDescending(s => s.Length))
        {
            t = v.Length >= 4
                ? t.Replace(v, Mask, StringComparison.Ordinal)
                // Very short: only as a whole word so the rest of the text is not mangled
                : Regex.Replace(t, "(?<=^|\\s)" + Regex.Escape(v) + "(?=\\s|$)", Mask, RegexOptions.Multiline);
        }
        var lines = t.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0)
            {
                continue;
            }
            foreach (var p in Patterns)
            {
                l = p.Replace(l, m =>
                {
                    var last = m.Groups[m.Groups.Count - 1];
                    if (string.Equals(last.Value, Mask, StringComparison.OrdinalIgnoreCase))
                    {
                        return m.Value;
                    }
                    var start = last.Index - m.Index;
                    return string.Concat(m.Value.AsSpan(0, start), Mask, m.Value.AsSpan(start + last.Length));
                });
            }
            lines[i] = l;
        }
        return string.Join('\n', lines);
    }
}
