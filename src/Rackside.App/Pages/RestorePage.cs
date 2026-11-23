using Rackside.App.Ui;
using Rackside.Core.Restore;
using Rackside.Core.Security;
using Rackside.Operations.Restore;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Pages;

/// <summary>Restore from backup (DRP): load the backup, review against the target, apply, record.</summary>
internal sealed class RestorePage : Page
{
    private readonly RestoreOperation op;
    private readonly ConnectionCard conn;
    private readonly PathField file;
    private readonly Label sourceInfo = Make.Note(S("No backup loaded."));
    private readonly TextBox model = Make.Input();
    private readonly TextBox hostname = Make.Input();
    private readonly TextBox domain = Make.Input();
    private readonly ComboBox mgmt = Make.Combo(["Vlan1"], 0, 200, editable: true);
    private readonly TextBox ip = Make.Input();
    private readonly TextBox mask = Make.Input();
    private readonly TextBox gateway = Make.Input();
    private readonly TextBox admin = Make.Input();
    private readonly TextBox adminPass = Make.Input(password: true);
    private readonly TextBox adminPass2 = Make.Input(password: true);
    private readonly TextBox enable = Make.Input(password: true);
    private readonly TextBox enable2 = Make.Input(password: true);
    private readonly CheckBox rsa = Make.Check(S("Generate 2048-bit RSA keys (SSH)"), true);
    private readonly CheckBox vlans = Make.Check(S("Create the VLANs the backup uses"), true);
    private readonly CheckBox cert = Make.Check(S("Remove the self-signed certificate of the backup"), true);
    private readonly CheckBox users = Make.Check(S("Also restore the other users"));
    private readonly CheckBox save = Make.Check(S("Save to startup-config (write memory)"), true);
    private readonly PathField folder;
    private readonly TextBox ticket = Make.Input();
    private readonly TextBox responsible = Make.Input();
    private readonly TextBox failedHost = Make.Input();
    private readonly TextBox failedSerial = Make.Input();
    private readonly TextBox notes = Make.Area(height: 70);
    private readonly Label summary = Make.Note(S("Click \"Connect and review\" to read the target switch and generate the configuration."), Theme.TextColor);
    private readonly TextBox txtConfig = Make.Area(readOnly: true, mono: true);
    private readonly TextBox txtChanges = Make.Area(readOnly: true, mono: true);
    private readonly TextBox txtMapping = Make.Area(readOnly: true, mono: true);
    private readonly LogView log = new();
    private readonly Tabs tabs = new();
    private readonly Button reviewButton;
    private readonly Button applyButton;
    private readonly Button recordButton;
    private SourceConfig? source;
    private RestoreReview? review;
    private string recordPath = string.Empty;

