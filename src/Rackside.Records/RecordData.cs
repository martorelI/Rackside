using Rackside.Core.Push;

namespace Rackside.Records;

/// <summary>What every record has at the top: result (internal code), reason and the event details.</summary>
public abstract record RecordBase
{
    /// <summary>Internal code: SUCCESSFUL, SUCCESSFUL WITH NOTES, FAILED, CANCELLED.</summary>
    public string Result { get; init; } = "FAILED";
    public string ErrorFatal { get; init; } = string.Empty;
    public string Ticket { get; init; } = string.Empty;
    public string Responsible { get; init; } = string.Empty;
    public string UserWindows { get; init; } = Environment.UserName;
    public string DeviceWork { get; init; } = Environment.MachineName;
    public string StartText { get; init; } = string.Empty;
    public string EndText { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;
}

/// <summary>A line and the switch answer (errors and warnings).</summary>
public sealed record LineAnswer(string Line, string Answer);

/// <summary>A change of the backup (BLOCKED, CHANGED...) for the DRP record.</summary>
public sealed record RecordChange(string Type, string Line, string Reason);

/// <summary>A row of the interface mapping.</summary>
public sealed record RecordMapping(string Source, string Target, string Role);

/// <summary>Data of the DRP record.</summary>
public sealed record DrpRecordData : RecordBase
{
    public string Remarks { get; init; } = string.Empty;
    public string FailedHost { get; init; } = string.Empty;
    public string FailedSerial { get; init; } = string.Empty;
    public string FailedModel { get; init; } = string.Empty;
    public string FailedOS { get; init; } = string.Empty;
    public string HostnameFinal { get; init; } = string.Empty;
    public string ReplacementModel { get; init; } = string.Empty;
    public string ReplacementSerial { get; init; } = string.Empty;
    public string ReplacementOS { get; init; } = string.Empty;
    public string IPManagement { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string BackupDate { get; init; } = string.Empty;
    public string BackupSize { get; init; } = string.Empty;
    public string BackupHash { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public int Lines { get; init; }
    public string ConfigHash { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;
    public IReadOnlyList<PushCheck> Verification { get; init; } = [];
    public IReadOnlyList<LineAnswer> Errors { get; init; } = [];
    public IReadOnlyList<LineAnswer> Warnings { get; init; } = [];
    public IReadOnlyList<RecordChange> Changes { get; init; } = [];
    public IReadOnlyList<RecordMapping> Mapping { get; init; } = [];
    public string ConfigText { get; init; } = string.Empty;
}

/// <summary>Data of the factory reset record.</summary>
public sealed record ResetRecordData : RecordBase
{
    public string Reason { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public string OS { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public int Members { get; init; } = 1;
    public string Filesystem { get; init; } = string.Empty;
    public IReadOnlyList<string> Protected { get; init; } = [];
    public string BackupPath { get; init; } = string.Empty;
    public string BackupHash { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;
    public IReadOnlyList<PushCheck> Actions { get; init; } = [];
    public IReadOnlyList<PushCheck> Verification { get; init; } = [];
}

/// <summary>Data of the IOS update record.</summary>
public sealed record UpgradeRecordData : RecordBase
{
    public string Hostname { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public int Members { get; init; } = 1;
    public string OS { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string VersionExpected { get; init; } = string.Empty;
    public string VersionAfter { get; init; } = string.Empty;
    public string ImageName { get; init; } = string.Empty;
    public long Bytes { get; init; }

    /// <summary>USB, SERIAL or SSH.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>SSH or SERIAL.</summary>
    public string Connection { get; init; } = string.Empty;
    public string IP { get; init; } = string.Empty;
    public string PortCom { get; init; } = string.Empty;

    /// <summary>"algorithm SHA256:... (how)", or empty when there was no SSH.</summary>
    public string Fingerprint { get; init; } = string.Empty;
    public string Md5Expected { get; init; } = string.Empty;
    public string Md5Local { get; init; } = string.Empty;
    public string Md5Switch { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;
    public IReadOnlyList<PushCheck> Actions { get; init; } = [];
    public IReadOnlyList<PushCheck> Verification { get; init; } = [];
}

/// <summary>Data of the push record.</summary>
public sealed record PushRecordData
{
    public string Ticket { get; init; } = string.Empty;
    public string Responsible { get; init; } = string.Empty;
    public string UserWindows { get; init; } = Environment.UserName;
    public string DeviceWork { get; init; } = Environment.MachineName;
    public string StartText { get; init; } = string.Empty;
    public string EndText { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;
    public string Connection { get; init; } = string.Empty;
    public string UserSwitch { get; init; } = string.Empty;
    public string Groups { get; init; } = string.Empty;
    public string Save { get; init; } = string.Empty;
    public string View { get; init; } = string.Empty;
    public IReadOnlyList<SwitchResult> Results { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;
}
