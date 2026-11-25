using System.Globalization;
using Rackside.App.Ui;
using Rackside.Core.Configure;
using Rackside.Core.Push;
using Rackside.Core.Security;
using Rackside.Operations;
using Rackside.Operations.Configure;
using Rackside.Records;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Pages;

/// <summary>Configure switches: inventory, query (documents, changes nothing) and push of common parameters.</summary>
internal sealed class ConfigurePage : Page
{
    private static readonly string[] Columns = ["Name", "IP", "Port", "Hostname", "Model", "Platform", "Version", "Serial", "LLDP", "CDP", "VTP", "STP", "SNMP", "NTP", "Syslog", "SSH", "Status", "Date"];

    private readonly ConfigureOperation op;
    private readonly DataGridView grid = new();
    private readonly Tabs tabs = new();
    private readonly LogView log = new();
    private readonly TextBox preview = Make.Area(readOnly: true, mono: true);
    private readonly CheckBox trustNew = Make.Check(S("Accept new SSH fingerprints without asking (only switches with none saved)"));
    private readonly CheckBox save = Make.Check(S("Save with write memory if there were no failures"), true);
    private readonly CheckBox saveWithErrors = Make.Check(S("Save even with failures"));
    private readonly CheckBox keepGoing = Make.Check(S("Continue the sweep after a failed switch"), true);
    private readonly PathField folder;
    private readonly TextBox ticket = Make.Input();
    private readonly TextBox responsible = Make.Input();
    private readonly Dictionary<string, Control> p = new(StringComparer.Ordinal);
    private readonly Button queryButton;
    private readonly Button previewButton;
    private readonly Button pushButton;
    private readonly Button recordButton;
    private string recordPath = string.Empty;
    private string lastUser = string.Empty;

    public ConfigurePage(MainForm shell) : base(shell, S("Configure switches"), S("Documents your switches and pushes common parameters over SSH to the checked switches."))
    {
        op = new ConfigureOperation(new ConfigurePaths(App.Paths.Inventory, App.Paths.InventoryCsv, App.Paths.Fingerprints), App.T, new SecretMasker(), shell.Operator());
        op.Log.Line += log.Append;
        folder = new PathField(App.Paths.Pushes, folder: true);
        if (File.Exists(App.Paths.InventoryCsv))
        {
            try
            {
                op.Inventory.Import(App.Paths.InventoryCsv, App.T);
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                log.Append(ex.Message);
            }
        }

        tabs.Add(S("1. Switches"), BuildInventory());
        tabs.Add(S("2. Parameters"), BuildParameters());
        tabs.Add(S("3. Sweep and save"), BuildOptions());
        tabs.Add(S("4. Preview"), preview);
        tabs.Add(S("Log"), log);
        tabs.Changed += (_, _) =>
        {
            if (tabs.Selected == 3)
            {
                Preview();
            }
        };
        SetBody(tabs);
        queryButton = Action(Make.Secondary(S("Query"), async (_, _) => await Query()));
        previewButton = Action(Make.Secondary(S("Preview"), (_, _) => { Preview(); tabs.Select(3); }));
        pushButton = Action(Make.Primary(S("Push"), async (_, _) => await Push()));
        recordButton = Action(Make.Secondary(S("Open record"), (_, _) => OpenPath(recordPath)));
        FillGrid();
        UpdateButtons();
    }