    public RestorePage(MainForm shell) : base(shell, S("Restore from backup"), S("Applies the backup of a failed switch to its replacement, converting between models if needed."))
    {
        op = new RestoreOperation(App.T, new SecretMasker(), shell.Operator());
        op.Log.Line += log.Append;
        conn = new ConnectionCard(App, allowSsh: false);
        file = new PathField(string.Empty, folder: false, S("Backups (*.txt;*.cfg;*.conf)|*.txt;*.cfg;*.conf|All files (*.*)|*.*"), () => App.Paths.Backups);
        file.Box.TextChanged += (_, _) => LoadBackup();
        folder = new PathField(App.Paths.Restores, folder: true);

        var step1 = new CardList();
        var backup = step1.Add(new Card(S("Backup"), S("The running-config of the failed switch. Only allowed global commands pass; the dangerous ones are always blocked.")));
        backup.Add(file);
        backup.Add(sourceInfo);
        step1.Add(conn);

        var step2 = new CardList();
        var ident = step2.Add(new Card(S("Identity and management"), S("Proposed from the backup. Change them if the replacement must be different.")));
        var g = new Fields();
        g.Add(S("Source model"), model);
        g.Add(S("Hostname"), hostname);
        g.Add(S("Domain"), domain);
        g.Add(S("Management interface"), mgmt);
        g.Add(S("IP address"), ip);
        g.Add(S("Mask"), mask);
        g.Add(S("Gateway"), gateway);
        ident.Add(g);
        var access = step2.Add(new Card(S("Access"), S("Administrator user and enable for the replacement. Empty = keep the ones of the backup (hashes are not portable between models).")));
        var a = new Fields();
        a.Add(S("Administrator user"), admin);
        a.Add(S("Password"), adminPass);
        a.Add(S("Repeat the password"), adminPass2);
        a.Add(S("Enable secret"), enable);
        a.Add(S("Repeat the enable secret"), enable2);
        access.Add(a);
        var opts = step2.Add(new Card(S("Options")));
        foreach (var c in new[] { rsa, vlans, cert, users, save })
        {
            opts.Add(c);
        }

        var step3 = new CardList();
        var rec = step3.Add(new Card(S("DRP record"), S("Data of the event for the printable record.")));
        var r = new Fields();
        r.Add(S("Evidence folder"), folder);
        r.Add(S("Ticket / incident"), ticket);
        r.Add(S("Responsible"), responsible);
        r.Add(S("Failed switch hostname"), failedHost);
        r.Add(S("Failed switch serial"), failedSerial);
        r.Add(S("Notes"), notes);
        rec.Add(r);

        var step4 = new Panel { BackColor = Theme.Background };
        var reviewTabs = new Tabs();
        reviewTabs.Add(S("Configuration to send"), txtConfig);
        reviewTabs.Add(S("Changes"), txtChanges);
        reviewTabs.Add(S("Interface mapping"), txtMapping);
        var sumPanel = new Panel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(24, 12, 24, 0), BackColor = Theme.Background };
        summary.MaximumSize = new Size(1100, 0);
        sumPanel.Controls.Add(summary);
        step4.Controls.Add(reviewTabs);
        step4.Controls.Add(sumPanel);

        tabs.Add(S("1. Backup and connection"), step1);
        tabs.Add(S("2. Target data"), step2);
        tabs.Add(S("3. Record"), step3);
        tabs.Add(S("4. Review"), step4);
        tabs.Add(S("Log"), log);
        SetBody(tabs);

