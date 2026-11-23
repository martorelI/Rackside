namespace Rackside.Core.Restore;

/// <summary>Kind of entry in the review report, in the order the report shows them.</summary>
public enum ReportType
{
    Blocked,
    NoMapping,
    Warning,
    Removed,
    Changed,
    Added,
}

/// <summary>An entry of the review report: what happened to a line and why.</summary>
public sealed record ReportItem(ReportType Type, string Line, string Reason)
{
    /// <summary>Internal code of the type (also the English text shown): BLOCKED, NO_MAPPING...</summary>
    public static string Code(ReportType type) => type switch
    {
        ReportType.Blocked => "BLOCKED",
        ReportType.NoMapping => "NO_MAPPING",
        ReportType.Warning => "WARNING",
        ReportType.Removed => "REMOVED",
        ReportType.Changed => "CHANGED",
        ReportType.Added => "ADDED",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
