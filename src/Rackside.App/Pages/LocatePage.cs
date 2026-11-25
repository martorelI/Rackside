using Rackside.App.Ui;
using Rackside.Core;
using Rackside.Core.Configure;
using Rackside.Core.Locate;
using Rackside.Core.Security;
using Rackside.Operations.Locate;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Pages;

/// <summary>
/// Locate IP: from the ARP of the gateway switch to the switch and port where an address is connected,
/// following the MAC tables. Read only, over SSH, with the switch list of "Configure switches".
/// </summary>
internal sealed class LocatePage : Page
{
    private readonly LocateOperation op;
    private readonly Inventory inventory = new();
    private readonly TextBox target = Make.Input(width: 260);
    private readonly ComboBox start = Make.Combo([], 0, 420);
    private readonly CheckBox refresh = Make.Check(S("Refresh the ARP: ping from the start switch the addresses missing from it (the searched one and the switches)"));
    private readonly CheckBox trustNew = Make.Check(S("Accept new SSH fingerprints without asking (only switches with none saved)"));
    private readonly Label listInfo = Make.Note(string.Empty);
    private readonly TextBox result = Make.Area(height: 300, readOnly: true, mono: true);
    private readonly Tabs tabs = new();
    private readonly LogView log = new();
    private readonly Button locateButton;
    private readonly Button folderButton;
    private IReadOnlyList<ConfTarget> switches = [];
    private string folderPath = string.Empty;
    private string lastUser = string.Empty;

    public LocatePage(MainForm shell) : base(shell, S("Locate IP"), S("Finds the switch and port where an IP or MAC address is connected, following the ARP and MAC tables (read only)."))
    {
        op = new LocateOperation(new LocatePaths(Path.Combine(App.Paths.Inventory, "Locate"), App.Paths.Fingerprints), App.T, new SecretMasker(), shell.Operator());
        op.Log.Line += log.Append;

        var list = new CardList();
        var what = list.Add(new Card(S("What to locate")));
        var f = new Fields();
        f.Add(S("IP or MAC address"), target);
        f.Add(S("Start switch"), start);
        what.Add(f);
        what.Add(Make.Note(S("Start at the switch that is the gateway of that VLAN (usually the core): its ARP table gives the MAC of the address and of the other switches. From there the MAC tables are followed switch by switch.")));
        what.Add(listInfo);
        var options = list.Add(new Card(S("Options")));
        options.Add(refresh);
        options.Add(trustNew);
        options.Add(Make.Note(S("Only show commands are sent (and the pings, if checked), over SSH to the switches of the list in \"Configure switches\". Layer 2 only: no CDP or LLDP.")));
        var res = list.Add(new Card(S("Result")));
        res.Add(result);
        tabs.Add(S("1. Locate"), list);
        tabs.Add(S("Log"), log);
        SetBody(tabs);

        locateButton = Action(Make.Primary(S("Locate"), async (_, _) => await Locate()));
        Action(Make.Secondary(S("Reload the switch list"), (_, _) => LoadList()));
        folderButton = Action(Make.Secondary(S("Open folder"), (_, _) => OpenPath(folderPath)));
        target.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && locateButton.Enabled)
            {
                e.SuppressKeyPress = true;
                await Locate();
            }
        };
        LoadList();
    }

    public override void Activated() => LoadList();

    private static string Label(ConfTarget s) => s.Name + "  (" + s.IP + ")";

    /// <summary>The list saved by "Configure switches" (it may have changed since the last visit).</summary>
    private void LoadList()
    {
        if (Shell.Busy)
        {
            return;
        }
        var keep = start.SelectedItem as string;
        try
        {
            inventory.Rows.Clear();
            if (File.Exists(App.Paths.InventoryCsv))
            {
                inventory.Import(App.Paths.InventoryCsv, App.T, replace: true);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            log.Append(ex.Message);
        }
        switches = inventory.AllWithIp();
        start.Items.Clear();
        foreach (var s in switches)
        {
            start.Items.Add(Label(s));
        }
        var index = keep is null ? -1 : start.Items.IndexOf(keep);
        if (index < 0)
        {
            index = switches.ToList().FindIndex(s => s.Name.Contains("core", StringComparison.OrdinalIgnoreCase));
        }
        start.SelectedIndex = index >= 0 ? index : (start.Items.Count > 0 ? 0 : -1);
        listInfo.Text = switches.Count == 0
            ? App.T.T("The switch list is empty: add the switches in \"Configure switches\" (tab 1) and save the list.")
            : F("{0} switch(es) in the list ({1}).", switches.Count, App.Paths.InventoryCsv);
        UpdateButtons();
    }

    private async Task Locate()
    {
        var text = target.Text.Trim();
        if (!Validators.IsIPv4(text) && !MacAddress.TryNormalize(text, out _))
        {
            Dialogs.Error(FindForm(), App.T.T("Enter an IPv4 address (10.1.2.3) or a MAC address (aabb.ccdd.eeff, aa:bb:cc:dd:ee:ff or aa-bb-cc-dd-ee-ff)."));
            target.Focus();
            return;
        }
        if (start.SelectedIndex < 0 || start.SelectedIndex >= switches.Count)
        {
            Dialogs.Error(FindForm(), S("Choose the start switch."));
            return;
        }
        var first = switches[start.SelectedIndex];
        var cred = CredentialsDialog.Ask(FindForm()!, S("Credentials to locate"), F("Read only (show commands) on {0} and on the switches along the path.", first.Name), true, lastUser);
        if (cred is null)
        {
            return;
        }
        lastUser = cred.User;
        var req = new LocateRequest { Target = text, RefreshArp = refresh.Checked, TrustNew = trustNew.Checked };
        var list = switches;
        result.Text = string.Empty;
        tabs.Select(1);
        var r = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Locate(req, list, first, cred);
        });
        tabs.Select(0);
        if (r is null)
        {
            return;
        }
        result.Text = r.Text.Replace("\n", "\r\n", StringComparison.Ordinal);
        folderPath = r.Folder;
        UpdateButtons();
    }

    protected override void UpdateButtons()
    {
        if (folderButton is null)
        {
            return;
        }
        var busy = Shell.Busy;
        locateButton.Enabled = !busy && switches.Count > 0;
        folderButton.Enabled = !busy && folderPath.Length > 0;
    }
}