    // ---------------------------------------------------------------- inventory
    private Control BuildInventory()
    {
        var panel = new Panel { Padding = new Padding(24, 12, 24, 16), BackColor = Theme.Background };
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Theme.Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Background;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.DefaultCellStyle.SelectionBackColor = Theme.AccentSoft;
        grid.DefaultCellStyle.SelectionForeColor = Theme.TextColor;
        grid.GridColor = Theme.Border;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Sel", HeaderText = S("Sel"), Width = 44 });
        foreach (var c in Columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = c, HeaderText = S(c), ReadOnly = c is not ("Name" or "IP" or "Port") });
        }
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Rows[e.RowIndex].Tag is not InventoryRow row)
            {
                return;
            }
            var col = grid.Columns[e.ColumnIndex].Name;
            var v = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            if (col == "Sel")
            {
                row.Selected = v is true;
            }
            else
            {
                row[col] = Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        };
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        var bar = Make.Row(
            Make.Secondary(S("Import CSV..."), (_, _) => ImportCsv()),
            Make.Secondary(S("Add"), (_, _) => { var r = new InventoryRow(); r["Name"] = S("new"); op.Inventory.Rows.Add(r); FillGrid(); }),
            Make.Secondary(S("Remove selected"), (_, _) => Remove()),
            Make.Secondary(S("Check all"), (_, _) => SelectAll(true)),
            Make.Secondary(S("Uncheck all"), (_, _) => SelectAll(false)),
            Make.Secondary(S("Save list"), (_, _) => SaveList()),
            Make.Secondary(S("Printable inventory"), (_, _) => InventoryDocument()));
        bar.Dock = DockStyle.Top;
        bar.Padding = new Padding(0, 0, 0, 10);
        var note = Make.Note(S("Check (Sel) the switches to query or push: they are reached over SSH with the IP and SSH port of each row."));
        note.Dock = DockStyle.Bottom;
        panel.Controls.Add(grid);
        panel.Controls.Add(bar);
        panel.Controls.Add(note);
        return panel;
    }

    private void FillGrid()
    {
        grid.Rows.Clear();
        foreach (var r in op.Inventory.Rows)
        {
            var i = grid.Rows.Add([r.Selected, .. Columns.Select(c => (object)r[c])]);
            grid.Rows[i].Tag = r;
        }
    }

    private void SelectAll(bool value)
    {
        op.Inventory.Rows.ForEach(r => r.Selected = value);
        FillGrid();
    }

    private void Remove()
    {
        var rows = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<InventoryRow>().ToList();
        if (rows.Count == 0 || !Dialogs.Ask(FindForm(), F("Remove {0} switch(es) from the list?", rows.Count)))
        {
            return;
        }
        rows.ForEach(r => op.Inventory.Rows.Remove(r));
        FillGrid();
    }

    private void ImportCsv()
    {
        using var d = new OpenFileDialog { Filter = S("CSV (*.csv)|*.csv|All files (*.*)|*.*") };
        if (d.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var r = op.Inventory.Import(d.FileName, App.T);
            FillGrid();
            Dialogs.Info(FindForm(), F("Imported: {0} added, {1} updated, {2} without IP skipped.", r.Added, r.Updated, r.Skipped));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(FindForm(), ex.Message);
        }
    }

    private void SaveList()
    {
        grid.EndEdit();
        try
        {
            op.Inventory.Save(App.Paths.InventoryCsv);
            Shell.SetStatus(F("List saved to {0}", App.Paths.InventoryCsv), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(FindForm(), ex.Message);
        }
    }

    private void InventoryDocument()
    {
        var path = Path.Combine(App.Paths.Inventory, "inventory.html");
        Directory.CreateDirectory(App.Paths.Inventory);
        HtmlRecords.Write(path, new HtmlRecords(App.T).InventoryDocument(op.Inventory.Rows));
        OpenPath(path);
    }

    // ---------------------------------------------------------------- parameters
    private T P<T>(string key, T control) where T : Control
    {
        p[key] = control;
        return control;
    }

    private bool On(string key) => p[key] is CheckBox { Checked: true };

    private string V(string key) => p[key] switch
    {
        ComboBox c => c.Text.Trim(),
        TextBox t => t.Multiline ? t.Text : t.Text.Trim(),
        _ => string.Empty,
    };

    private int I(string key) => p[key] is ComboBox c ? Math.Max(c.SelectedIndex, 0) : 0;

    private Card Group(CardList list, string key, string title, string? note = null)
    {
        var card = list.Add(new Card(title, note));
        card.Add(P(key, Make.Check(S("Apply this group"))));
        return card;
    }

    private Control BuildParameters()
    {
        var d = App.Settings.Push;
        var list = new CardList();
        string[] noYes = [S("Do not change"), S("Enable"), S("Disable")];

        var g = Group(list, "ApLldp", App.T.T("LLDP and CDP"));
        var f = new Fields();
        f.Add("LLDP", P("Lldp", Make.Combo(noYes, 1, 200)));
        f.Add("CDP", P("Cdp", Make.Combo(noYes, 0, 200)));
        g.Add(f);

        g = Group(list, "ApBannerLogin", App.T.T("Login banner"), S("Empty text = remove the banner."));
        g.Add(P("BannerLogin", Make.Area(d.LoginBanner, 90, mono: true)));
        g = Group(list, "ApBannerMotd", App.T.T("MOTD banner"), S("Empty text = remove the banner."));
        g.Add(P("BannerMotd", Make.Area(d.MotdBanner, 90, mono: true)));

        g = Group(list, "ApVtp", "VTP", S("Transparent is recommended: server/client can overwrite the VLANs of the whole domain."));
        f = new Fields();
        f.Add(S("Mode"), P("VtpMode", Make.Combo(["transparent", "server", "client", "off"], 0, 200)));
        f.Add(S("Domain"), P("VtpDomain", Make.Input()));
        f.Add(S("Version"), P("VtpVersion", Make.Combo([S("Do not change"), "1", "2", "3"], 0, 200)));
        f.Add(S("Password"), P("VtpPassword", Make.Input(password: true)));
        g.Add(f);

        g = Group(list, "ApSnmp", "SNMP");
        f = new Fields();
        f.Add(S("Location"), P("SnmpLocation", Make.Input(width: 420)));
        f.Add(S("Contact"), P("SnmpContact", Make.Input(width: 420)));
        f.Add(S("RO community"), P("SnmpCommunity", Make.Input(password: true)));
        f.Add(S("Community ACL"), P("SnmpAcl", Make.Input()));
        f.Add(S("Trap destinations"), P("SnmpTraps", Make.Input(width: 420)));
        f.Full(P("SnmpEnableTraps", Make.Check(S("snmp-server enable traps"))));
        f.Full(P("SnmpRemoveOthers", Make.Check(S("Remove the other communities and trap destinations"))));
        f.Add(S("SNMPv3 user"), P("SnmpV3User", Make.Input()));
        f.Add(S("SNMPv3 group"), P("SnmpV3Group", Make.Input()));
        f.Add(S("Authentication key (SHA)"), P("SnmpV3Auth", Make.Input(password: true)));
        f.Add(S("Encryption key (AES 128)"), P("SnmpV3Priv", Make.Input(password: true)));
        g.Add(f);

        g = Group(list, "ApNtp", App.T.T("NTP and time"));
        f = new Fields();
        f.Add(S("NTP servers"), P("NtpServers", Make.Input(width: 420)));
        f.Add(S("Source interface"), P("NtpSource", Make.Input()));
        f.Full(P("NtpRemoveOthers", Make.Check(S("Remove the other NTP servers"))));
        f.Add(S("Time zone"), P("Zone", Make.Input(d.TimeZone, width: 120)));
        f.Add(S("Offset from UTC"), P("Offset", Make.Input(d.UtcOffset, width: 120)));
        f.Add(S("Summer time"), P("Summer", Make.Input(d.SummerTime, width: 120)));
        g.Add(f);

        g = Group(list, "ApSyslog", "Syslog");
        f = new Fields();
        f.Add(S("Servers"), P("LogServers", Make.Input(width: 420)));
        f.Add(S("Trap level"), P("LogLevel", Make.Combo([S("Do not change"), "informational", "notifications", "warnings", "errors", "debugging"], 0, 200)));
        f.Add(S("Source interface"), P("LogSource", Make.Input()));
        f.Add(S("Buffer (bytes)"), P("LogBuffer", Make.Input(width: 120)));
        f.Full(P("LogRemoveOthers", Make.Check(S("Remove the other syslog servers"))));
        g.Add(f);

        g = Group(list, "ApDns", "DNS");
        f = new Fields();
        f.Add(S("Domain"), P("DnsDomain", Make.Input()));
        f.Add(S("DNS servers"), P("DnsServers", Make.Input(width: 420)));
        f.Full(P("DnsNoLookup", Make.Check(S("no ip domain lookup"), true)));
        g.Add(f);

        g = Group(list, "ApSsh", App.T.T("SSH and lines"));
        f = new Fields();
        f.Full(P("SshV2", Make.Check(S("ip ssh version 2"), true)));
        f.Add(S("SSH time-out (s)"), P("SshTimeout", Make.Input("60", width: 120)));
        f.Add(S("SSH retries"), P("SshRetries", Make.Input("3", width: 120)));
        f.Add(S("VTY idle timeout (min)"), P("VtyTimeout", Make.Input(d.VtyIdleTimeout, width: 120)));
        f.Full(P("VtyOnlySsh", Make.Check(S("VTY: SSH only (no telnet)"))));
        f.Add(S("VTY ACL"), P("VtyAcl", Make.Input()));
        f.Add(S("Console idle timeout (min)"), P("ConTimeout", Make.Input(width: 120)));
        g.Add(f);

        g = Group(list, "ApServices", App.T.T("Services"));
        foreach (var (k, text) in new[] { ("SvcEncrypt", "service password-encryption"), ("SvcTimestamps", "service timestamps (msec, local time)"), ("SvcNoHttp", "no ip http server / secure-server"), ("SvcNoPad", "no service pad"), ("SvcKeepalives", "service tcp-keepalives-in / out"), ("SvcNoSourceRoute", "no ip source-route"), ("SvcLoginLog", "login on-failure / on-success log") })
        {
            g.Add(P(k, Make.Check(text, true)));
        }

        g = Group(list, "ApStp", "Spanning-tree", S("Changing the mode restarts STP on every VLAN: do it in a maintenance window."));
        f = new Fields();
        f.Add(S("Mode"), P("StpMode", Make.Combo(["rapid-pvst", "pvst"], 0, 200)));
        f.Full(P("StpLoopguard", Make.Check(S("spanning-tree loopguard default"))));
        g.Add(f);

        g = Group(list, "ApUser", App.T.T("Local user / enable"));
        f = new Fields();
        f.Add(S("User (privilege 15)"), P("UsrName", Make.Input()));
        f.Add(S("Password"), P("UsrPassword", Make.Input(password: true)));
        f.Add(S("Repeat the password"), P("UsrPassword2", Make.Input(password: true)));
        f.Add(S("Enable secret"), P("UsrEnable", Make.Input(password: true)));
        f.Add(S("Repeat the enable secret"), P("UsrEnable2", Make.Input(password: true)));
        g.Add(f);

        g = Group(list, "ApExtra", App.T.T("Additional commands"), S("One per line; indented lines are sublines. Hostname, IP addresses and dangerous commands are never sent."));
        g.Add(P("Extra", Make.Area(height: 120, mono: true)));
        return list;
    }

    private PushParameters Parameters()
    {
        string[] noEnableDisable = ["No", "Enable", "Disable"];
        string[] versions = ["No", "1", "2", "3"];
        string[] levels = ["No", "informational", "notifications", "warnings", "errors", "debugging"];
        return new PushParameters
        {
            Lldp = { Apply = On("ApLldp"), Lldp = noEnableDisable[I("Lldp")], Cdp = noEnableDisable[I("Cdp")] },
            Banner = { ApplyLogin = On("ApBannerLogin"), Login = V("BannerLogin"), ApplyMotd = On("ApBannerMotd"), Motd = V("BannerMotd") },
            Vtp = { Apply = On("ApVtp"), Mode = V("VtpMode"), Domain = V("VtpDomain"), Version = versions[I("VtpVersion")], Password = ((TextBox)p["VtpPassword"]).Text },
            Snmp =
            {
                Apply = On("ApSnmp"), Location = V("SnmpLocation"), Contact = V("SnmpContact"), Community = V("SnmpCommunity"), Acl = V("SnmpAcl"),
                Traps = PushParameters.SplitList(V("SnmpTraps")), EnableTraps = On("SnmpEnableTraps"), RemoveOthers = On("SnmpRemoveOthers"),
                V3User = V("SnmpV3User"), V3Group = V("SnmpV3Group"), V3Auth = ((TextBox)p["SnmpV3Auth"]).Text, V3Priv = ((TextBox)p["SnmpV3Priv"]).Text,
            },
            Ntp = { Apply = On("ApNtp"), Servers = PushParameters.SplitList(V("NtpServers")), Source = V("NtpSource"), RemoveOthers = On("NtpRemoveOthers"), Zone = V("Zone"), Offset = V("Offset"), Summer = V("Summer") },
            Syslog = { Apply = On("ApSyslog"), Servers = PushParameters.SplitList(V("LogServers")), Level = levels[I("LogLevel")], Source = V("LogSource"), Buffer = V("LogBuffer"), RemoveOthers = On("LogRemoveOthers") },
            Dns = { Apply = On("ApDns"), Domain = V("DnsDomain"), Servers = PushParameters.SplitList(V("DnsServers")), NoLookup = On("DnsNoLookup") },
            Ssh = { Apply = On("ApSsh"), V2 = On("SshV2"), Timeout = V("SshTimeout"), Retries = V("SshRetries"), VtyTimeout = V("VtyTimeout"), OnlySsh = On("VtyOnlySsh"), VtyAcl = V("VtyAcl"), WithTimeout = V("ConTimeout") },
            Services =
            {
                Apply = On("ApServices"), Encrypt = On("SvcEncrypt"), Timestamps = On("SvcTimestamps"), NoHttp = On("SvcNoHttp"), NoPad = On("SvcNoPad"),
                Keepalives = On("SvcKeepalives"), NoSourceRoute = On("SvcNoSourceRoute"), LoginLog = On("SvcLoginLog"),
            },
            Stp = { Apply = On("ApStp"), Mode = V("StpMode"), Loopguard = On("StpLoopguard") },
            User = { Apply = On("ApUser"), Name = V("UsrName"), Password = ((TextBox)p["UsrPassword"]).Text, Password2 = ((TextBox)p["UsrPassword2"]).Text, Enable = ((TextBox)p["UsrEnable"]).Text, Enable2 = ((TextBox)p["UsrEnable2"]).Text },
            Extra = { Apply = On("ApExtra"), Text = V("Extra") },
        };
    }

    // ---------------------------------------------------------------- options
    private Control BuildOptions()
    {
        var list = new CardList();
        var o = list.Add(new Card(S("Sweep and save")));
        foreach (var c in new[] { trustNew, save, saveWithErrors, keepGoing })
        {
            o.Add(c);
        }
        o.Add(Make.Note(S("A switch whose SSH fingerprint differs from the saved one is always asked about; if you do not accept it, it is skipped.")));
        var r = list.Add(new Card(S("Record")));
        var f = new Fields();
        f.Add(S("Evidence folder"), folder);
        f.Add(S("Ticket / change"), ticket);
        f.Add(S("Responsible"), responsible);
        r.Add(f);
        return list;
    }

    private ConfigureOptions Options() => new()
    {
        TrustNew = trustNew.Checked,
        Save = save.Checked, SaveWithErrors = saveWithErrors.Checked, Continue = keepGoing.Checked, FolderRecord = folder.Value,
        Ticket = ticket.Text.Trim(), Responsible = responsible.Text.Trim(),
    };

    // ---------------------------------------------------------------- actions
    private void Preview()
    {
        try
        {
            var opt = Options();
            IReadOnlyList<ConfTarget>? targets = null;
            try
            {
                targets = op.Inventory.Targets(App.T);
            }
            catch (FormatException)
            {
            }
            var masker = new SecretMasker();
            var prm = Parameters();
            masker.Register([.. prm.Secrets]);
            preview.Text = PushPlanner.Preview(prm, opt.PreviewOptions(App.T, targets), App.T, masker, App.Settings.ExtraAllowedCommands).Replace("\n", "\r\n", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            preview.Text = ex.Message;
        }
    }

    private Credentials? AskCredentials(string title, string detail, bool required)
    {
        var c = CredentialsDialog.Ask(FindForm()!, title, detail, required, lastUser);
        if (c is not null)
        {
            lastUser = c.User;
        }
        return c;
    }

    private async Task Query()
    {
        grid.EndEdit();
        var opt = Options();
        IReadOnlyList<ConfTarget> targets;
        try
        {
            targets = op.Inventory.Targets(App.T);
        }
        catch (FormatException ex)
        {
            tabs.Select(0);
            Dialogs.Error(FindForm(), ex.Message);
            return;
        }
        var who = string.Format(CultureInfo.CurrentCulture, App.T.T("{0} switch(es) over SSH"), targets.Count);
        var cred = AskCredentials(App.T.T("Credentials to query"), string.Format(CultureInfo.CurrentCulture, App.T.T("{0} will be queried: version, LLDP, CDP, VTP, STP and the running-config (read only)."), who), true);
        if (cred is null)
        {
            return;
        }
        tabs.Select(4);
        var r = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Query(opt, cred);
        });
        FillGrid();
        tabs.Select(0);
        if (r != default)
        {
            Dialogs.Info(FindForm(), string.Format(CultureInfo.CurrentCulture, App.T.T("Query finished: {0} successful, {1} failed.{2}{2}The list was updated and saved to:{2}{3}"), r.Ok, r.Failed, "\r\n", App.Paths.InventoryCsv), S("Configure switches"));
        }
    }

    private async Task Push()
    {
        grid.EndEdit();
        PreparedPush prepared;
        try
        {
            prepared = op.PreparePush(Parameters(), Options(), App.Settings.ExtraAllowedCommands);
        }
        catch (FormatException ex)
        {
            Dialogs.Error(FindForm(), ex.Message);
            return;
        }
        preview.Text = prepared.Preview.Replace("\n", "\r\n", StringComparison.Ordinal);
        tabs.Select(3);
        var title = App.T.T("Confirm push");
        if (!(prepared.ConfirmationIsWarning ? Dialogs.Warn(FindForm(), prepared.Confirmation, title) : Dialogs.Ask(FindForm(), prepared.Confirmation, title)))
        {
            Shell.SetStatus(App.T.T("Push cancelled."), null);
            return;
        }
        var who = string.Format(CultureInfo.CurrentCulture, App.T.T("the {0} checked switch(es)"), prepared.Targets.Count);
        var cred = AskCredentials(App.T.T("Credentials for the push"), string.Format(CultureInfo.CurrentCulture, App.T.T("User with privilege 15 (or with enable) on {0}."), who), true);
        if (cred is null)
        {
            Shell.SetStatus(App.T.T("Push cancelled."), null);
            return;
        }
        tabs.Select(4);
        var run = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Push(prepared, cred, App.Settings.ExtraAllowedCommands);
        });
        FillGrid();
        if (run is null)
        {
            return;
        }
        recordPath = run.RecordPath;
        UpdateButtons();
        if (Dialogs.Ask(FindForm(), string.Format(CultureInfo.CurrentCulture, App.T.T("Push finished.{0}{0}{1}{0}{0}Record: {2}{0}{0}Open it in the browser? (from there you can print it or save it as PDF)"), "\r\n", run.Summary, run.RecordPath), S("Configure switches")))
        {
            OpenPath(run.RecordPath);
        }
    }

    protected override void UpdateButtons()
    {
        if (recordButton is null)
        {
            return;
        }
        var busy = Shell.Busy;
        queryButton.Enabled = !busy;
        previewButton.Enabled = !busy;
        pushButton.Enabled = !busy;
        recordButton.Enabled = !busy && recordPath.Length > 0;
    }
}