        reviewButton = Action(Make.Secondary(S("Connect and review"), async (_, _) => await Review()));
        applyButton = Action(Make.Primary(S("Apply to switch"), async (_, _) => await Apply()));
        recordButton = Action(Make.Secondary(S("Open record"), (_, _) => OpenPath(recordPath)));
        UpdateButtons();
    }

    private void LoadBackup()
    {
        source = null;
        review = null;
        var path = file.Value;
        if (!File.Exists(path))
        {
            sourceInfo.Text = S("No backup loaded.");
            UpdateButtons();
            return;
        }
        try
        {
            source = SourceConfig.ReadFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            sourceInfo.Text = ex.Message;
            UpdateButtons();
            return;
        }
        var o = RestoreOptions.FromSource(source);
        sourceInfo.Text = F("Detected source: {0} {1}  |  model {2}  |  {3} physical interfaces  |  hostname {4}", source.OS, source.Version,
            source.Model.Length > 0 ? source.Model : S("(not in the file: type it if you know it)"), source.Physical.Count, source.Hostname);
        model.Text = o.ModelSource;
        hostname.Text = o.Hostname;
        domain.Text = o.Domain;
        gateway.Text = o.Gateway;
        admin.Text = o.UserAdmin;
        failedHost.Text = source.Hostname;
        mgmt.Items.Clear();
        mgmt.Items.AddRange([.. source.Svis.Select(s => s.Name).DefaultIfEmpty("Vlan1")]);
        mgmt.Text = o.InterfaceManagement;
        ip.Text = o.IP;
        mask.Text = o.Mask;
        UpdateButtons();
    }

    private RestoreRun Run()
    {
        var o = RestoreOptions.FromSource(source!);
        o.ModelSource = model.Text.Trim();
        o.Hostname = hostname.Text.Trim();
        o.Domain = domain.Text.Trim();
        o.InterfaceManagement = mgmt.Text.Trim();
        o.IP = ip.Text.Trim();
        o.Mask = mask.Text.Trim();
        o.Gateway = gateway.Text.Trim();
        o.UserAdmin = admin.Text.Trim();
        o.PasswordAdmin = adminPass.Text;
        o.EnableSecret = enable.Text;
        o.CreateVlans = vlans.Checked;
        o.RemoveCertificate = cert.Checked;
        o.IncludeUsers = users.Checked;
        o.ExtraAllowedCommands = App.Settings.ExtraAllowedCommands;
        return new RestoreRun
        {
            Options = o, PortCom = conn.PortCom, BaudRate = conn.BaudRate, ConsoleUser = conn.User, ConsolePassword = conn.Password, ConsoleEnable = conn.Enable,
            PasswordAdmin2 = adminPass2.Text, EnableSecret2 = enable2.Text, GenerateRSA = rsa.Checked, Save = save.Checked, FolderRecord = folder.Value,
            Ticket = ticket.Text.Trim(), Responsible = responsible.Text.Trim(), FailedHost = failedHost.Text.Trim(), FailedSerial = failedSerial.Text.Trim(), Remarks = notes.Text.Trim(),
        };
    }

    private bool Valid(RestoreRun run)
    {
        var problems = op.Validate(source, run);
        if (problems.Count == 0)
        {
            return true;
        }
        tabs.Select(problems[0].Tab);
        Dialogs.Error(FindForm(), string.Join("\r\n", problems.Select(p => p.Message)));
        return false;
    }

    private void Show(RestoreReview r)
    {
        summary.Text = r.Summary;
        txtConfig.Text = r.TextConfig.Replace("\n", "\r\n", StringComparison.Ordinal);
        txtChanges.Text = r.TextChanges.Replace("\n", "\r\n", StringComparison.Ordinal);
        txtMapping.Text = r.TextMapping.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private async Task Review()
    {
        if (source is null)
        {
            tabs.Select(0);
            Dialogs.Error(FindForm(), S("Select the backup file."));
            return;
        }
        var run = Run();
        if (!Valid(run))
        {
            return;
        }
        tabs.Select(4);
        var src = source;
        review = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Review(src, run);
        });
        if (review is not null)
        {
            Show(review);
            tabs.Select(3);
        }
        UpdateButtons();
    }

    private async Task Apply()
    {
        if (review is null)
        {
            return;
        }
        var run = Run();
        if (!Valid(run))
        {
            return;
        }
        tabs.Select(4);
        var rv = review;
        var r = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Apply(rv, run);
        });
        Show(rv);
        if (r is null)
        {
            UpdateButtons();
            return;
        }
        recordPath = r.RecordPath;
        var end = App.T;
        var msg = string.Format(System.Globalization.CultureInfo.CurrentCulture, end.T("Result: {0}{1}{1}Switch errors: {2}{1}Saved to startup-config: {3}{1}{1}The DRP record and evidence are in:{1}{4}"),
            end.T(r.Outcome.Code()), "\r\n", r.Errors.Count, r.Saved ? end.T("yes") : "no", rv.Folder);
        if (recordPath.Length > 0 && Dialogs.Ask(FindForm(), msg + end.T("\r\n\r\nOpen the DRP record in the browser to print it or save it as PDF?"), end.T("Restore finished")))
        {
            OpenPath(recordPath);
        }
        else if (recordPath.Length == 0)
        {
            Dialogs.Error(FindForm(), msg + end.T("\r\n\r\nThe DRP record could NOT be generated: check the log."), end.T("Restore finished"));
        }
        UpdateButtons();
    }

    protected override void UpdateButtons()
    {
        if (applyButton is null)
        {
            return;
        }
        var busy = Shell.Busy;
        reviewButton.Enabled = !busy && source is not null;
        applyButton.Enabled = !busy && review is { Applied: false };
        recordButton.Enabled = !busy && recordPath.Length > 0;
    }
}
