using System.Globalization;
using System.Text;
using Rackside.Core;
using Rackside.Core.Configure;
using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Core.Restore;
using Rackside.Core.Security;
using Rackside.Records;
using Rackside.Transport.Console;
using Rackside.Transport.Ssh;

namespace Rackside.Operations.Configure;

/// <summary>Connection and save options of "Configure switches".</summary>
public sealed record ConfigureOptions
{
    public bool OverSsh { get; init; } = true;

    /// <summary>"USB" or "serial" (only for the texts).</summary>
    public string ConsoleMedium { get; init; } = "USB";

    public string PortCom { get; init; } = string.Empty;

    public int BaudRate { get; init; } = 9600;

    /// <summary>Simulator pty instead of a real COM port (tests only).</summary>
    public bool Pty { get; init; }

    /// <summary>"Accept new fingerprints" in a sweep (only when nothing is saved for that switch).</summary>
    public bool TrustNew { get; init; }

    public bool Save { get; init; } = true;

    public bool SaveWithErrors { get; init; }

    /// <summary>Continue the sweep after a FAILED switch.</summary>
    public bool Continue { get; init; }

    public string FolderRecord { get; init; } = string.Empty;

    public string Ticket { get; init; } = string.Empty;

    public string Responsible { get; init; } = string.Empty;

    public string ConnectionText(Translator t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return OverSsh ? t.T("SSH (sweep of the checked switches)") : Ci.F(t.T("{0} console {1} at {2} baud"), ConsoleMedium, PortCom, BaudRate);
    }

    public PushRunOptions PreviewOptions(Translator t, IReadOnlyList<ConfTarget>? targets) =>
        new(ConnectionText(t), OverSsh, Save, SaveWithErrors, targets?.Select(d => new PushTarget(d.Name, d.IP, d.Port)).ToList());
}

/// <summary>Where the data of "Configure switches" lives (Inventory/, the fingerprints).</summary>
public sealed record ConfigurePaths(string InventoryFolder, string InventoryCsv, string Fingerprints);

/// <summary>A push ready to run: validated, with its targets, preview and the confirmation the operator must accept.</summary>
public sealed record PreparedPush(PushParameters Parameters, ConfigureOptions Options, IReadOnlyList<ConfTarget> Targets, string Preview, string Confirmation, bool ConfirmationIsWarning);

/// <summary>Everything the push record needs.</summary>
public sealed record PushRun(string Ticket, string Responsible, string UserWindows, string Computer, DateTime Start, DateTime End, string Connection,
    string SwitchUser, string Groups, string Save, string Preview, IReadOnlyList<SwitchResult> Results, string Summary, string Folder)
{
    /// <summary>push_report.html (or the reduced record), empty if neither could be written.</summary>
    public string RecordPath { get; set; } = string.Empty;

    public string Duration
    {
        get
        {
            var d = End - Start;
            return Ci.F("{0:00}:{1:00}:{2:00}", (int)Math.Floor(d.TotalHours), d.Minutes, d.Seconds);
        }
    }
}

/// <summary>"Configure switches": query (documentation) and push of common parameters.</summary>
public sealed class ConfigureOperation(ConfigurePaths paths, Translator t, SecretMasker masker, IOperatorUi ui, CancellationToken cancel = default)
{
    /// <summary>Stop button of the current run (the window sets a new one before each run).</summary>
    public CancellationToken Cancellation { get; set; } = cancel;

    public OperationLog Log { get; } = new(t, masker);

    public Inventory Inventory { get; } = new();

    private string Now(string format) => DateTime.Now.ToString(format, CultureInfo.InvariantCulture);

