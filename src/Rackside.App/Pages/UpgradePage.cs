using System.Globalization;
using Rackside.App.Ui;
using Rackside.Core.Security;
using Rackside.Core.Upgrade;
using Rackside.Operations.Upgrade;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Pages;

/// <summary>Update IOS: query, choose the image, plan (with its denials) and update with MD5 and reload.</summary>
internal sealed class UpgradePage : Page
{
    private readonly UpgradeOperation op;
    private readonly ConnectionCard conn;
    private readonly RadioButton fromUsb = Make.Radio(S("USB drive in the switch"), true);
    private readonly RadioButton fromSerial = Make.Radio(S("Xmodem over the console"));
    private readonly RadioButton fromSsh = Make.Radio(S("SCP over SSH"));
    private readonly ComboBox usbFile = Make.Combo([], width: 420);
    private readonly Label usbInfo = Make.Note(S("Query the switch to read its USB drive."));
    private readonly PathField localFile;
    private readonly TextBox md5 = Make.Input(width: 320);
    private readonly ComboBox xmodemSpeed = Make.Combo(["115200", "57600", "38400", "19200", "9600"], 0, 120);
    private readonly CheckBox saveBefore = Make.Check(S("Save the configuration first (write memory)"), true);
    private readonly CheckBox cleanFlash = Make.Check(S("Delete the other images from flash (never the running one)"));
    private readonly CheckBox cleanAll = Make.Check(S("Also include the running image"));
    private readonly CheckBox cleanInactive = Make.Check(S("IOS-XE: install remove inactive"), true);
    private readonly CheckBox reload = Make.Check(S("Reload when finished"), true);
    private readonly CheckBox otherFamily = Make.Check(S("Allow an image from another family"));
    private readonly PathField folder;
    private readonly TextBox ticket = Make.Input();
    private readonly TextBox responsible = Make.Input();
    private readonly TextBox info = Make.Area(readOnly: true, mono: true);
    private readonly TextBox planText = Make.Area(readOnly: true, mono: true);
    private readonly LogView log = new();
    private readonly Tabs tabs = new();
    private readonly Button query;
    private readonly Button readUsb;
    private readonly Button prepare;
    private readonly Button update;
    private readonly Button record;
    private UpgradeSession? session;
    private string recordPath = string.Empty;
    private bool confirming;

    public UpgradePage(MainForm shell) : base(shell, S("Update IOS"), S("Loads a new image from the switch USB, over Xmodem or over SCP, with MD5 verification and reload."))
    {
        op = new UpgradeOperation(App.Paths.Fingerprints, App.T, new SecretMasker(), shell.Operator());
        op.Log.Line += log.Append;
        conn = new ConnectionCard(App, allowSsh: true);
        folder = new PathField(App.Paths.Upgrades, folder: true);
        localFile = new PathField(string.Empty, folder: false, S("IOS images (*.bin;*.tar)|*.bin;*.tar|All files (*.*)|*.*"));

        var step1 = new CardList();
        step1.Add(conn);
        var rec = step1.Add(new Card(S("Record")));
        var r = new Fields();
        r.Add(S("Evidence folder"), folder);
        r.Add(S("Ticket / change"), ticket);
        r.Add(S("Responsible"), responsible);
        rec.Add(r);

        var step2 = new Panel();
        step2.Controls.Add(info);

        var step3 = new CardList();
        var img = step3.Add(new Card(S("Image"), S("Take the MD5 published by Cisco: it is compared with the file and with what the switch computes.")));
        img.Add(Make.Row(fromUsb, fromSerial, fromSsh));
        var fi = new Fields();
        fi.Add(S("Image on the USB"), usbFile);
        fi.Full(usbInfo);
        fi.Add(S("Image on this computer"), localFile);
        fi.Add(S("Cisco MD5"), md5);
        fi.Add(S("Xmodem speed (baud)"), xmodemSpeed);
        img.Add(fi);
        var opts = step3.Add(new Card(S("Options"), S("The running image is never deleted unless you check it and accept the warning.")));
        foreach (var c in new[] { saveBefore, cleanFlash, cleanAll, cleanInactive, reload, otherFamily })
        {
            opts.Add(c);
        }
        cleanAll.CheckedChanged += (_, _) => Guard(cleanAll, () => op.ConfirmCleanAll());
        otherFamily.CheckedChanged += (_, _) => Guard(otherFamily, () => op.ConfirmOtherFamily(session));
        cleanFlash.CheckedChanged += (_, _) =>
        {
            if (!cleanFlash.Checked)
            {
                cleanAll.Checked = false;
            }
            cleanAll.Enabled = cleanFlash.Checked;
        };
        cleanAll.Enabled = false;

        var step4 = new Panel();
        step4.Controls.Add(planText);

        tabs.Add(S("1. Connection"), step1);
        tabs.Add(S("2. Current version"), step2);
        tabs.Add(S("3. Image"), step3);
        tabs.Add(S("4. Plan"), step4);
        tabs.Add(S("Log"), log);
        SetBody(tabs);

        query = Action(Make.Secondary(S("Connect and query"), async (_, _) => await Query()));
        readUsb = Action(Make.Secondary(S("Read switch USB"), async (_, _) => await ReadUsb()));
        prepare = Action(Make.Secondary(S("Prepare plan"), (_, _) => Plan()));
        update = Action(Make.Primary(S("Update"), async (_, _) => await RunUpdate()));
        record = Action(Make.Secondary(S("Open record"), (_, _) => OpenPath(recordPath)));
        UpdateButtons();
    }

