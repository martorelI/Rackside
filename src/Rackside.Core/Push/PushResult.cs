using Rackside.Core.Configure;

namespace Rackside.Core.Push;

/// <summary>Result of one switch in a push. The code (English) is what the record and the logs translate.</summary>
public enum PushOutcome
{
    Failed,
    Skipped,
    NotRun,
    WithFailures,
    SuccessfulWithNotes,
    Successful,
}

public static class PushOutcomeExtensions
{
    /// <summary>Internal state text (key of the translation table).</summary>
    public static string Code(this PushOutcome o) => o switch
    {
        PushOutcome.Failed => "FAILED",
        PushOutcome.Skipped => "SKIPPED",
        PushOutcome.NotRun => "NOT RUN",
        PushOutcome.WithFailures => "WITH FAILURES",
        PushOutcome.SuccessfulWithNotes => "SUCCESSFUL WITH NOTES",
        PushOutcome.Successful => "SUCCESSFUL",
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };
}

/// <summary>What happened on one switch.</summary>
public sealed class SwitchResult(ConfTarget target)
{
    public string Name { get; set; } = target?.Name ?? string.Empty;

    /// <summary>ip, or ip:port when the port is not 22.</summary>
    public string IP { get; } = target is null || target.IP.Length == 0 ? string.Empty : target.Port != 22 ? Ci.F("{0}:{1}", target.IP, target.Port) : target.IP;

    public string Hostname { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public PushOutcome Result { get; set; } = PushOutcome.Failed;
    public string Detail { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string Saved { get; set; } = "No";
    public IReadOnlyList<(string Line, string Answer)> Warnings { get; set; } = [];
    public IReadOnlyList<PushCheck> Verification { get; set; } = [];
    public string Folder { get; set; } = string.Empty;
    public SwitchDocument? Doc { get; set; }
    public DateTime Start { get; init; } = DateTime.Now;
    public DateTime? End { get; set; }
}
