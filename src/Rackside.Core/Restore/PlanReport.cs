using Rackside.Core.Localization;
using Rackside.Core.Security;

namespace Rackside.Core.Restore;

/// <summary>Collects the review report: lines are masked and reasons translated when added.</summary>
public sealed class PlanReport
{
    private readonly List<ReportItem> items = [];
    private readonly Translator translator;
    private readonly SecretMasker masker;

    public PlanReport(Translator translator, SecretMasker masker)
    {
        this.translator = translator;
        this.masker = masker;
    }

    public IReadOnlyList<ReportItem> Items => items;

    public int Count(ReportType type) => items.Count(i => i.Type == type);

    internal string T(string text) => translator.T(text);

    internal void Add(ReportType type, string line, string reason) =>
        items.Add(new ReportItem(type, masker.Protect(line), translator.T(reason)));
}