    /// <summary>Checking a dangerous box asks first (security rule 5); if not accepted it is unchecked again.</summary>
    private void Guard(CheckBox box, Func<bool> confirm)
    {
        if (confirming || !box.Checked)
        {
            return;
        }
        confirming = true;
        try
        {
            if (!confirm())
            {
                box.Checked = false;
            }
        }
        finally
        {
            confirming = false;
        }
    }

    private UpgradeOptions Options() => new()
    {
        Connection = conn.OverSsh ? "SSH" : "SERIAL", PortCom = conn.PortCom, BaudRate = conn.BaudRate, ConsoleMedium = conn.Medium,
        User = conn.User, Password = conn.Password, Enable = conn.Enable, IP = conn.IP, PortSsh = conn.SshPort, FingerprintExpected = conn.Fingerprint,
        FolderRecord = folder.Value, Ticket = ticket.Text.Trim(), Responsible = responsible.Text.Trim(),
        Source = fromUsb.Checked ? "USB" : fromSerial.Checked ? "SERIAL" : "SSH", FileLocal = localFile.Value, FileUsb = usbFile.Text,
        Md5Expected = md5.Text.Replace(" ", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant(),
        VelXmodem = int.TryParse(xmodemSpeed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 115200,
        SaveBefore = saveBefore.Checked, CleanFlash = cleanFlash.Checked, CleanAll = cleanFlash.Checked && cleanAll.Checked, CleanInactive = cleanInactive.Checked,
        Reload = reload.Checked, IgnoreFamily = otherFamily.Checked,
    };

    private void ShowUsb()
    {
        usbFile.Items.Clear();
        var i = session?.Info;
        if (i?.Usb is { } usb)
        {
            usbFile.Items.AddRange([.. usb.Entries.Where(e => !e.IsDir && (e.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))).Select(e => e.Name)]);
            if (usbFile.Items.Count > 0)
            {
                usbFile.SelectedIndex = 0;
            }
            usbInfo.Text = string.Format(CultureInfo.CurrentCulture, App.T.T("{0} detected: {1} .bin/.tar image(s)"), i.UsbName, usbFile.Items.Count);
        }
        else
        {
            usbInfo.Text = i is null ? S("Query the switch to read its USB drive.") : i.UsbDiagnostic.Length > 0 ? i.UsbDiagnostic : App.T.T("No USB detected on the switch.");
        }
    }

    private async Task Query()
    {
        var opt = Options();
        otherFamily.Checked = false;
        cleanAll.Checked = false;
        tabs.Select(4);
        var s = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Query(opt);
        });
        if (s is not null)
        {
            session = s;
            info.Text = s.InfoText.Replace("\n", "\r\n", StringComparison.Ordinal);
            ShowUsb();
            tabs.Select(1);
        }
        UpdateButtons();
    }

    private async Task ReadUsb()
    {
        if (session is null)
        {
            return;
        }
        var opt = Options();
        var ses = session;
        await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            op.ReadUsb(ses, opt);
        });
        ShowUsb();
        tabs.Select(2);
    }

    private UpgradePlan? Plan()
    {
        if (session is null)
        {
            return null;
        }
        try
        {
            var p = op.Plan(session, Options());
            planText.Text = p.Text.Replace("\n", "\r\n", StringComparison.Ordinal);
            tabs.Select(3);
            return p;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(FindForm(), ex.Message);
            return null;
        }
    }

    private async Task RunUpdate()
    {
        var p = Plan();
        if (p is null || session is null)
        {
            return;
        }
        if (p.Denials.Count > 0)
        {
            Dialogs.Error(FindForm(), string.Format(CultureInfo.CurrentCulture, App.T.T("Cannot update:{0}- {1}"), "\r\n", string.Join("\r\n- ", p.Denials)));
            return;
        }
        var opt = Options();
        var ses = session;
        tabs.Select(4);
        var r = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Update(ses, opt);
        });
        if (r is not null)
        {
            recordPath = r.RecordPath;
            var msg = App.T.T("Result: ") + App.T.T(r.Outcome.Code())
                + (r.VersionAfter.Length > 0 ? "\r\nVersion: " + ses.Info!.Version + " -> " + r.VersionAfter : string.Empty)
                + (r.ErrorFatal.Length > 0 ? "\r\n\r\n" + r.ErrorFatal : string.Empty)
                + App.T.T("\r\n\r\nEvidence and record in:\r\n") + ses.Folder;
            if (r.Outcome == UpgradeOutcome.Successful) { Dialogs.Info(FindForm(), msg, S("Update IOS")); }
            else { Dialogs.Error(FindForm(), msg, S("Update IOS")); }
        }
        UpdateButtons();
    }

    protected override void UpdateButtons()
    {
        if (record is null)
        {
            return;
        }
        var busy = Shell.Busy;
        query.Enabled = !busy;
        readUsb.Enabled = !busy && session is not null;
        prepare.Enabled = !busy && session is not null;
        update.Enabled = !busy && session is { Done: false };
        record.Enabled = !busy && recordPath.Length > 0;
    }
}
