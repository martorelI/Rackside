using Rackside.Core;
using Rackside.Core.Configure;
using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Records;

namespace Rackside.Tests;

/// <summary>tests/snapshots/records: the HTML records with synthetic data (logo and version normalized).</summary>
public class RecordsSnapshotTests
{
    private static readonly HtmlRecords R = new(Translator.English);

    private static PushCheck C(string c, string r, string d) => new(c, r, d);

    private static void Same(string file, string html)
    {
        var normalized = html.Replace(HtmlRecords.Logo, "LOGO", StringComparison.Ordinal)
            .Replace(AppInfo.Name + " " + AppInfo.Version, AppInfo.Name + " VERSION", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Snapshot.Match(normalized, "records", file);
    }

    private static readonly DrpRecordData Drp = new()
    {
        Result = "SUCCESSFUL WITH NOTES", Ticket = "INC-0042", Responsible = "operator <ops>", UserWindows = "tester", DeviceWork = "LAB-PC",
        StartText = "2026-11-22 10:00:00", EndText = "2026-11-22 10:12:34", Duration = "00:12:34", Remarks = "Replaced after a power surge & fan failure",
        FailedHost = "SW-OLD", FailedSerial = "JAE1111OLD", FailedModel = "C9200L-48P-4X", FailedOS = "IOS-XE 17.9.4a", HostnameFinal = "SW23",
        ReplacementModel = "WS-C2960XR-48TS-I", ReplacementSerial = "FOC0000TEST", ReplacementOS = "IOS 15.2(7)E6",
        IPManagement = "192.0.2.23 255.255.255.0 on Vlan1, gateway 192.0.2.1", BackupPath = "C:\\Backups\\SW-OLD.txt", BackupDate = "2026-11-01 08:00:00", BackupSize = "12,345 bytes",
        BackupHash = "AB12", Mode = "CONVERT (IOS-XE -> IOS)", Lines = 321, ConfigHash = "CD34", Folder = "C:\\Restores\\DRP_SW23_2026-11-22_100000",
        Verification = [C("Hostname", "OK", "SW23"), C("Enable secret", "NOTE", "No enable secret"), C("Management IP (Vlan1)", "FAIL", "192.0.2.23 255.255.255.0")],
        Errors = [new("login on-success log", "% Invalid input detected at '^' marker.")],
        Warnings = [new("[interface GigabitEthernet1/0/10] shutdown", "%LINK-3-UPDOWN: <down>")],
        Changes = [new("BLOCKED", "do reload", "Runs an exec mode command from configuration mode"), new("CHANGED", "ip domain name lab.local", "IOS-XE -> IOS syntax")],
        Mapping = [new("GigabitEthernet1/0/1", "GigabitEthernet1/0/1", "access"), new("TenGigabitEthernet1/1/1", "TenGigabitEthernet1/0/1", "uplink")],
        ConfigText = "hostname SW23\n!\ninterface Vlan1\n ip address 192.0.2.23 255.255.255.0\n!\nend",
    };

    [Fact]
    public void Drp_record() => Same("drp.html", R.Drp(Drp));

    [Fact]
    public void Drp_record_failed() => Same("drp_failed.html", R.Drp(Drp with
    {
        Result = "FAILED", ErrorFatal = "The connected switch (serial X) is not the one that was reviewed", Errors = [], Warnings = [], Changes = [], Mapping = [],
    }));

    [Fact]
    public void Reset_record() => Same("reset.html", R.Reset(new ResetRecordData
    {
        Result = "SUCCESSFUL", Ticket = "RMA-8841", Responsible = "technician", Reason = "Back to stock", UserWindows = "tester", DeviceWork = "LAB-PC",
        StartText = "2026-11-22 11:00:00", EndText = "2026-11-22 11:09:00", Duration = "00:09:00",
        Hostname = "SW07", Model = "WS-C2960X-48FPD-L", Serial = "FOC1234X0AB", OS = "IOS", Version = "15.2(7)E3", Members = 1, Filesystem = "flash", Protected = ["c2960x-universalk9-mz.152-7.E3"],
        BackupPath = "C:\\FactoryResets\\RESET_SW07\\SW07_running.txt", BackupHash = "EF56", Folder = "C:\\FactoryResets\\RESET_SW07",
        Actions = [C("Delete RSA keys", "OK", "Keys deleted"), C("Delete flash:vlan.dat", "NOTE", "%Error deleting")],
    }));

    [Fact]
    public void Upgrade_record() => Same("upgrade.html", R.Upgrade(new UpgradeRecordData
    {
        Result = "CANCELLED", ErrorFatal = "Stopped by the operator", Ticket = "CHG-2026-0112", Responsible = "technician", UserWindows = "tester", DeviceWork = "LAB-PC",
        StartText = "2026-11-22 12:00:00", EndText = "2026-11-22 12:30:00", Duration = "00:30:00",
        Hostname = "SW09", Model = "C9200L-48P-4X", Serial = "JAE0000TEST", Members = 1, OS = "IOS-XE", Version = "17.09.04a", VersionExpected = "17.09.05",
        ImageName = "cat9k_lite_iosxe.17.09.05.SPA.bin", Bytes = 9000000, Method = "SSH", Connection = "SSH", IP = "192.0.2.9", PortCom = "COM3",
        Md5Local = "0123456789abcdef0123456789abcdef", Md5Switch = "0123456789abcdef0123456789abcdef",
        Fingerprint = "rsa-sha2-512 SHA256:9TFTk9ozu2FY0yLbjoxmKp+VaIaWw4vi59QyWG1e+/c (Matches the saved one)",
        Folder = "C:\\Upgrades\\UPDATE_SW09", Actions = [C("SCP transfer", "OK", "9,000,000 bytes in 00:01:30")], Verification = [C("MD5 on the switch", "OK", "x = x")],
    }));

    [Fact]
    public void Push_record()
    {
        SwitchResult Sr(string n, string ip, int port, PushOutcome res, string det) => new(new ConfTarget(null, n, ip, port))
        {
            Hostname = n, Model = "WS-C2960X-48FPD-L", Platform = "IOS", Version = "15.2(7)E3", Serial = "FOC1", Result = res, Detail = det, Saved = "Yes (write memory [OK])",
            Start = new DateTime(2026, 11, 22, 13, 0, 5, DateTimeKind.Local), End = new DateTime(2026, 11, 22, 13, 1, 10, DateTimeKind.Local),
        };
        var r1 = Sr("SW01", "192.0.2.1", 22, PushOutcome.Successful, "12 check(s) verified");
        r1.Fingerprint = "rsa-sha2-512 SHA256:abc (Matches the saved one)";
        r1.Verification = [C("lldp run", "OK", "Confirmed with \"show lldp\""), C("logging buffered 64000", "NOTE", "Not found")];
        r1.Warnings = [("spanning-tree portfast", "%Warning: portfast <host>")];
        var r2 = Sr("SW02", "192.0.2.2", 2222, PushOutcome.Failed, "No response at 192.0.2.2:2222");
        r2.End = null;
        Same("push.html", R.Push(new PushRecordData
        {
            Ticket = "CHG-TEST-1", Responsible = "operator", UserWindows = "tester", DeviceWork = "LAB-PC", StartText = "2026-11-22 13:00:00", EndText = "2026-11-22 13:05:00", Duration = "00:05:00",
            Connection = "SSH (sweep of the checked switches)", UserSwitch = "admin", Groups = "LLDP and CDP, Syslog", Save = "Yes, only if there were no failures",
            View = "PUSH PREVIEW\nlldp run & <syslog>", Results = [r1, r2], Summary = "FAILED: 1, SUCCESSFUL: 1", Folder = "C:\\Pushes\\PUSH_20261122_130000",
        }, PushScenarios.Masker(PushScenarios.Parameters())));
    }

    [Fact]
    public void Minimal_record() => Same("minimal.html", R.Minimal("DRP execution record (reduced)",
        [("Result", "FAILED"), ("Ticket / incident", "INC-1"), ("Evidence folder", "C:\\x <y>")], "disk full", "DATE", "DEVICE"));

    [Fact]
    public void The_version_matches_the_build()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        Assert.StartsWith(AppInfo.Version, info, StringComparison.Ordinal);
    }
}
