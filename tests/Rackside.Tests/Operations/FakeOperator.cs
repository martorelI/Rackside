using System.Text.RegularExpressions;
using Rackside.Operations;

namespace Rackside.Tests.Operations;

/// <summary>The operator in the tests: records warnings and answers them by regex (accepts by default).</summary>
internal sealed class FakeOperator : IOperatorUi
{
    public List<(string Title, string Text)> Warnings { get; } = [];

    public Dictionary<string, bool> WarningAnswer { get; } = [];

    public bool QuestionAnswer { get; set; }

    public List<string> Statuses { get; } = [];

    public bool Warn(string text, string title)
    {
        Warnings.Add((title, text));
        foreach (var (k, v) in WarningAnswer)
        {
            if (Regex.IsMatch(text, k))
            {
                return v;
            }
        }
        return true;
    }

    public bool Ask(string text, string title) => QuestionAnswer;

    public void Status(string text, int? progress = null) => Statuses.Add(text);
}
