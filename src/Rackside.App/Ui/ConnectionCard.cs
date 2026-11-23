using System.ComponentModel;
using System.Globalization;
using Rackside.Transport.Console;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Ui;

/// <summary>How to reach the switch: console (USB or serial COM port) and, optionally, SSH; plus the login.</summary>
internal sealed class ConnectionCard : Card
{
    private readonly ComboBox port;
    private readonly ComboBox baud;
    private readonly RadioButton usb = Make.Radio(S("USB console (mini-USB / USB-C)"), true);
    private readonly RadioButton serial = Make.Radio(S("Serial console (RJ45 / DB9)"));
    private readonly RadioButton overConsole = Make.Radio(S("Console"), true);
    private readonly RadioButton overSsh = Make.Radio("SSH");
    private readonly TextBox ip = Make.Input();
    private readonly TextBox sshPort = Make.Input("22", width: 80);
    private readonly TextBox fingerprint = Make.Input(width: 420);
    private readonly TextBox user = Make.Input();
    private readonly TextBox password = Make.Input(password: true);
    private readonly TextBox enable = Make.Input(password: true);
    private readonly Fields consoleFields = new();
    private readonly Fields sshFields = new();

    public ConnectionCard(AppState app, bool allowSsh, bool withLogin = true)
        : base(S("Connection"), allowSsh
            ? S("Console cable or SSH. Over SSH the fingerprint is always verified before sending the password.")
            : S("Console cable connected to the switch."))
    {
        ArgumentNullException.ThrowIfNull(app);
        var card = this;

        port = Make.Combo([], width: 160, editable: true);
        RefreshPorts(app.Settings.PreferredCOMPort);
        var refresh = Make.Secondary(S("Refresh"), (_, _) => RefreshPorts(port.Text));
        refresh.Margin = new Padding(6, 0, 0, 4);
        baud = Make.Combo(["9600", "19200", "38400", "57600", "115200"], 0, 160);

        if (allowSsh)
        {
            card.Add(Make.Row(overConsole, overSsh));
            overSsh.CheckedChanged += (_, _) => Toggle();
        }
        consoleFields.Add(S("Port"), Make.Row(port, refresh));
        consoleFields.Add(S("Speed (baud)"), baud);
        consoleFields.Add(S("Cable"), Make.Row(usb, serial));
        card.Add(consoleFields);
        sshFields.Add(S("IP address"), ip);
        sshFields.Add(S("SSH port"), sshPort);
        sshFields.Add(S("Expected fingerprint (optional)"), fingerprint);
        sshFields.Full(Make.Note(S("SHA256:... or MD5 as shown by PuTTY. If empty, it is compared with the one saved from previous connections; the first time you are asked.")));
        if (allowSsh)
        {
            card.Add(sshFields);
        }
        if (withLogin)
        {
            var login = new Fields();
            login.Add(S("User"), user);
            login.Add(S("Password"), password);
            login.Add(S("Enable (optional)"), enable);
            login.Full(Make.Note(S("They are not saved: they are only used for this operation and are masked in every log and record.")));
            card.Add(login);
        }
        Toggle();
    }

    private void RefreshPorts(string preferred)
    {
        port.Items.Clear();
        var names = SerialChannel.PortNames();
        port.Items.AddRange([.. names]);
        port.Text = names.Contains(preferred, StringComparer.OrdinalIgnoreCase) || names.Count == 0 ? preferred : names[0];
    }

    private void Toggle()
    {
        sshFields.Visible = overSsh.Checked;
        consoleFields.Visible = !overSsh.Checked;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool OverSsh => overSsh.Checked;

    public string PortCom => port.Text.Trim().ToUpperInvariant();

    public int BaudRate => int.TryParse(baud.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b > 0 ? b : 9600;

    public string Medium => usb.Checked ? "USB" : "serial";

    public string IP => ip.Text.Trim();

    public int SshPort => int.TryParse(sshPort.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : 22;

    public string Fingerprint => fingerprint.Text.Trim();

    public string User => user.Text.Trim();

    public string Password => password.Text;

    public string Enable => enable.Text;
}
