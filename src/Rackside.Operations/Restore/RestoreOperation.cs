using System.Globalization;
using System.Text;
using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Core.Restore;
using Rackside.Core.Security;
using Rackside.Records;
using Rackside.Transport.Console;

namespace Rackside.Operations.Restore;

/// <summary>Everything the operator enters in "Restore from backup".</summary>
public sealed record RestoreRun
{
    public required RestoreOptions Options { get; init; }

    public string PortCom { get; init; } = string.Empty;

    public int BaudRate { get; init; } = 9600;

    public string ConsoleUser { get; init; } = string.Empty;

    public string ConsolePassword { get; init; } = string.Empty;

    public string ConsoleEnable { get; init; } = string.Empty;

    /// <summary>Repeated password and enable (they must match).</summary>
    public string PasswordAdmin2 { get; init; } = string.Empty;

    public string EnableSecret2 { get; init; } = string.Empty;

    public bool GenerateRSA { get; init; }

    public bool Save { get; init; } = true;

    public string FolderRecord { get; init; } = string.Empty;

    public string Ticket { get; init; } = string.Empty;

    public string Responsible { get; init; } = string.Empty;

    public string FailedHost { get; init; } = string.Empty;

    public string FailedSerial { get; init; } = string.Empty;

    public string Remarks { get; init; } = string.Empty;
}

/// <summary>The result of "Connect and review": target read, plan built, evidence written. Nothing changed on the switch.</summary>
public sealed class RestoreReview
{
    public required SourceConfig Source { get; init; }
    public required TargetInfo Target { get; init; }
    public required RestoreRun Run { get; set; }
    public required RestorePlan Plan { get; set; }
    public required string Folder { get; set; }
    public string TextConfig { get; set; } = string.Empty;
    public string TextChanges { get; set; } = string.Empty;
    public string TextMapping { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool Applied { get; set; }

    public string PathConfig => Path.Combine(Folder, "applied_config.txt");

    public string PathReport => Path.Combine(Folder, "report.txt");
}

/// <summary>Final state of a restore (internal codes, translated only to show them).</summary>
public enum RestoreOutcome
{
    Failed,
    Cancelled,
    SuccessfulWithNotes,
    Successful,
}

public static class RestoreOutcomeExtensions
{
    public static string Code(this RestoreOutcome o) => o switch
    {
        RestoreOutcome.Failed => "FAILED",
        RestoreOutcome.Cancelled => "CANCELLED",
        RestoreOutcome.SuccessfulWithNotes => "SUCCESSFUL WITH NOTES",
        RestoreOutcome.Successful => "SUCCESSFUL",
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };
}

/// <summary>What the DRP record needs.</summary>
public sealed record RestoreResult(RestoreReview Review, RestoreOutcome Outcome, string ErrorFatal, IReadOnlyList<ApplyIssue> Errors, IReadOnlyList<ApplyIssue> Warnings,
    IReadOnlyList<PushCheck> Verification, bool Saved, string HostFinal, DateTime Start, DateTime End, string RecordPath);

/// <summary>"Restore from backup". Over the console only.</summary>
public sealed class RestoreOperation(Translator t, SecretMasker masker, IOperatorUi ui, Func<string, int, IConsoleChannel>? openPort = null, CancellationToken cancel = default)
{
    /// <summary>Stop button of the current run (the window sets a new one before each run).</summary>
    public CancellationToken Cancellation { get; set; } = cancel;

    private readonly Func<string, int, IConsoleChannel> open = openPort ?? ((port, baud) => SerialChannel.Open(port, baud, t));

    public OperationLog Log { get; } = new(t, masker);

    /// <summary>Fields that change the generated configuration (PlanFields), with their label.</summary>
    private static readonly (string Name, string Label, Func<RestoreOptions, string> Get)[] PlanFields =
    [
        ("ModelSource", "Source model", o => o.ModelSource),
        ("Hostname", "Hostname", o => o.Hostname),
        ("Domain", "Domain", o => o.Domain),
        ("InterfaceManagement", "Management interface", o => o.InterfaceManagement),
        ("IP", "IP address", o => o.IP),
        ("Mask", "Mask", o => o.Mask),
        ("Gateway", "Gateway", o => o.Gateway),
        ("UserAdmin", "Administrator user", o => o.UserAdmin),
        ("PasswordAdmin", "Password", o => o.PasswordAdmin),
        ("EnableSecret", "Enable secret", o => o.EnableSecret),
        ("CreateVlans", "Create VLANs", o => o.CreateVlans ? "True" : "False"),
        ("RemoveCertificate", "Remove certificate", o => o.RemoveCertificate ? "True" : "False"),
        ("IncludeUsers", "Restore the other users", o => o.IncludeUsers ? "True" : "False"),
    ];