    private SwitchConnection Connect(ConfigureOptions opt, Credentials cred, ConfTarget target, string folder)
    {
        if (opt.OverSsh)
        {
            if (!SwitchConnection.TcpOpen(target.IP, target.Port, 4000))
            {
                throw new SwitchException(Ci.F(t.T("No response at {0}:{1} (powered off, no network, ACL or SSH disabled)."), target.IP, target.Port));
            }
            var request = new FingerprintRequest(target.IP, target.Port) { TrustFirstUse = opt.TrustNew, Hostname = target.Name };
            return SwitchConnection.OverSsh(request, cred, folder, new FingerprintStore(paths.Fingerprints), t, masker, ui, Log, Cancellation);
        }
        return SwitchConnection.OverConsole(opt.PortCom, opt.BaudRate, opt.Pty, cred, folder, t, masker, Cancellation);
    }

    /// <summary>Reads the connected switch and documents it. Changes nothing.</summary>
    private SwitchDocument Document(SwitchConnection c, string? running = null, TargetInfo? version = null)
    {
        version ??= c.Version();
        var outs = SwitchDocument.Commands.Select(x => c.Session.RunCommand(x.Command, x.Timeout)).ToList();
        running ??= c.FullRunning();
        return SwitchDocument.Build(version, outs[0], outs[1], outs[2], outs[3], running, outs[4], c.Hostname, t);
    }

    /// <summary>Copies what was documented to the row (the target's or, over the console, the hostname's).</summary>
    private void UpdateRow(InventoryRow? row, SwitchDocument? doc, string state)
    {
        if (row is null && doc is not null)
        {
            row = Inventory.Rows.FirstOrDefault(f => Ci.Eq(f["Name"], doc.Hostname) || Ci.Eq(f["Hostname"], doc.Hostname) || (doc.IPManagement.Length > 0 && f["IP"] == doc.IPManagement));
            if (row is null && doc.Hostname.Length > 0)
            {
                row = new InventoryRow();
                row["Name"] = doc.Hostname;
                row["IP"] = doc.IPManagement;
                Inventory.Rows.Add(row);
                Log.Info(Ci.F(t.T("{0} was not in the list: it was added{1}."), doc.Hostname, doc.IPManagement.Length > 0 ? t.T(" with IP ") + doc.IPManagement : t.T(" without IP (enter it for SSH)")));
            }
        }
        if (row is null)
        {
            return;
        }
        doc?.CopyTo(row);
        if (state.Length > 0)
        {
            row["Status"] = state;
            row["Date"] = Now("yyyy-MM-dd HH:mm");
        }
    }

    private static void SetRowStatus(InventoryRow? row, string text)
    {
        if (row is not null)
        {
            row["Status"] = text;
        }
    }

    private void RegisterCredentials(Credentials cred) => masker.Register(cred.Password, cred.Enable);

