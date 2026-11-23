using Rackside.Core.Localization;

namespace Rackside.Core.Upgrade;

/// <summary>An entry of "dir" with its size.</summary>
public sealed class FlashEntry(string name, bool isDir, long bytes)
{
    public string Name { get; } = name;
    public bool IsDir { get; } = isDir;
    public long Bytes { get; set; } = bytes;
}

/// <summary>"dir" with sizes, total and free.</summary>
public sealed record FlashListing(long Total, long Free, IReadOnlyList<FlashEntry> Entries)
{
    /// <summary>Null if the device does not exist (no "bytes total" line).</summary>
    public static FlashListing? Parse(string? text)
    {
        if (!Ci.TryMatch(text, "(\\d+)\\s+bytes total\\s*\\((\\d+)\\s+bytes free\\)", out var m))
        {
            return null;
        }
        var entries = new List<FlashEntry>();
        foreach (var l in Ci.Split(text, "\\r?\\n"))
        {
            if (Ci.TryMatch(l, "^\\s*\\d+\\s+([d-][rwx-]+)\\s+(\\d+)\\s+.*\\s(\\S+)\\s*$", out var e))
            {
                entries.Add(new FlashEntry(e.Groups[3].Value, e.Groups[1].Value.StartsWith('d'), long.Parse(e.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return new FlashListing(long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), long.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), entries);
    }
}

/// <summary>What "Connect and query" reads from the switch.</summary>
public sealed class UpgradeInfo
{
    public string Hostname { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public string OS { get; set; } = "IOS";
    public string Version { get; set; } = string.Empty;
    public string ImageSystem { get; set; } = string.Empty;
    /// <summary>First element of the running image path in flash (file or folder).</summary>
    public string ImageTop { get; set; } = string.Empty;
    public long ImageBytes { get; set; }
    /// <summary>IOS-XE: BUNDLE or INSTALL.</summary>
    public string ModeXE { get; set; } = string.Empty;
    public string Uptime { get; set; } = string.Empty;
    public int Members { get; set; } = 1;
    public string Boot { get; set; } = string.Empty;
    public bool BootManual { get; set; }
    public long FlashTotal { get; set; }
    public long FlashFree { get; set; }
    public IReadOnlyList<FlashEntry> FlashEntries { get; set; } = [];
    /// <summary>Images in flash that are not the running one (classic IOS).</summary>
    public IReadOnlyList<FlashEntry> Cleanable { get; set; } = [];
    public string BootTop { get; set; } = string.Empty;
    public FlashListing? Usb { get; set; }
    public string UsbName { get; set; } = string.Empty;
    public string UsbProblem { get; set; } = string.Empty;
    public string UsbDiagnostic { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
}

/// <summary>Image name rules and other pure helpers of "Update IOS".</summary>
public static class ImageNames
{
    /// <summary>c2960x-universalk9-mz.152-7.E10.bin -> 15.2(7)E10 ; cat9k_lite_iosxe.17.09.05.SPA.bin -> 17.09.05</summary>
    public static string VersionFromFile(string? name)
    {
        if (Ci.TryMatch(name, "(?i)-(?:mz|tar)\\.(\\d{2})(\\d)-(\\d+)\\.([A-Za-z]+\\d+[a-z]?)", out var m))
        {
            return Ci.F("{0}.{1}({2}){3}", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
        }
        return Ci.TryMatch(name, "(?i)iosxe\\.(\\d+\\.\\d+\\.\\d+[a-z]?)\\.", out m) ? m.Groups[1].Value : string.Empty;
    }

    /// <summary>Prefix the new image must have for this device.</summary>
    public static string Family(UpgradeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.OS == "IOS-XE")
        {
            return Ci.IsMatch(info.Model, "(?i)^C9200") ? "cat9k_lite_iosxe" : "cat9k_iosxe";
        }
        var f = Ci.Split(Ci.Replace(info.ImageSystem.Replace("\"", string.Empty, StringComparison.Ordinal), "^[A-Za-z0-9\\-]+:/*", string.Empty), "/")[^1];
        return Ci.TryMatch(f, "^(.+?)-(mz|tar)\\.", out var m) ? m.Groups[1].Value : string.Empty;
    }

    /// <summary>c2960x-universalk9-tar.152-7.E14.tar -> c2960x-universalk9-mz.152-7.E14 (what archive download-sw creates).</summary>
    public static string TarFolder(string name) => Ci.Replace(Ci.Replace(name, "(?i)-tar\\.", "-mz."), "(?i)\\.tar$", string.Empty);

    /// <summary>
    /// Images in flash that are NOT the running one (classic IOS): loose .bin/.tar, image folders and "update".
    /// Never the running image, configuration files, vlan.dat, crashinfo...
    /// </summary>
    public static List<FlashEntry> CleanupCandidates(IEnumerable<FlashEntry> entries, string imageTop)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var res = new List<FlashEntry>();
        foreach (var e in entries)
        {
            var n = e.Name;
            if (string.IsNullOrEmpty(n) || Ci.Eq(n, imageTop))
            {
                continue;
            }
            var isImage = (!e.IsDir && Ci.IsMatch(n, "(?i)\\.(bin|tar)$")) || (e.IsDir && (Ci.IsMatch(n, "(?i)^[a-z0-9_]+-[a-z0-9_]+-(mz|tar)\\.\\d") || Ci.Eq(n, "update")));
            if (isImage)
            {
                res.Add(new FlashEntry(n, e.IsDir, e.Bytes));
            }
        }
        return res;
    }

    public static long Sum(IEnumerable<FlashEntry> items) => items.Sum(i => i.Bytes);

    /// <summary>Steps to do it by hand over the console when the new image does not fit next to the running one.</summary>
    public static IReadOnlyList<string> ManualSpaceProcedure(UpgradeInfo info, string name, string sourceSw, Translator t)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(t);
        var bin = Ci.Replace(Ci.Replace(name, "(?i)-tar\\.", "-mz."), "(?i)\\.tar$", ".bin");
        var src = sourceSw.Length > 0 && sourceSw != "flash:" ? sourceSw : "usbflash0:";
        return
        [
            t.T("MANUAL CONSOLE PROCEDURE (the tool does not do it: it involves deleting the running image)"),
            t.T("  Requirements: console cable connected the whole time, the .bin image on a USB drive and on the PC, and its Cisco MD5."),
            t.T("  1. Back up the configuration off the switch and save: write memory"),
            Ci.F(t.T("  2. Delete the running image (the switch keeps running from RAM; do NOT reload from here): delete /force /recursive flash:/{0}"), info.ImageTop),
            Ci.F(t.T("  3. Copy the new one: copy {0}{1} flash:{1}"), src, bin),
            Ci.F(t.T("  4. Verify: verify /md5 flash:{0}  (must match the Cisco MD5)"), bin),
            Ci.F(t.T("  5. boot system flash:{0}   /   write memory   /   reload"), bin),
            t.T("  If something fails before reloading: repeat step 3. If the switch reloaded without an image it stays at \"switch:\";"),
            Ci.F(t.T("  from there: flash_init / copy xmodem: flash:{0} (Xmodem from the terminal) / set BOOT flash:{0} / boot"), bin),
        ];
    }

    /// <summary>
    /// Why there is no readable USB drive, from what dir answered and the USB events of the switch log
    ///. Returns the explanation (with the "read again" hint) and the events found.
    /// </summary>
    public static (string Text, string Logged, IReadOnlyList<string> Events) UsbDiagnostic(string? logUsb, string? logBuffer, string? drive, Translator t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var noBuffer = Ci.IsMatch(logBuffer, "(?i)Buffer logging\\s*:\\s*disabled");
        // USB_CONSOLE messages are about the console port, not the drive
        var events = Ci.Split(logUsb, "\\r?\\n").Where(l => Ci.IsMatch(l, "%USB") && !Ci.IsMatch(l, "USB_CONSOLE") && !Ci.IsMatch(l, "(?i)Invalid input")).Select(l => l.Trim()).ToList();
        const string withdrawn = "(?i)has been removed|removed";
        const string rejection = "(?i)READY_TEST_FAILED|DEVSTARTSTOP|ENUM_FAIL|Failed to enumerate|OVERCURRENT|over.?current|NOT_SUPPORTED|not supported";
        const string format = "(?i)USBFLASH-\\d-FORMAT|unexpected values|needs formatting|Invalid DOS";
        const string inserted = "(?i)has been inserted|inserted";
        var scenario = "NONE";
        var evt = string.Empty;
        if (events.Count > 0)
        {
            // Only counts what happened after the drive was last removed
            var from = 0;
            for (var i = 0; i < events.Count; i++)
            {
                if (Ci.IsMatch(events[i], withdrawn))
                {
                    from = i + 1;
                }
            }
            if (from >= events.Count)
            {
                scenario = "WITHDRAWN";
                evt = events[^1];
            }
            else
            {
                var window = events.Skip(from).ToList();
                foreach (var (name, pattern) in new[] { ("REJECTED", rejection), ("FORMAT", format), ("INSERTED", inserted) })
                {
                    var hit = window.Where(w => Ci.IsMatch(w, pattern)).ToList();
                    if (hit.Count > 0)
                    {
                        scenario = name;
                        evt = hit[^1];
                        break;
                    }
                }
            }
        }
        if (Ci.TryMatch(evt, "(%USB\\S*:.*)$", out var me))
        {
            evt = me.Groups[1].Value.Trim();
        }
        var dev = drive ?? string.Empty;
        if (dev.Length == 0 && Ci.TryMatch(evt, "(?i)(usbflash\\d+)", out var md))
        {
            dev = md.Groups[1].Value.ToLowerInvariant() + ":";
        }
        if (dev.Length == 0)
        {
            dev = "usbflash0:";
        }
        var fat = t.T("format it as FAT32 with an MBR partition table and a single partition (Windows only offers FAT32 on drives of 32 GB or less)");
        var read = t.T(" Then click \"Read switch USB\".");
        string text;
        switch (scenario)
        {
            case "REJECTED":
                text = Ci.F(t.T("The switch detected the drive but rejected it when initializing it ({0}). It is not the format: that drive is not compatible with the switch. Use another one, preferably USB 2.0 of 8 GB or less; USB 3.x and high-capacity drives often fail on the 2960-X."), evt);
                break;
            case "FORMAT":
                text = Ci.F(t.T("The switch sees the drive but does not understand its partition ({0}). {1}, or format it from the switch with \"format {2}\" (erases the drive) and then copy the image from the PC."), evt, fat[..1].ToUpper(System.Globalization.CultureInfo.CurrentCulture) + fat[1..], dev);
                break;
            case "WITHDRAWN":
                text = Ci.F(t.T("The last thing the switch logged is that the drive was removed ({0}). Insert it again and wait about 10 seconds."), evt);
                break;
            case "INSERTED":
                text = Ci.F(t.T("The switch logged the drive ({0}) but does not have it mounted. Remove it, insert it again and wait about 10 seconds. If nothing changes, {1}, or try another drive."), evt, fat);
                break;
            default:
                if (!string.IsNullOrEmpty(drive))
                {
                    text = Ci.F(t.T("The switch has the {0} unit but answers \"Invalid DOS media or no media in slot\": there is no drive inserted or it cannot read it. If it is inserted, {1}, or format it from the switch with \"format {0}\" (erases the drive)."), drive, fat);
                }
                else
                {
                    text = t.T("The switch has no USB drive mounted and there are no USB events in its log: it does not see it. Check: (1) that it is in the type A USB port on the front (the mini-USB is the console); (2) that it goes all the way in: the slot is deep and a thick casing keeps it from making contact; (3) that it is USB 2.0 of 8 GB or less. Insert it with the switch powered on and wait about 10 seconds.");
                    if (noBuffer)
                    {
                        text += t.T(" (The switch does not keep a log in memory, so it cannot be known whether it detected it.)");
                    }
                }
                break;
        }
        return (text + read, text, events);
    }
}
