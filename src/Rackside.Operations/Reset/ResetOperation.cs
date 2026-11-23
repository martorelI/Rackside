using System.Globalization;
using System.Text;
using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Core.Restore;
using Rackside.Core.Security;
using Rackside.Records;
using Rackside.Transport.Console;

namespace Rackside.Operations.Reset;

/// <summary>What the operator enters in "Factory reset".</summary>
public sealed record ResetRun
{
    public string PortCom { get; init; } = string.Empty;
    public int BaudRate { get; init; } = 9600;
    public string ConsoleUser { get; init; } = string.Empty;
    public string ConsolePassword { get; init; } = string.Empty;
    public string ConsoleEnable { get; init; } = string.Empty;
    public string FolderRecord { get; init; } = string.Empty;
    public string Ticket { get; init; } = string.Empty;
    public string Responsible { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;

    /// <summary>Wait for the "Switch>" prompt after the reload.</summary>
    public bool WaitBoot { get; init; } = true;
}

/// <summary>What was read from the device and what will be kept or deleted. Changes nothing.</summary>
public sealed class ResetInfo
{
    public required string Hostname { get; init; }
    public required string Model { get; init; }
    public required string Serial { get; init; }
    public required string OS { get; init; }
    public required string Version { get; init; }
    public required int Members { get; init; }
    public required string Boot { get; init; }
    public required string Filesystem { get; init; }

    /// <summary>The IOS image and what it boots from: never deleted (security rule 7).</summary>
    public required IReadOnlyList<string> Protected { get; init; }

    public required IReadOnlyList<string> ToDelete { get; init; }
    public required string Running { get; init; }
}

/// <summary>The result of the review: backup taken, evidence folder named after the hostname, summary for the operator.</summary>
public sealed class ResetReview
{
    public required ResetInfo Info { get; init; }
    public required ResetRun Run { get; init; }
    public required string Folder { get; init; }
    public required string PathBackup { get; init; }
    public required string Summary { get; init; }

    /// <summary>The device was already erased and reloaded: it cannot be repeated without reviewing again.</summary>
    public bool Done { get; set; }
}

public enum ResetOutcome
{
    Failed,
    Cancelled,
    SuccessfulWithNotes,
    Successful,
}

public static class ResetOutcomeExtensions
{
    public static string Code(this ResetOutcome o) => o switch
    {
        ResetOutcome.Failed => "FAILED",
        ResetOutcome.Cancelled => "CANCELLED",
        ResetOutcome.SuccessfulWithNotes => "SUCCESSFUL WITH NOTES",
        ResetOutcome.Successful => "SUCCESSFUL",
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };
}

/// <summary>What the erase record needs.</summary>
public sealed record ResetResult(ResetReview Review, ResetOutcome Outcome, string ErrorFatal, IReadOnlyList<PushCheck> Actions, IReadOnlyList<PushCheck> Verification, DateTime Start, DateTime End, string RecordPath);

/// <summary>
/// "Factory reset". Security rule 7: confirmation typing the exact
/// hostname (case-sensitive), backup first, keeps the IOS image, reloads without saving.
/// </summary>
public sealed class ResetOperation(Translator t, SecretMasker masker, IOperatorUi ui, Func<string, int, IConsoleChannel>? openPort = null, CancellationToken cancel = default)
{
    /// <summary>Stop button of the current run (the window sets a new one before each run).</summary>
    public CancellationToken Cancellation { get; set; } = cancel;

    private readonly Func<string, int, IConsoleChannel> open = openPort ?? ((port, baud) => SerialChannel.Open(port, baud, t));

    public OperationLog Log { get; } = new(t, masker);

    private static string Safe(string s) => Ci.Replace(s, "[\\\\/:*?\"<>|]", "_");

    private SwitchConnection Connect(ResetRun run, string folder)
    {
        var c = SwitchConnection.Over(open(run.PortCom, run.BaudRate), Path.Combine(folder, "console_session.txt"),
            new Credentials(run.ConsoleUser, run.ConsolePassword, run.ConsoleEnable), t, masker, Cancellation);
        Log.Ok(Ci.F(t.T("Connected via {0}: {1}#"), run.PortCom, c.Hostname));
        return c;
    }