    private void Register(RestoreRun run) => masker.Register(run.ConsolePassword, run.ConsoleEnable, run.Options.PasswordAdmin, run.Options.EnableSecret);

    /// <summary>The problems of the data, each with the tab where it is fixed.</summary>
    public IReadOnlyList<(int Tab, string Message)> Validate(SourceConfig? source, RestoreRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var o = run.Options;
        var f = new List<(int, string)>();
        void Add(int tab, string msg) => f.Add((tab, t.T(msg)));
        if (source is null) { Add(0, "Select the backup file."); }
        if (string.IsNullOrWhiteSpace(run.PortCom)) { Add(0, "Enter the COM port."); }
        if (o.Hostname.Length > 0 && !Ci.IsMatch(o.Hostname, "^[A-Za-z][A-Za-z0-9\\-]{0,62}$")) { Add(1, "Invalid hostname (letters, numbers and hyphen; must start with a letter)."); }
        if (o.Domain.Length > 0 && !Ci.IsMatch(o.Domain, "^[A-Za-z0-9][A-Za-z0-9\\.\\-]*$")) { Add(1, "Invalid domain."); }
        if (o.IP.Length > 0)
        {
            if (!Validators.IsIPv4(o.IP)) { Add(1, "Invalid IP address."); }
            if (!Validators.IsMask(o.Mask)) { Add(1, "Invalid mask."); }
            if (!Ci.IsMatch(o.InterfaceManagement, "^[A-Za-z][\\w\\-]*\\d+(/\\d+)*$")) { Add(1, "Invalid management interface."); }
        }
        if (o.Gateway.Length > 0 && !Validators.IsIPv4(o.Gateway)) { Add(1, "Invalid gateway."); }
        if (!string.Equals(o.PasswordAdmin, run.PasswordAdmin2, StringComparison.OrdinalIgnoreCase)) { Add(1, "The user passwords do not match."); }
        if (!string.Equals(o.EnableSecret, run.EnableSecret2, StringComparison.OrdinalIgnoreCase)) { Add(1, "The enable secrets do not match."); }
        if (o.PasswordAdmin.Length > 0 && o.UserAdmin.Length == 0) { Add(1, "You entered a password but no administrator user."); }
        if (o.UserAdmin.Length > 0 && !Ci.IsMatch(o.UserAdmin, "^[A-Za-z0-9][\\w\\.\\-@]{0,63}$")) { Add(1, "Invalid administrator user (letters, numbers, dot, hyphen, underscore or @; no spaces)."); }
        if (Ci.IsMatch(o.PasswordAdmin, "[\\?\\s]") || Ci.IsMatch(o.EnableSecret, "[\\?\\s]")) { Add(1, "Passwords cannot contain spaces or \"?\" (the \"?\" opens help on the console)."); }
        if (string.IsNullOrWhiteSpace(run.FolderRecord)) { Add(2, "Enter the folder where the DRP record will be saved."); }
        return f;
    }

    private void ThrowIfInvalid(SourceConfig? source, RestoreRun run)
    {
        var problems = Validate(source, run);
        if (problems.Count > 0)
        {
            throw new FormatException(string.Join("\r\n", problems.Select(p => p.Message)));
        }
    }

    private static string HostOf(RestoreRun run, SourceConfig source) =>
        Ci.Replace(run.Options.Hostname.Length > 0 ? run.Options.Hostname : source.Hostname, "[\\\\/:*?\"<>|]", "_");