    /// <summary>Documents the selected switches (or the console one): changes nothing on them. Returns (ok, failed).</summary>
    public (int Ok, int Failed) Query(ConfigureOptions opt, Credentials cred)
    {
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(cred);
        RegisterCredentials(cred);
        var targets = Inventory.Targets(opt.OverSsh, opt.PortCom, t);
        var folder = Path.Combine(paths.InventoryFolder, "Queries", "QUERY_" + Now("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(folder);
        Log.Paths.Clear();
        Log.Paths.Add(Path.Combine(folder, "log.txt"));
        Log.Info(Ci.F(t.T("===== Query of {0} switch(es) via {1} ====="), targets.Count, opt.ConnectionText(t)));
        int ok = 0, bad = 0, n = 0;
        foreach (var d in targets)
        {
            n++;
            if (Cancellation.IsCancellationRequested)
            {
                SetRowStatus(d.Row, t.T("NOT QUERIED (stopped)"));
                continue;
            }
            ui.Status(Ci.F(t.T("Querying {0} ({1} of {2})..."), d.Name, n, targets.Count), 100 * (n - 1) / targets.Count);
            SetRowStatus(d.Row, t.T("Querying..."));
            try
            {
                using var c = Connect(opt, cred, d, Path.Combine(folder, Inventory.SafeName(Ci.F("{0}_{1}", d.Name, d.IP))));
                var doc = Document(c);
                UpdateRow(d.Row, doc, t.T("Queried"));
                Log.Ok(Ci.F(t.T("{0}: {1} {2} {3}, LLDP {4}, CDP {5}, VTP {6}, STP {7}"), d.Name, doc.Model, doc.Platform, doc.Version, doc.LLDP, doc.CDP, doc.VTP, doc.STP));
                ok++;
            }
            catch (Exception ex) when (IsOperational(ex))
            {
                var pref = ex is FingerprintRejectedException ? "SKIPPED" : "FAILED";
                SetRowStatus(d.Row, Ci.F("{0}: {1}", t.T(pref), masker.Protect(ex.Message)));
                if (d.Row is not null)
                {
                    d.Row["Date"] = Now("yyyy-MM-dd HH:mm");
                }
                Log.Error(Ci.F("{0}: {1}", d.Name, ex.Message));
                bad++;
            }
        }
        Inventory.Save(paths.InventoryCsv);
        ui.Status(Ci.F(t.T("Query finished: {0} successful, {1} failed."), ok, bad), 100);
        Log.Info(Ci.F(t.T("Query finished: {0} successful, {1} failed. List saved to {2}"), ok, bad, paths.InventoryCsv));
        return (ok, bad);
    }

    /// <summary>Errors that end one switch (and not the whole operation).</summary>
    private static bool IsOperational(Exception ex) =>
        ex is SwitchException or OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException
            or InvalidOperationException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or FormatException;

    /// <summary>Validates and builds what the operator must confirm before the push.</summary>
    public PreparedPush PreparePush(PushParameters p, ConfigureOptions opt, IEnumerable<string>? extraAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(opt);
        masker.Register([.. p.Secrets]);
        var errors = PushPlanner.Validate(p, t);
        if (errors.Count > 0)
        {
            throw new FormatException(PushPlanner.ValidationMessage(errors, t));
        }
        if (string.IsNullOrWhiteSpace(opt.FolderRecord))
        {
            throw new FormatException(t.T("Enter the evidence folder (tab 4)."));
        }
        var targets = Inventory.Targets(opt.OverSsh, opt.PortCom, t);
        var view = PushPlanner.Preview(p, opt.PreviewOptions(t, targets), t, masker, extraAllowed);

        var groups = string.Join(", ", PushPlanner.Groups(p, t));
        string who;
        if (opt.OverSsh)
        {
            var names = string.Join(", ", targets.Take(12).Select(x => x.Name));
            if (targets.Count > 12)
            {
                names += Ci.F(t.T(" and {0} more"), targets.Count - 12);
            }
            who = Ci.F(t.T("{0} switch(es) over SSH: {1}"), targets.Count, names);
        }
        else
        {
            who = Ci.F(t.T("the switch connected to console {0}"), opt.PortCom);
        }
        var txt = Ci.F(t.T("These groups will be pushed:{0}  {1}{0}{0}to {2}.{0}{0}Save: {3}.{0}Each switch's running-config is backed up first in its evidence folder."),
            "\r\n", groups, who, opt.Save ? t.T("yes, with write memory if there were no failures") : "no");
        var risks = PushPlanner.Risks(p, t);
        if (risks.Count > 0)
        {
            txt += Ci.F(t.T("{0}{0}WARNING:{0}- {1}{0}{0}Continue?"), "\r\n", string.Join("\r\n- ", risks));
            return new PreparedPush(p, opt, targets, view, txt, true);
        }
        return new PreparedPush(p, opt, targets, view, txt + t.T("\r\n\r\nContinue?"), false);
    }

    private static void WriteCrlf(string path, string text) =>
        File.WriteAllText(path, Ci.Replace(text, "\\r?\\n", "\r\n"), new UTF8Encoding(false));

    /// <summary>Pushes to one switch: backup, apply, verify, save only if allowed, document.</summary>
    public SwitchResult PushOne(PushParameters p, ConfigureOptions opt, Credentials cred, ConfTarget target, string folder, IEnumerable<string>? extraAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(target);
        var r = new SwitchResult(target) { Folder = folder };
        var applying = false;
        SwitchConnection? c = null;
        try
        {
            c = Connect(opt, cred, target, folder);
            if (c.Trust is { } tr)
            {
                r.Fingerprint = Ci.F("{0} {1} ({2})", tr.Algorithm, tr.Normal, tr.How);
            }
            r.Hostname = c.Hostname;
            if (target.Row is null)
            {
                r.Name = r.Hostname;
            }
            var ver = c.Version();
            r.Model = ver.Model;
            r.Platform = ver.OS;
            r.Version = ver.Version;
            r.Serial = ver.Serial;
            Log.Info(Ci.F(t.T("{0}: connected to {1}# ({2} {3} {4})"), target.Name, r.Hostname, ver.Model, ver.OS, ver.Version));

            var runBefore = c.FullRunning();
            // Backup to roll back (not masked: it is the real configuration; protect the folder)
            WriteCrlf(Path.Combine(folder, "running_before.txt"), runBefore);

            var items = PushPlanner.Lines(p, ver.OS, runBefore, t, masker, extraAllowed);
            var blocks = PushPlanner.Blocks(items);
            WriteCrlf(Path.Combine(folder, "sent_lines.txt"), PushPlanner.PlatformText(items, false, t, masker));
            var verif = new List<PushCheck>();
            if (target.Row is not null && r.Hostname.Length > 0 && !Ci.Eq(r.Hostname, target.Name))
            {
                verif.Add(new PushCheck(t.T("Hostname"), "NOTE", Ci.F(t.T("The list says \"{0}\" and the switch answers as \"{1}\""), target.Name, r.Hostname)));
            }
            IReadOnlyList<ApplyIssue> errors = [];
            if (blocks.Count > 0)
            {
                ui.Status(Ci.F(t.T("{0}: applying..."), target.Name));
                applying = true;
                var ap = c.Session.ApplyPlan(blocks, PushVerifier.LineCount(blocks));
                applying = false;
                errors = ap.Errors;
                r.Warnings = ap.Warnings.Select(w => (masker.Protect(w.Line), masker.Protect(w.Answer))).ToList();
            }
            ui.Status(Ci.F(t.T("{0}: verifying..."), target.Name));
            var runAfter = c.FullRunning();
            var shows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cmd in items.Where(i => i.Verif == "show").Select(i => i.Command).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                shows[cmd] = c.Session.RunCommand(cmd, 60);
            }
            verif.AddRange(PushVerifier.Verify(items, errors.Select(e => (e.Line, e.Answer)), runAfter, shows, t, masker));
            var failures = verif.Count(v => v.Result == "FAIL");
            var notes = verif.Count(v => v.Result == "NOTE");

            if (blocks.Count == 0)
            {
                r.Saved = t.T("Not needed (nothing was sent)");
            }
            else if (!opt.Save)
            {
                r.Saved = t.T("No (option unchecked): the configuration is lost if the switch reloads");
            }
            else if (failures > 0 && !opt.SaveWithErrors)
            {
                r.Saved = t.T("NO: there were failures. What was applied stays only in the running-config (a reload reverts it)");
            }
            else
            {
                ui.Status(Ci.F(t.T("{0}: saving (write memory)..."), target.Name));
                var rw = c.Session.SendConfigLine("write memory", 180);
                if (Ci.IsMatch(rw.Output, "\\[OK\\]"))
                {
                    r.Saved = t.T("Yes (write memory [OK])");
                }
                else
                {
                    r.Saved = t.T("NO: write memory did not confirm [OK]");
                    verif.Add(new PushCheck(t.T("write memory"), "FAIL", t.T("Did not confirm [OK]")));
                    failures++;
                }
            }
            r.Verification = verif;
            try
            {
                r.Doc = Document(c, runAfter, ver);
            }
            catch (Exception ex) when (IsOperational(ex) && ex is not OperationCanceledException)
            {
                Log.Warn(Ci.F(t.T("{0}: could not document after the change: {1}"), target.Name, ex.Message));
            }
            if (failures > 0)
            {
                r.Result = PushOutcome.WithFailures;
                r.Detail = Ci.F(t.T("{0} failure(s), {1} note(s)"), failures, notes);
            }
            else if (notes > 0)
            {
                r.Result = PushOutcome.SuccessfulWithNotes;
                r.Detail = Ci.F(t.T("{0} note(s)"), notes);
            }
            else
            {
                r.Result = PushOutcome.Successful;
                r.Detail = Ci.F(t.T("{0} check(s) verified"), verif.Count);
            }
        }
        catch (Exception ex) when (IsOperational(ex))
        {
            r.Result = ex is FingerprintRejectedException ? PushOutcome.Skipped : PushOutcome.Failed;
            r.Detail = masker.Protect(ex.Message);
            if (applying)
            {
                r.Detail += t.T(" (stopped in the middle of the apply: part stayed in the running-config and was NOT saved; check it or reload without saving to revert)");
            }
            if (c?.Trust is { } tr && r.Fingerprint.Length == 0)
            {
                r.Fingerprint = Ci.F("{0} {1} ({2})", tr.Algorithm, tr.Normal, tr.How);
            }
        }
        finally
        {
            c?.Dispose();
            r.End = DateTime.Now;
        }
        return r;
    }

    /// <summary>
    /// The sweep, once the operator confirmed and gave the credentials. A switch whose
    /// fingerprint is not accepted is SKIPPED; after a FAILED one it stops unless "continue" is checked.
    /// </summary>
    public PushRun Push(PreparedPush prepared, Credentials cred, IEnumerable<string>? extraAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(cred);
        RegisterCredentials(cred);
        var (p, opt, targets) = (prepared.Parameters, prepared.Options, prepared.Targets);
        var groups = string.Join(", ", PushPlanner.Groups(p, t));
        var suffix = opt.Ticket.Length > 0 ? "_" + Inventory.SafeName(opt.Ticket) : string.Empty;
        var start = DateTime.Now;
        var folder = Path.Combine(opt.FolderRecord, "PUSH_" + start.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + suffix);
        Directory.CreateDirectory(folder);
        Log.Paths.Clear();
        Log.Paths.Add(Path.Combine(folder, "log.txt"));
        WriteCrlf(Path.Combine(folder, "parameters.txt"), prepared.Preview);
        Log.Info(Ci.F(t.T("===== Push: {0} to {1} target(s) via {2} ====="), groups, targets.Count, opt.ConnectionText(t)));
        Log.Info(Ci.F(t.T("Switch user: {0}. Evidence: {1}"), cred.User, folder));

        var results = new List<SwitchResult>();
        var stopped = false;
        var n = 0;
        foreach (var d in targets)
        {
            n++;
            if (Cancellation.IsCancellationRequested || stopped)
            {
                var rr = new SwitchResult(d)
                {
                    Result = PushOutcome.NotRun,
                    Detail = Cancellation.IsCancellationRequested ? t.T("Stopped by the operator") : t.T("The sweep stopped after a failure"),
                    End = DateTime.Now,
                };
                results.Add(rr);
                SetRowStatus(d.Row, Ci.F(t.T("NOT RUN: {0}"), rr.Detail));
                continue;
            }
            ui.Status(Ci.F("[{0}/{1}] {2}...", n, targets.Count, d.Name), 100 * (n - 1) / targets.Count);
            SetRowStatus(d.Row, t.T("Pushing..."));
            var sub = Path.Combine(folder, Inventory.SafeName(d.IP.Length > 0 ? Ci.F("{0}_{1}", d.Name, d.IP) : "console"));
            var r = PushOne(p, opt, cred, d, sub, extraAllowed);
            results.Add(r);
            var stateTxt = r.Result is PushOutcome.Successful or PushOutcome.SuccessfulWithNotes or PushOutcome.WithFailures
                ? Ci.F("{0} {1}: {2}", t.T(r.Result.Code()), Now("HH:mm"), r.Detail)
                : Ci.F("{0}: {1}", t.T(r.Result.Code()), r.Detail);
            UpdateRow(d.Row, r.Doc, Ci.F(t.T("Push {0}"), stateTxt));
            var level = r.Result switch
            {
                PushOutcome.Successful => "OK",
                PushOutcome.SuccessfulWithNotes or PushOutcome.WithFailures or PushOutcome.Skipped => "WARN",
                _ => "ERROR",
            };
            Log.Write(level, Ci.F(t.T("{0}: {1} - {2}. Saved: {3}"), d.Name, t.T(r.Result.Code()), r.Detail.TrimEnd('.'), r.Saved));
            foreach (var v in r.Verification.Where(v => v.Result != "OK"))
            {
                Log.Warn(Ci.F("   {0}: {1} - {2}", v.Check, t.T(v.Result), v.Detail));
            }
            if (r.Result == PushOutcome.Failed && !opt.Continue)
            {
                stopped = true;
            }
        }
        var end = DateTime.Now;
        try
        {
            Inventory.Save(paths.InventoryCsv);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(Ci.F(t.T("Could not save the list: {0}"), ex.Message));
        }
        var summary = string.Join(", ", results.GroupBy(x => x.Result.Code()).OrderBy(g => g.Key, Ci.SortComparer).Select(g => Ci.F("{0}: {1}", t.T(g.Key), g.Count())));
        var save = opt.Save ? (opt.SaveWithErrors ? t.T("Yes, even with failures") : t.T("Yes, only if there were no failures")) : t.T("No");
        var run = new PushRun(opt.Ticket, opt.Responsible, Environment.UserName, Environment.MachineName, start, end, opt.ConnectionText(t), cred.User,
            groups, save, prepared.Preview, results, summary, folder);
        run.RecordPath = PushRecord(run);
        ui.Status(Ci.F(t.T("Push finished. {0}"), summary), 100);
        Log.Info(Ci.F(t.T("===== Push finished: {0}. Record: {1}"), summary, run.RecordPath));
        return run;
    }

    private string PushRecord(PushRun run)
    {
        var path = Path.Combine(run.Folder, "push_report.html");
        var records = new HtmlRecords(t);
        try
        {
            HtmlRecords.Write(path, records.Push(new PushRecordData
            {
                Ticket = run.Ticket, Responsible = run.Responsible, UserWindows = run.UserWindows, DeviceWork = run.Computer,
                StartText = run.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), EndText = run.End.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Duration = run.Duration, Connection = run.Connection, UserSwitch = run.SwitchUser, Groups = run.Groups, Save = run.Save, View = run.Preview,
                Results = run.Results, Summary = run.Summary, Folder = run.Folder,
            }, masker));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException)
        {
            Log.Error(Ci.F(t.T("Could not generate the full record: {0}"), ex.Message));
            var min = new List<(string, string)>
            {
                ("Ticket", run.Ticket), ("Responsible", run.Responsible), ("Start", run.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                ("End", run.End.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), ("Groups", run.Groups), ("Result", run.Summary), ("Folder", run.Folder),
            };
            min.AddRange(run.Results.Select(r => (Ci.F("{0} ({1})", r.Name, r.IP), Ci.F("{0} - {1}", t.T(r.Result.Code()), r.Detail))));
            try
            {
                HtmlRecords.Write(path, records.Minimal(t.T("Configuration push record"), min, ex.Message));
                return path;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }
}
