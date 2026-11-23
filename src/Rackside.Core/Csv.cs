using System.Text;

namespace Rackside.Core;

/// <summary>Minimal CSV (comma, every field in quotes, "" inside quotes).</summary>
internal static class Csv
{
    public static string Line(IEnumerable<string> fields) => string.Join(',', fields.Select(f => "\"" + (f ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));

    public static List<List<string>> Parse(string text, char delimiter = ',')
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case var d when d == delimiter:
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                case '\uFEFF' when i == 0:
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