    /// <summary>Evidence folder DRP_&lt;host&gt;_&lt;date&gt;; renamed if the hostname changed before applying.</summary>
    private string EvidenceFolder(RestoreRun run, SourceConfig source, RestoreReview? previous)
    {
        var host = HostOf(run, source);
        string folder;
        if (previous is null || previous.Applied || !Directory.Exists(previous.Folder) ||
            !string.Equals(Path.GetDirectoryName(previous.Folder), run.FolderRecord.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            folder = Path.Combine(run.FolderRecord, Ci.F("DRP_{0}_{1}", host, DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)));
        }
        else
        {
            folder = previous.Folder;
            var leaf = Path.GetFileName(folder);
            if (!leaf.StartsWith("DRP_" + host + "_", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = Ci.Replace(leaf, "^.*_(\\d{4}-\\d{2}-\\d{2}_\\d{6})$", "$1");
                var renamed = Path.Combine(Path.GetDirectoryName(folder)!, Ci.F("DRP_{0}_{1}", host, suffix));
                Directory.Move(folder, renamed);
                folder = renamed;
            }
        }
        Directory.CreateDirectory(folder);
        Log.Paths.Clear();
        Log.Paths.Add(Path.Combine(run.FolderRecord, "Restores.log"));
        Log.Paths.Add(Path.Combine(folder, "log.txt"));
        return folder;
    }

    private SwitchConnection Connect(RestoreRun run, string folder)
    {
        var c = SwitchConnection.Over(open(run.PortCom, run.BaudRate), Path.Combine(folder, "console_session.txt"),
            new Credentials(run.ConsoleUser, run.ConsolePassword, run.ConsoleEnable), t, masker, Cancellation);
        Log.Ok(Ci.F(t.T("Connected via {0}: {1}#"), run.PortCom, c.Hostname));
        return c;
    }

    /// <summary>"Connect and review": reads the target and builds the plan. Changes nothing on the switch.</summary>
    public RestoreReview Review(SourceConfig source, RestoreRun run)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(run);
        Register(run);
        ThrowIfInvalid(source, run);
        var folder = EvidenceFolder(run, source, null);
        Log.Info(Ci.F(t.T("===== Review: backup {0} ====="), source.Path));
        ui.Status(Ci.F(t.T("Connecting via {0}..."), run.PortCom), 5);
        TargetInfo target;
        using (var c = Connect(run, folder))
        {
            ui.Status(t.T("Reading the target switch..."), 30);
            target = c.Target(Log);
        }
        var physical = target.Interfaces.Count(i => InterfaceInfo.Parse(i) is { IsManagement: false });
        Log.Info(Ci.F(t.T("Target: {0} {1}, model \"{2}\", serial {3}, {4} physical interfaces"), target.OS, target.Version, target.Model, target.Serial, physical));
        if (physical == 0)
        {
            throw new SwitchException(t.T("Could not read the target interfaces (show ip interface brief)."));
        }
        ui.Status(t.T("Generating the configuration..."), 70);
        var review = new RestoreReview { Source = source, Target = target, Run = run, Plan = null!, Folder = folder };
        Build(review);
        Log.Ok(Ci.F(t.T("Plan ready: mode {0}, {1} lines. Check tab 4 and click \"Apply to switch\". If you change data, the configuration is regenerated automatically."), t.T(review.Plan.ModeCode), review.Plan.Lines));
        ui.Status(Ci.F(t.T("Review ready: {0} lines in mode {1}. Check it and click \"Apply to switch\"."), review.Plan.Lines, t.T(review.Plan.ModeCode)), 100);
        return review;
    }

    private static void WriteCrlf(string path, string text) => File.WriteAllText(path, Ci.Replace(text, "\\r?\\n", "\r\n"), new UTF8Encoding(false));