    /// <summary>Reads the device and decides what is kept (IOS) and what is erased. Changes nothing.</summary>
    private ResetInfo Read(SwitchConnection c)
    {
        var s = c.Session;
        var d = c.Version();
        var members = Ci.Split(s.RunCommand("show switch", 30), "\\r?\\n").Count(l => Ci.IsMatch(l, "^\\s*\\*?\\s*\\d+\\s+(Active|Master|Member|Standby)\\b"));
        members = Math.Max(members, 1);
        var bootTop = string.Empty;
        if (Ci.TryMatch(s.RunCommand("show boot", 30), "(?im)^\\s*BOOT (?:path-list|variable)\\s*[:=]\\s*(\\S+)", out var mb))
        {
            var value = Ci.Split(mb.Groups[1].Value, "[;,]")[0];
            bootTop = Ci.Split(Ci.Replace(value, "^[A-Za-z0-9\\-]+:/*", string.Empty), "/")[0];
        }
        var dir = DirListing.Parse(s.RunCommand("dir", 60));
        if (dir.Filesystem.Length == 0)
        {
            throw new SwitchException(t.T("Could not detect the filesystem in the \"dir\" output."));
        }
        var prot = dir.Entries.Where(e => Ci.IsMatch(e.Name, "(?i)\\.(bin|pkg)$") || Ci.Eq(e.Name, "packages.conf")
                || (e.IsDir && Ci.IsMatch(e.Name, "(?i)^(c\\d{4}|cat\\d|ies|s\\d{4})")) || (bootTop.Length > 0 && Ci.Eq(e.Name, bootTop)))
            .Select(e => e.Name).ToList();
        if (prot.Count == 0)
        {
            throw new SwitchException(Ci.F(t.T("No IOS image found on {0}:. Nothing is deleted."), dir.Filesystem));
        }
        if (bootTop.Length > 0 && !dir.Entries.Any(e => Ci.Eq(e.Name, bootTop)))
        {
            throw new SwitchException(Ci.F(t.T("BOOT points to \"{0}\" but it does not exist on {1}:. Nothing is deleted."), bootTop, dir.Filesystem));
        }
        string[] candidates = ["vlan.dat", "private-config.text", "config.text.backup", "config.text.renamed"];
        var toDelete = dir.Entries.Where(e => !e.IsDir && candidates.Contains(e.Name, StringComparer.OrdinalIgnoreCase) && !prot.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
            .Select(e => e.Name).ToList();
        var run = s.RunCommand("show running-config", 300);
        if (!Ci.IsMatch(run, "(?m)^hostname\\s+\\S+") || !Ci.IsMatch(run, "(?m)^end\\s*$"))
        {
            throw new SwitchException(t.T("The running-config came out incomplete; not continuing without a backup."));
        }
        return new ResetInfo
        {
            Hostname = c.Hostname, Model = d.Model, Serial = d.Serial, OS = d.OS, Version = d.Version, Members = members, Boot = bootTop,
            Filesystem = dir.Filesystem, Protected = prot, ToDelete = toDelete, Running = run,
        };
    }

    /// <summary>Reads the device, backs up its running-config and names the evidence folder after it.</summary>
    public ResetReview Review(ResetRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        masker.Register(run.ConsolePassword, run.ConsoleEnable);
        if (string.IsNullOrWhiteSpace(run.PortCom))
        {
            throw new FormatException(t.T("Enter the COM port."));
        }
        if (string.IsNullOrWhiteSpace(run.FolderRecord))
        {
            throw new FormatException(t.T("Enter the evidence folder."));
        }
        // Provisional folder: renamed with the hostname once it is known
        var mark = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(run.FolderRecord, "RESET_" + mark);
        Directory.CreateDirectory(folder);
        Log.Paths.Clear();
        Log.Paths.Add(Path.Combine(run.FolderRecord, "FactoryReset.log"));
        Log.Paths.Add(Path.Combine(folder, "log.txt"));
        Log.Info(t.T("===== Review for factory reset ====="));
        ui.Status(Ci.F(t.T("Connecting via {0}..."), run.PortCom), 5);
        ResetInfo info;
        string nameBackup;
        using (var c = Connect(run, folder))
        {
            ui.Status(t.T("Reading the device and backing up its configuration..."), 40);
            info = Read(c);
            nameBackup = Safe(Ci.F("{0}_{1}_running_{2}.txt", info.Hostname, info.Serial.Length > 0 ? info.Serial : "SN", mark));
            File.WriteAllText(Path.Combine(folder, nameBackup), info.Running, new UTF8Encoding(false));
        }
        var renamed = Path.Combine(run.FolderRecord, Safe(Ci.F("RESET_{0}_{1}", info.Hostname, mark)));
        Directory.Move(folder, renamed);
        folder = renamed;
        Log.Paths[1] = Path.Combine(folder, "log.txt");
        var pathBackup = Path.Combine(folder, nameBackup);
        Log.Ok(Ci.F(t.T("running-config backup: {0}"), pathBackup));

        var fs = info.Filesystem;
        var sb = new StringBuilder();
        void L(string x) => sb.Append(x).Append("\r\n");
        L(Ci.F(t.T("Hostname      : {0}"), info.Hostname));
        L(Ci.F(t.T("Model         : {0}"), info.Model));
        L(Ci.F(t.T("Serial        : {0}"), info.Serial));
        L(Ci.F(t.T("System        : {0} {1}"), info.OS, info.Version));
        L(Ci.F(t.T("Stack         : {0} member(s){1}"), info.Members, info.Members > 1 ? t.T("  <- the reload restarts the whole stack") : string.Empty));
        L(string.Empty);
        L(Ci.F(t.T("Kept          : {0}"), string.Join(", ", info.Protected.Select(x => fs + ":" + x))));
        L(Ci.F(t.T("Deleted       : startup-config, RSA keys{0}"), info.ToDelete.Count > 0 ? ", " + string.Join(", ", info.ToDelete.Select(x => fs + ":" + x)) : string.Empty));
        L(Ci.F(t.T("Then          : reload without saving{0}"), run.WaitBoot ? t.T(" and wait for the \"Switch>\" prompt") : string.Empty));
        L(string.Empty);
        L(Ci.F(t.T("Backup        : {0}"), pathBackup));
        ui.Status(Ci.F(t.T("Device {0} reviewed. Type its hostname to enable \"Factory reset\"."), info.Hostname), 100);
        return new ResetReview { Info = info, Run = run, Folder = folder, PathBackup = pathBackup, Summary = sb.ToString() };
    }

    /// <summary>The button is enabled only with the exact hostname typed (case-sensitive) and only once per review.</summary>
    public static bool CanReset(ResetReview? review, string? typed) =>
        review is not null && !review.Done && string.Equals(typed, review.Info.Hostname, StringComparison.Ordinal);

    /// <summary>Erases and reloads. Returns null if the operator did not confirm.</summary>
    public ResetResult? Reset(ResetReview review, string typedHostname)
    {
        ArgumentNullException.ThrowIfNull(review);
        var info = review.Info;
        if (!CanReset(review, typedHostname))
        {
            throw new FormatException(t.T("Type the exact hostname of the device to confirm."));
        }
        if (!ui.Ask(Ci.F(t.T("The configuration of {0} (model {1}, serial {2}) will be ERASED and the switch will reload. This cannot be undone.{3}{3}Previous backup:{3}{4}{3}{3}Continue?"),
            info.Hostname, info.Model, info.Serial, "\r\n", review.PathBackup), t.T("Factory reset")))
        {
            return null;
        }
        var run = review.Run;
        var start = DateTime.Now;
        var actions = new List<PushCheck>();
        var verification = new List<PushCheck>();
        var final = ResetOutcome.Failed;
        var errorFatal = string.Empty;
        var reloadSent = false;
        void Step(string action, string result, string detail)
        {
            actions.Add(new PushCheck(action, result, detail));
            Log.Write(result == "OK" ? "OK" : "WARN", Ci.F("{0}: {1} {2}", action, t.T(result), detail));
        }
        void Verif(string check, bool ok, string detail) => verification.Add(new PushCheck(check, ok ? "OK" : "FAIL", detail));
        Log.Info(Ci.F(t.T("===== Factory reset: {0} ====="), info.Hostname));
        try
        {
            ui.Status(t.T("Connecting..."), 5);
            using var c = Connect(run, review.Folder);
            var s = c.Session;
            var d2 = c.Version();
            if (info.Serial.Length > 0 && !Ci.Eq(d2.Serial, info.Serial))
            {
                throw new SwitchException(Ci.F(t.T("The connected device (serial {0}) is not the one that was reviewed (serial {1}). Review again."), d2.Serial, info.Serial));
            }
            var fs = info.Filesystem;

            ui.Status(t.T("Deleting keys and configuration files..."), 20);
            s.SendConfigLine("configure terminal");
            s.SendConfigLine("no logging console");
            var rz = s.SendConfigLine("crypto key zeroize rsa", 60, [("(?i)remove these keys\\?\\s*\\[yes/no\\]:\\s*$", "yes")]);
            s.SendConfigLine("end");
            if (rz.Errors.Count > 0) { Step(t.T("Delete RSA keys"), "NOTE", string.Join(" / ", rz.Errors)); }
            else if (Ci.IsMatch(rz.Output, "(?i)No .*keys? found")) { Step(t.T("Delete RSA keys"), "OK", t.T("There were no keys")); }
            else { Step(t.T("Delete RSA keys"), "OK", t.T("Keys deleted")); }

            foreach (var file in info.ToDelete)
            {
                var rd = s.SendConfigLine(Ci.F("delete /force {0}:{1}", fs, file), 60);
                if (Ci.TryMatch(rd.Output, "(?i)%\\s*Error.*", out var me)) { Step(Ci.F(t.T("Delete {0}:{1}"), fs, file), "NOTE", me.Value.Trim()); }
                else { Step(Ci.F(t.T("Delete {0}:{1}"), fs, file), "OK", string.Empty); }
            }

            ui.Status(t.T("Erasing startup-config (write erase)..."), 40);
            var rw = s.SendConfigLine("write erase", 120, [("(?i)remove all configuration files!?\\s*Continue\\?\\s*\\[confirm\\]\\s*$", string.Empty)]);
            if (!Ci.IsMatch(rw.Output, "(?i)\\[OK\\]|complete"))
            {
                throw new SwitchException(Ci.F(t.T("write erase did not confirm. Output: {0}"), Ci.Replace(rw.Output, "[\\r\\n]+", " | ").Trim()));
            }
            Step(t.T("Erase startup-config (write erase)"), "OK", t.T("Erase of nvram: complete"));

            ui.Status(t.T("Verifying before reloading..."), 55);
            var okStartup = Ci.IsMatch(s.SendConfigLine("show startup-config", 60).Output, "(?i)not present");
            Verif(t.T("No startup-config"), okStartup, okStartup ? t.T("startup-config is not present") : t.T("Still present"));
            var names2 = DirListing.Parse(s.SendConfigLine("dir", 60).Output).Entries.Select(e => e.Name).ToList();
            var missingIos = info.Protected.Where(x => !names2.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            Verif(t.T("IOS image kept"), missingIos.Count == 0, missingIos.Count == 0 ? string.Join(", ", info.Protected) : t.T("Missing: ") + string.Join(", ", missingIos));
            var remaining = info.ToDelete.Where(x => names2.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            Verif(t.T("Configuration files deleted"), remaining.Count == 0, remaining.Count == 0 ? t.T("vlan.dat and the rest deleted") : t.T("Still there: ") + string.Join(", ", remaining));
            if (!okStartup || missingIos.Count > 0 || remaining.Count > 0)
            {
                throw new SwitchException(t.T("The pre-check failed; the device was NOT reloaded. Check the log."));
            }

            ui.Status(t.T("Reloading without saving..."), 65);
            s.ReloadWithoutSaving();
            reloadSent = true;
            review.Done = true;
            Step(t.T("Reload without saving (reload)"), "OK", t.T("Answered \"no\" to saving"));

            if (run.WaitBoot)
            {
                ui.Status(t.T("Waiting for boot (5 to 15 minutes)..."), 75);
                (string, string)[] patBoot =
                [
                    ("Rommon", "(?:^|\\n)\\s*(switch:|rommon\\s*\\d*\\s*>)\\s*$"),
                    ("Dialog", "(?i)initial configuration dialog\\?\\s*\\[yes/no\\]:\\s*$"),
                    ("Basic", "(?i)basic management setup\\?\\s*\\[yes/no\\]:\\s*$"),
                    ("Autoinst", "(?i)terminate autoinstall\\?\\s*\\[yes\\]:\\s*$"),
                    ("Return", "(?i)Press RETURN to get started"),
                    ("Ready", "(?:^|\\n)\\s*([A-Za-z0-9][\\w\\.\\-]*)>\\s*$"),
                ];
                var limit = DateTime.UtcNow.AddMinutes(20);
                var prompt = string.Empty;
                while (DateTime.UtcNow < limit && prompt.Length == 0)
                {
                    var left = (int)Math.Max(10, (limit - DateTime.UtcNow).TotalSeconds);
                    var r = s.Wait(patBoot, left, silenceMs: 3000)!;
                    switch (r.Key)
                    {
                        case "Rommon": throw new SwitchException(t.T("The device was left in ROMMON after the reload. Check it by hand on the console."));
                        case "Dialog" or "Basic": s.SendLine("no"); break;
                        case "Autoinst" or "Return": s.SendLine(string.Empty); break;
                        case "Ready": prompt = r.Match.Groups[1].Value; break;
                    }
                }
                if (prompt.Length == 0)
                {
                    throw new SwitchException(t.T("The prompt did not appear after 20 minutes. Check the console."));
                }
                Verif(t.T("Boot without configuration"), prompt == "Switch", Ci.F(t.T("Prompt \"{0}>\""), prompt));
            }
            else
            {
                verification.Add(new PushCheck(t.T("Boot without configuration"), "NOTE", t.T("Did not wait for boot (option unchecked)")));
            }
            final = verification.Any(v => v.Result != "OK") || actions.Any(a => a.Result != "OK") ? ResetOutcome.SuccessfulWithNotes : ResetOutcome.Successful;
        }
        catch (OperationCanceledException)
        {
            if (reloadSent)
            {
                final = ResetOutcome.SuccessfulWithNotes;
                errorFatal = t.T("The device was already erased and reloaded; the operator stopped waiting for boot.");
                verification.Add(new PushCheck(t.T("Boot without configuration"), "NOTE", t.T("Wait stopped by the operator")));
            }
            else
            {
                final = ResetOutcome.Cancelled;
                errorFatal = t.T("Stopped by the operator before the reload; check the device state.");
            }
            Log.Error(errorFatal);
        }
        catch (Exception ex) when (ex is SwitchException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or FormatException)
        {
            errorFatal = masker.Protect(ex.Message);
            Log.Error(errorFatal);
        }
        var end = DateTime.Now;
        var recordPath = RecordWriter.Write(Path.Combine(review.Folder, RecordWriter.FileName("Reset", info.Hostname, start)), () => new HtmlRecords(t).Reset(new ResetRecordData
        {
            Result = final.Code(), ErrorFatal = errorFatal, Ticket = run.Ticket, Responsible = run.Responsible, Reason = run.Reason,
            StartText = RecordWriter.Stamp(start), EndText = RecordWriter.Stamp(end), Duration = RecordWriter.Duration(start, end),
            Hostname = info.Hostname, Model = info.Model, Serial = info.Serial, OS = info.OS, Version = info.Version, Members = info.Members, Filesystem = info.Filesystem,
            Protected = info.Protected, BackupPath = review.PathBackup, BackupHash = HtmlRecords.Sha256(review.PathBackup), Folder = review.Folder, Actions = actions, Verification = verification,
        }), Log, t);
        Log.Info(Ci.F(t.T("===== Result: {0} ====="), t.T(final.Code())));
        ui.Status(Ci.F(t.T("Factory reset: {0}. Evidence in {1}"), t.T(final.Code()), review.Folder), 100);
        return new ResetResult(review, final, errorFatal, actions, verification, start, end, recordPath);
    }
}
