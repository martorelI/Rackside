using System.Security.Cryptography;
using Rackside.Core.Localization;
using Rackside.Core.Upgrade;

namespace Rackside.Tests;

/// <summary>tests/snapshots/upgrade: update plans for synthetic devices and options.</summary>
public sealed class UpgradeSnapshotTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rackside_upd_" + Guid.NewGuid().ToString("N"));

    public UpgradeSnapshotTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    private static FlashEntry E(string n, bool d, long b) => new(n, d, b);

    private static UpgradeInfo New2960() => new()
    {
        Hostname = "SW07", Model = "WS-C2960X-48FPD-L", Serial = "FOC1234X0AB", OS = "IOS", Version = "15.2(7)E3",
        ImageSystem = "flash:/c2960x-universalk9-mz.152-7.E3/c2960x-universalk9-mz.152-7.E3.bin", ImageTop = "c2960x-universalk9-mz.152-7.E3", ImageBytes = 30000000,
        Uptime = "2 weeks", Members = 1, Boot = "flash:/c2960x-universalk9-mz.152-7.E3/c2960x-universalk9-mz.152-7.E3.bin", FlashTotal = 122185728, FlashFree = 20000000,
        FlashEntries = [E("c2960x-universalk9-mz.152-7.E3", true, 512), E("c2960x-universalk9-mz.152-2.E8", true, 512), E("update", true, 512), E("vlan.dat", false, 736)],
        Cleanable = [E("c2960x-universalk9-mz.152-2.E8", true, 30000000), E("update", true, 5000000)], BootTop = "c2960x-universalk9-mz.152-7.E3",
        Usb = new FlashListing(8000000000, 7000000000, [E("c2960x-universalk9-tar.152-7.E10.tar", false, 40000000), E("c3750e-universalk9-tar.152-4.E10.tar", false, 38000000)]),
        UsbName = "usbflash0:", Family = "c2960x-universalk9",
    };

    private static UpgradeInfo New9200() => new()
    {
        Hostname = "SW09", Model = "C9200L-48P-4X", Serial = "JAE0000TEST", OS = "IOS-XE", Version = "17.09.04a", ImageSystem = "flash:packages.conf", ImageTop = "packages.conf",
        ModeXE = "INSTALL", Uptime = "1 week", Members = 1, Boot = "flash:packages.conf", BootManual = true, FlashTotal = 1600000000, FlashFree = 300000000,
        FlashEntries = [E("packages.conf", false, 7000)], Cleanable = [], BootTop = "packages.conf",
        Usb = new FlashListing(8000000000, 7000000000, [E("cat9k_lite_iosxe.17.09.05.SPA.bin", false, 400000000)]), UsbName = "usbflash0:", Family = "cat9k_lite_iosxe",
    };

    private LocalImage Image(string name, int size)
    {
        var path = Path.Combine(dir, name);
        var data = new byte[size];
        File.WriteAllBytes(path, data);
        return new LocalImage(name, path, size, Convert.ToHexStringLower(MD5.HashData(data)));
    }

    private static readonly UpgradeOptions Base = new() { PortCom = "COM3", BaudRate = 9600, PortSsh = 22, VelXmodem = 115200 };
    private static readonly UpgradeOptions Ssh = Base with { Connection = "SSH", Source = "SSH", IP = "10.0.0.7", User = "admin", Password = "Pa55w0rd!", Reload = true };

    public static TheoryData<string> Cases() => new(
        "usb_2960_tar_cleanup", "usb_2960_no_cleanup_checked", "usb_other_family_denied", "usb_other_family_allowed", "ssh_2960_bin_reuse", "ssh_2960_no_space_manual",
        "ssh_2960_delete_running_before", "serial_2960_bin_delete_after", "usb_9200_no_space", "usb_9200_install_remove_inactive", "ssh_2960_same_as_running");

    private (UpgradeInfo, UpgradeOptions, LocalImage?) Scenario(string name)
    {
        var bin3 = Image("c2960x-universalk9-mz.152-7.E10.bin", 3000000);
        var bin40 = Image("c2960x-universalk9-mz.152-7.E12.bin", 40000000);
        var full = New2960();
        full.FlashFree = 10000000;
        full.ImageBytes = 35000000;
        full.Cleanable = [];
        switch (name)
        {
            case "usb_2960_tar_cleanup":
                return (New2960(), Base with { Source = "USB", FileUsb = "c2960x-universalk9-tar.152-7.E10.tar", CleanFlash = true, Reload = true, SaveBefore = true, Md5Expected = "0123456789abcdef0123456789abcdef" }, null);
            case "usb_2960_no_cleanup_checked":
                return (New2960(), Base with { Source = "USB", FileUsb = "c2960x-universalk9-tar.152-7.E10.tar", Reload = true }, null);
            case "usb_other_family_denied":
                return (New2960(), Base with { Source = "USB", FileUsb = "c3750e-universalk9-tar.152-4.E10.tar", CleanFlash = true }, null);
            case "usb_other_family_allowed":
                return (New2960(), Base with { Source = "USB", FileUsb = "c3750e-universalk9-tar.152-4.E10.tar", CleanFlash = true, IgnoreFamily = true }, null);
            case "ssh_2960_bin_reuse":
                var reuse = New2960();
                reuse.FlashFree = 60000000;
                reuse.FlashEntries = [.. reuse.FlashEntries, E("c2960x-universalk9-mz.152-7.E10.bin", false, 3000000)];
                reuse.Cleanable = [.. reuse.Cleanable, E("c2960x-universalk9-mz.152-7.E10.bin", false, 3000000)];
                return (reuse, Ssh with { FileLocal = bin3.Path }, bin3);
            case "ssh_2960_no_space_manual":
                return (full, Ssh with { FileLocal = bin40.Path }, bin40);
            case "ssh_2960_delete_running_before":
                return (full, Ssh with { FileLocal = bin40.Path, CleanFlash = true, CleanAll = true, Md5Expected = bin40.Md5 }, bin40);
            case "serial_2960_bin_delete_after":
                return (New2960(), Base with { Source = "SERIAL", FileLocal = bin3.Path, CleanFlash = true, CleanAll = true, Md5Expected = "ffffffffffffffffffffffffffffffff" }, bin3);
            case "ssh_2960_same_as_running":
                var bin14 = Image("c2960x-universalk9-mz.152-7.E14.bin", 3000000);
                var run = New2960();
                run.Version = "15.2(7)E14";
                run.ImageSystem = "flash:c2960x-universalk9-mz.152-7.E14.bin";
                run.ImageTop = "c2960x-universalk9-mz.152-7.E14.bin";
                run.ImageBytes = 3000000;
                run.Boot = "flash:c2960x-universalk9-mz.152-7.E14.bin";
                run.BootTop = "c2960x-universalk9-mz.152-7.E14.bin";
                run.FlashFree = 90000000;
                run.FlashEntries = [E("c2960x-universalk9-mz.152-7.E14.bin", false, 3000000), E("vlan.dat", false, 736)];
                run.Cleanable = [];
                return (run, Ssh with { FileLocal = bin14.Path, CleanFlash = true, CleanAll = true, Reload = false, Md5Expected = bin14.Md5 }, bin14);
            case "usb_9200_no_space":
                return (New9200(), Base with { Source = "USB", FileUsb = "cat9k_lite_iosxe.17.09.05.SPA.bin", CleanFlash = true }, null);
            default:
                return (New9200(), Base with { Source = "USB", FileUsb = "cat9k_lite_iosxe.17.09.05.SPA.bin", CleanInactive = true, Reload = true }, null);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Core_reproduces_the_update_plan(string name)
    {
        var (info, opt, local) = Scenario(name);
        var plan = UpgradePlanner.Build(info, opt, local, Translator.English);
        Snapshot.Match(plan.Text, "upgrade", name + ".txt");
    }

    [Fact]
    public void The_running_image_is_never_deleted_without_the_checkbox()
    {
        foreach (var name in Cases().Cast<object[]>().Select(c => (string)c[0]))
        {
            var (info, opt, local) = Scenario(name);
            var plan = UpgradePlanner.Build(info, opt, local, Translator.English);
            if (!opt.CleanAll)
            {
                Assert.Equal(string.Empty, plan.DeleteInUse);
                Assert.DoesNotContain(plan.Cleanup, c => c.Name == info.ImageTop);
            }
        }
    }

    [Fact]
    public void Deleting_the_running_image_before_copying_requires_the_cisco_md5()
    {
        var (info, opt, local) = Scenario("ssh_2960_delete_running_before");
        var plan = UpgradePlanner.Build(info, opt with { Md5Expected = string.Empty }, local, Translator.English);
        Assert.Equal("BEFORE", plan.DeleteInUse);
        Assert.Contains(plan.Denials, d => d.Contains("MD5 published by Cisco", StringComparison.Ordinal));
    }
}