    /// <summary>Builds the plan with the data and the target already read, and writes the evidence.</summary>
    private void Build(RestoreReview r)
    {
        var plan = RestorePlanner.Build(r.Source, r.Target, r.Run.Options, t, masker);
        r.Plan = plan;
        r.TextConfig = RestoreTexts.Config(plan, masker, r.Run.Options.PasswordAdmin, r.Run.Options.EnableSecret);
        r.TextMapping = RestoreTexts.Mapping(plan, t);
        r.TextChanges = RestoreTexts.Changes(plan, t);
        if (plan.Mapping is not null)
        {
            var csv = new StringBuilder();
            csv.Append(Csv.Line(["Source", "Target", "Role", "Member"])).Append("\r\n");
            foreach (var m in plan.Mapping.Rows)
            {
                csv.Append(Csv.Line([m.Source, m.Target, m.Role, m.Member.ToString(CultureInfo.InvariantCulture)])).Append("\r\n");
            }
            File.WriteAllText(Path.Combine(r.Folder, "interface_mapping.csv"), csv.ToString(), new UTF8Encoding(true));
        }
        var desc = plan.Mode == RestoreMode.Direct ? t.T("same model, applied as is") : t.T("conversion ") + plan.Direction;
        var rep = plan.Report;
        r.Summary = Ci.F(t.T("Mode: {0} ({1})"), t.T(plan.ModeCode), desc) + "\r\n" +
            Ci.F(t.T("Source: {0} {1}, model \"{2}\"   ->   Target: {3} {4}, model \"{5}\", serial {6}"), r.Source.OS, r.Source.Version, r.Run.Options.ModelSource, r.Target.OS, r.Target.Version, r.Target.Model, r.Target.Serial) + "\r\n" +
            Ci.F(t.T("Blocked for safety: {6}  |  Lines to send: {0}  |  Removed: {1}  |  Changed: {2}  |  No equivalent: {3}  |  Added: {4}  |  Warnings: {5}"), plan.Lines,
                rep.Count(ReportType.Removed), rep.Count(ReportType.Changed), rep.Count(ReportType.NoMapping), rep.Count(ReportType.Added), rep.Count(ReportType.Warning), rep.Count(ReportType.Blocked));
        WriteCrlf(r.PathConfig, r.TextConfig);
        WriteCrlf(r.PathReport, r.Summary + "\r\n\r\n" + r.TextChanges + "\r\n===== MAPPING =====\r\n" + r.TextMapping);
    }

    /// <summary>What changed since the review, for the confirmation and the log.</summary>
    public IReadOnlyList<string> ChangesSince(RestoreRun current, RestoreRun reviewed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(reviewed);
        var dif = new List<string>();
        foreach (var (name, label, get) in PlanFields)
        {
            var before = get(reviewed.Options);
            var now = get(current.Options);
            if (string.Equals(now, before, StringComparison.Ordinal))
            {
                continue;
            }
            if (name is "PasswordAdmin" or "EnableSecret")
            {
                dif.Add(Ci.F(t.T("{0}: changed"), t.T(label)));
            }
            else
            {
                string Fmt(string v) => v.Length == 0 ? t.T("(empty)") : v == "True" ? t.T("Yes") : v == "False" ? t.T("No") : v;
                dif.Add(Ci.F("{0}: {1} -> {2}", t.T(label), Fmt(before), Fmt(now)));
            }
        }
        return dif;
    }

    /// <summary>Regenerates the configuration with the current data, without reconnecting.</summary>
    public IReadOnlyList<string> Update(RestoreReview review, RestoreRun run)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(run);
        Register(run);
        ThrowIfInvalid(review.Source, run);
        var dif = ChangesSince(run, review.Run);
        review.Folder = EvidenceFolder(run, review.Source, review);
        review.Run = run;
        Build(review);
        if (dif.Count > 0)
        {
            Log.Info(Ci.F(t.T("Configuration regenerated with the changes: {0}"), string.Join("; ", dif)));
        }
        ui.Status(Ci.F(t.T("Configuration updated with your changes: {0} lines."), review.Plan.Lines));
        return dif;
    }

