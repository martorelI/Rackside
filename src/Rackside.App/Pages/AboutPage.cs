using System.Diagnostics;
using Rackside.App.Ui;
using Rackside.Core;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Pages;

/// <summary>About: version, author, support, what it does, trademarks and the data responsibility (rule 9).</summary>
internal sealed class AboutPage : Page
{
    public AboutPage(MainForm shell) : base(shell, S("About"), S("Version, support and data responsibility."))
    {
        var list = new CardList();
        var who = list.Add(new Card(AppInfo.Name));
        var head = Make.Row(new PictureBox { Image = Theme.Logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(72, 72), Margin = new Padding(0, 0, 16, 0) });
        var lines = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        lines.Controls.Add(Make.Label(F("Version {0}", AppInfo.Version), Theme.Body, Theme.Muted));
        lines.Controls.Add(Make.Label(F("Developed by {0}", AppInfo.Author), Theme.BodyBold, Theme.Accent));
        var mail = new LinkLabel { Text = F("Support: {0}", AppInfo.SupportEmail), AutoSize = true, LinkColor = Theme.Accent, Margin = new Padding(0, 6, 0, 0) };
        mail.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("mailto:" + AppInfo.SupportEmail) { UseShellExecute = true });
        lines.Controls.Add(mail);
        head.Controls.Add(lines);
        who.Add(head);

        var what = list.Add(new Card(S("What it does")));
        what.Add(Make.Note(S("Tools for Catalyst switches (IOS / IOS-XE) over the console (serial or USB) and over SSH:"), Theme.TextColor));
        foreach (var (title, text) in new[]
        {
            ("Restore from backup", "Applies the backup of a failed switch to its replacement. If the model is the same it applies it as is; otherwise it remaps the interfaces and converts between IOS-XE and classic IOS. Generates the DRP record of the procedure."),
            ("Factory reset", "Backs up the configuration, keeps the IOS image, deletes keys, vlan.dat and startup-config, and reloads without saving. Generates the erase record."),
            ("Update IOS", "Queries the version over the console or SSH and loads the new image from a USB drive, over the console (Xmodem) or over SSH (SCP), with MD5 verification, reload and change record."),
            ("Configure switches", "CSV switch inventory, a query that documents each one and a push of common parameters (LLDP/CDP, banners, VTP, SNMP, NTP, syslog, DNS, SSH, services, STP, users) over SSH to the checked switches, with a prior backup, verification and record."),
            ("Locate IP", "Finds the switch and port where an IP or MAC address is connected: ARP of the gateway switch, then the MAC tables switch by switch over SSH (read only, layer 2)."),
        })
        {
            what.Add(Make.Label(S(title), Theme.BodyBold));
            what.Add(Make.Note(S(text)));
        }
        what.Add(Make.Note(S("Compatibility: Catalyst 2960 / 2960-S / 2960-X / 2960-XR (classic IOS) and Catalyst 9200 / 9200L (IOS-XE).")));

        var data = list.Add(new Card(S("Your data")));
        var p = App.Paths;
        var f = App.Settings.Folders;
        data.Add(Make.Note(F("Data folder: {0}", p.Data), Theme.TextColor));
        data.Add(Make.Note(F("Settings: {0}", App.SettingsPath), Theme.TextColor));
        data.Add(Make.Note(F("Subfolders: {0}, {1}, {2}, {3}, {4}, {5}, {6}", f.Restores, f.FactoryResets, f.Upgrades, f.Backups, f.SSHFingerprints, f.Inventory, f.Pushes), Theme.TextColor));
        data.Add(Make.Note(S("They contain backups, transcripts and records with network data: protecting them (permissions, encryption, backup) is the operator's responsibility."), Theme.Danger));
        data.Add(Make.Secondary(S("Open the data folder"), (_, _) => { Directory.CreateDirectory(p.Data); OpenPath(p.Data); }));

        var marks = list.Add(new Card(S("Trademarks")));
        marks.Add(Make.Note(S("Cisco, Catalyst, IOS and IOS-XE are trademarks of Cisco Systems, Inc. This tool is independent: it is not affiliated with, sponsored or endorsed by Cisco. The trademarks are mentioned only to indicate which devices it is compatible with.")));
        SetBody(list);
    }
}