    /// <summary>
    /// "Apply to switch", always with the current data. Returns null if the operator did not confirm.
    /// </summary>
    public RestoreResult? Apply(RestoreReview review, RestoreRun run)
    {
        ArgumentNullException.ThrowIfNull(review);
        var dif = Update(review, run);
        var opt = review.Run;
        var plan = review.Plan;
        var target = review.Target;
        var text = dif.Count > 0 ? Ci.F(t.T("The configuration was regenerated with your changes:{0}  - {1}{0}{0}"), "\r\n", string.Join("\r\n  - ", dif)) : string.Empty;
        if (!target.Clean)
        {
            text += Ci.F(t.T("WARNING: the switch was not at factory defaults when reviewed ({0}). Whatever is not in the backup will remain.{1}{1}"), target.Remains, "\r\n");
        }
        var hostname = opt.Options.Hostname.Length > 0 ? opt.Options.Hostname : review.Source.Hostname;
        if (!ui.Ask(Ci.F(t.T("{0}{1} lines will be sent to switch {2} (serial {3}) via {4}, with hostname {5}.{6}{6}Continue?"), text, plan.Lines, target.Model, target.Serial, opt.PortCom, hostname, "\r\n"), t.T("Restore from backup")))
        {
            return null;
        }

        var start = DateTime.Now;
        ApplyResult? result = null;
        var errors = new List<ApplyIssue>();
        var verification = new List<PushCheck>();
        var critical = new HashSet<PushCheck>();
        var errorFatal = string.Empty;
        var final = RestoreOutcome.Failed;
        var saved = false;
        bool? rsaOk = null;
        var hostFinal = opt.Options.Hostname;
        Log.Info(Ci.F(t.T("===== Apply: {0} lines, mode {1} ====="), plan.Lines, t.T(plan.ModeCode)));
        try
        {
            ui.Status(t.T("Connecting..."), 0);
            using var c = Connect(opt, review.Folder);
            var s = c.Session;
            var d2 = c.Version();
            if (target.Serial.Length > 0 && !Ci.Eq(d2.Serial, target.Serial))
            {
                throw new SwitchException(Ci.F(t.T("The connected switch (serial {0}) is not the one that was reviewed (serial {1}). Review again."), d2.Serial, target.Serial));
            }
            result = s.ApplyPlan(plan.Blocks, plan.Lines, (txt, pct) => ui.Status(txt, pct));
            errors.AddRange(result.Errors);
            Log.Info(Ci.F(t.T("Apply finished: {0} error(s), {1} warning(s)."), result.Errors.Count, result.Warnings.Count));

            if (opt.GenerateRSA)
            {
                ui.Status(t.T("Generating 2048-bit RSA keys (may take a couple of minutes)..."), 100);
                s.SendConfigLine("configure terminal");
                var rr = s.SendConfigLine("crypto key generate rsa modulus 2048", 400, [("(?i)replace them\\?\\s*\\[yes/no\\]:\\s*$", "yes")]);
                s.SendConfigLine("end");
                if (rr.Errors.Count > 0)
                {
                    rsaOk = false;
                    errors.Add(new ApplyIssue("crypto key generate rsa modulus 2048", string.Join(" / ", rr.Errors)));
                    Log.Warn(Ci.F(t.T("RSA: {0}"), string.Join(" / ", rr.Errors)));
                }
                else
                {
                    rsaOk = true;
                    Log.Ok(t.T("RSA keys generated."));
                }
            }

            ui.Status(t.T("Verifying..."));
            var ver = s.SendConfigLine("show running-config | include ^hostname|^username|^enable|^ip domain", 120).Output;
            if (Ci.TryMatch(ver, "(?m)^hostname\\s+(\\S+)", out var mh))
            {
                hostFinal = mh.Groups[1].Value;
            }
            PushCheck Check(string check, bool ok, string detail, string notOk = "FAIL") => new(t.T(check), ok ? "OK" : notOk, detail);
            if (opt.Options.Hostname.Length > 0)
            {
                verification.Add(Check("Hostname", Ci.IsMatch(ver, Ci.F("(?m)^hostname {0}\\s*$", System.Text.RegularExpressions.Regex.Escape(opt.Options.Hostname))), hostFinal));
            }
            if (opt.Options.UserAdmin.Length > 0)
            {
                var ok = Ci.IsMatch(ver, Ci.F("(?m)^username {0}\\s", System.Text.RegularExpressions.Regex.Escape(opt.Options.UserAdmin)));
                var v = Check("Administrator user", ok, ok ? opt.Options.UserAdmin : Ci.F(t.T("{0} does NOT exist: create it before closing the console"), opt.Options.UserAdmin));
                verification.Add(v);
                critical.Add(v);
            }
            var okEnable = Ci.IsMatch(ver, "(?m)^enable (secret|password)");
            verification.Add(Check("Enable secret", okEnable, okEnable ? t.T("Configured") : t.T("No enable secret"), "NOTE"));
            if (opt.Options.IP.Length > 0)
            {
                var brief = s.SendConfigLine("show ip interface brief | include " + opt.Options.InterfaceManagement, 60).Output;
                var v = new PushCheck(Ci.F(t.T("Management IP ({0})"), opt.Options.InterfaceManagement), Ci.IsMatch(brief, System.Text.RegularExpressions.Regex.Escape(opt.Options.IP)) ? "OK" : "FAIL",
                    Ci.F("{0} {1}", opt.Options.IP, opt.Options.Mask));
                verification.Add(v);
                critical.Add(v);
            }
            if (rsaOk is { } rsa)
            {
                verification.Add(Check("RSA keys for SSH", rsa, t.T("2048 bits")));
            }
            verification.Add(Check("Switch errors while applying", errors.Count == 0, Ci.F(t.T("{0} line(s) rejected"), errors.Count), "NOTE"));

            var failures = verification.Count(v => v.Result == "FAIL");
            PushCheck savedCheck;
            if (opt.Save)
            {
                var toDo = true;
                if (errors.Count > 0 || failures > 0)
                {
                    toDo = ui.Ask(Ci.F(t.T("There were {0} switch error(s) and {1} verification failure(s) (details in tab 5).{2}{2}Save to startup-config anyway?"), errors.Count, failures, "\r\n"), t.T("Restore from backup"));
                }
                if (toDo)
                {
                    ui.Status(t.T("Saving (write memory)..."));
                    saved = Ci.IsMatch(s.SendConfigLine("write memory", 180).Output, "\\[OK\\]");
                }
                savedCheck = Check("Saved to startup-config", saved, saved ? t.T("write memory [OK]") : toDo ? t.T("write memory did not confirm [OK]") : t.T("Not saved by operator decision"));
                critical.Add(savedCheck);
            }
            else
            {
                savedCheck = new PushCheck(t.T("Saved to startup-config"), "NOTE", t.T("Option unchecked: the configuration is lost if the switch reloads"));
            }
            verification.Add(savedCheck);

            failures = verification.Count(v => v.Result == "FAIL");
            var notes = verification.Count(v => v.Result == "NOTE");
            final = failures > 0
                ? (verification.Any(v => v.Result == "FAIL" && critical.Contains(v)) ? RestoreOutcome.Failed : RestoreOutcome.SuccessfulWithNotes)
                : notes > 0 ? RestoreOutcome.SuccessfulWithNotes : RestoreOutcome.Successful;
        }
        catch (OperationCanceledException)
        {
            final = RestoreOutcome.Cancelled;
            errorFatal = t.T("Stopped by the operator in the middle of the apply: the switch configuration is incomplete.");
            Log.Error(errorFatal);
        }
        catch (Exception ex) when (ex is SwitchException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or FormatException)
        {
            errorFatal = masker.Protect(ex.Message);
            Log.Error(errorFatal);
        }
        finally
        {
            review.Applied = true;
        }
        var end = DateTime.Now;
        var warnings = result?.Warnings ?? [];
        // Append errors to the text report (if it fails, the DRP record is generated anyway)
        try
        {
            var sb = new StringBuilder("\r\n");
            sb.Append(Ci.F(t.T("===== RESULT: {0} ====="), t.T(final.Code()))).Append("\r\n");
            if (errorFatal.Length > 0)
            {
                sb.Append(errorFatal).Append("\r\n");
            }
            sb.Append(Ci.F(t.T("===== SWITCH ERRORS ({0}) ====="), errors.Count)).Append("\r\n");
            foreach (var e in errors)
            {
                sb.Append(masker.Protect(e.Line)).Append("\r\n").Append("    -> ").Append(e.Answer).Append("\r\n");
            }
            sb.Append(t.T("===== VERIFICATION =====")).Append("\r\n");
            foreach (var v in verification)
            {
                sb.Append(Ci.F("{0}: {1} ({2})", v.Check, v.Result, v.Detail)).Append("\r\n");
            }
            File.AppendAllText(review.PathReport, sb.ToString(), new UTF8Encoding(false));
        }
        catch (IOException ex)
        {
            Log.Warn(Ci.F(t.T("Could not complete report.txt: {0}"), ex.Message));
        }
        var recordPath = DrpRecord(review, final, errorFatal, errors, warnings, verification, saved, hostFinal, start, end);
        Log.Info(Ci.F(t.T("===== Result: {0} ({1} switch error(s), saved={2}) ====="), t.T(final.Code()), errors.Count, saved ? "True" : "False"));
        ui.Status(Ci.F(t.T("Result: {0}. DRP record in {1}"), t.T(final.Code()), review.Folder), 100);
        return new RestoreResult(review, final, errorFatal, errors, warnings, verification, saved, hostFinal, start, end, recordPath);
    }

    /// <summary>DRP record (printable HTML). Always generated: full or, if something fails, reduced.</summary>
    private string DrpRecord(RestoreReview review, RestoreOutcome final, string errorFatal, IReadOnlyList<ApplyIssue> errors, IReadOnlyList<ApplyIssue> warnings,
        IReadOnlyList<PushCheck> verification, bool saved, string hostFinal, DateTime start, DateTime end)
    {
        var opt = review.Run;
        var o = opt.Options;
        var source = review.Source;
        var target = review.Target;
        var plan = review.Plan;
        var path = Path.Combine(review.Folder, RecordWriter.FileName("DRP", hostFinal, start));
        var records = new HtmlRecords(t);
        var changes = new List<RecordChange>();
        foreach (var type in Enum.GetValues<ReportType>())
        {
            var items = plan.Report.Items.Where(i => i.Type == type).ToList();
            changes.AddRange(RestoreTexts.Grouped(items).Select(g => new RecordChange(ReportItem.Code(type), g.Line, g.Reason)));
        }
        string Full()
        {
            var arch = new FileInfo(source.Path);
            return records.Drp(new DrpRecordData
            {
                Result = final.Code(), ErrorFatal = errorFatal, Ticket = opt.Ticket, Responsible = opt.Responsible,
                StartText = RecordWriter.Stamp(start), EndText = RecordWriter.Stamp(end), Duration = RecordWriter.Duration(start, end), Remarks = opt.Remarks,
                FailedHost = opt.FailedHost, FailedSerial = opt.FailedSerial, FailedModel = o.ModelSource, FailedOS = Ci.F("{0} {1}", source.OS, source.Version),
                HostnameFinal = hostFinal, ReplacementModel = target.Model, ReplacementSerial = target.Serial, ReplacementOS = Ci.F("{0} {1}", target.OS, target.Version),
                IPManagement = o.IP.Length > 0 ? Ci.F(t.T("{0} {1} on {2}, gateway {3}"), o.IP, o.Mask, o.InterfaceManagement, o.Gateway) : t.T("(no change)"),
                BackupPath = source.Path, BackupDate = arch.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                BackupSize = string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", arch.Length), BackupHash = HtmlRecords.Sha256(source.Path),
                Mode = plan.Mode == RestoreMode.Direct ? t.T("DIRECT (same model, applied as is)") : t.T("CONVERT (") + plan.Direction + ")",
                Lines = plan.Lines, ConfigHash = HtmlRecords.Sha256(review.PathConfig), Folder = review.Folder, Verification = verification,
                Errors = errors.Select(e => new LineAnswer(masker.Protect(e.Line), e.Answer)).ToList(),
                Warnings = warnings.Select(w => new LineAnswer(masker.Protect(w.Line), w.Answer)).ToList(),
                Changes = changes, Mapping = plan.Mapping?.Rows.Select(m => new RecordMapping(m.Source, m.Target, m.Role)).ToList() ?? [], ConfigText = review.TextConfig,
            });
        }
        try
        {
            HtmlRecords.Write(path, Full());
            Log.Ok(Ci.F(t.T("DRP record: {0}"), path));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException)
        {
            Log.Error(Ci.F(t.T("Could not generate the full DRP record: {0}. A reduced one is generated."), ex.Message));
            (string, string)[] min =
            [
                ("Result", t.T(final.Code())), ("Reason", errorFatal), ("Ticket / incident", opt.Ticket), ("Responsible", opt.Responsible),
                ("Windows user / computer", Ci.F("{0} / {1}", Environment.UserName, Environment.MachineName)),
                ("Start / end / duration", Ci.F("{0} / {1} / {2}", RecordWriter.Stamp(start), RecordWriter.Stamp(end), RecordWriter.Duration(start, end))),
                ("Failed switch", Ci.F(t.T("{0}, serial {1}, model {2}"), opt.FailedHost, opt.FailedSerial, o.ModelSource)),
                ("Replacement switch", Ci.F(t.T("{0}, serial {1}, model {2}"), hostFinal, target.Serial, target.Model)),
                ("Backup used", source.Path), ("Switch errors", errors.Count.ToString(CultureInfo.InvariantCulture)),
                ("Saved to startup-config", saved ? t.T("yes") : "no"), ("Evidence folder", review.Folder),
            ];
            return RecordWriter.Write(path, () => records.Minimal(t.T("DRP execution record (reduced)"), min, ex.Message), Log, t,
                "Reduced DRP record: {0}", "The reduced record could not be generated either: {0}");
        }
    }
}
