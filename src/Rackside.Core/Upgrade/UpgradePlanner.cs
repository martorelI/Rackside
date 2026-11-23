using System.Globalization;
using System.Text;
using Rackside.Core.Localization;

namespace Rackside.Core.Upgrade;

/// <summary>What the operator chose in "Update IOS".</summary>
public sealed record UpgradeOptions
{
    /// <summary>Control connection: SSH or SERIAL.</summary>
    public string Connection { get; init; } = "SERIAL";
    public string PortCom { get; init; } = string.Empty;
    public int BaudRate { get; init; } = 9600;
    public string ConsoleMedium { get; init; } = "USB";
    public string User { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Enable { get; init; } = string.Empty;
    public string IP { get; init; } = string.Empty;
    public int PortSsh { get; init; } = 22;
    public string FingerprintExpected { get; init; } = string.Empty;
    public string FolderRecord { get; init; } = string.Empty;
    public string Ticket { get; init; } = string.Empty;
    public string Responsible { get; init; } = string.Empty;

    /// <summary>Where the image comes from: USB (on the switch), SERIAL (Xmodem) or SSH (SCP).</summary>
    public string Source { get; init; } = "USB";
    public string FileLocal { get; init; } = string.Empty;
    public string FileUsb { get; init; } = string.Empty;

    /// <summary>MD5 published by Cisco (lower case, no spaces).</summary>
    public string Md5Expected { get; init; } = string.Empty;
    public int VelXmodem { get; init; } = 115200;
    public bool SaveBefore { get; init; }
    public bool CleanFlash { get; init; }

    /// <summary>"Also include the running image" (security rule 5): only with CleanFlash and the operator's explicit warning.</summary>
    public bool CleanAll { get; init; }
    public bool CleanInactive { get; init; }
    public bool Reload { get; init; }

    /// <summary>"Allow an image from another family" (security rule 5): explicit checkbox + strong warning.</summary>
    public bool IgnoreFamily { get; init; }
}

/// <summary>A local image file with its MD5.</summary>
public sealed record LocalImage(string Name, string Path, long Bytes, string Md5);

/// <summary>The update plan: what will be done, warnings and the reasons it cannot continue.</summary>
public sealed class UpgradePlan
{
    public required UpgradeOptions Options { get; init; }
    public required string Platform { get; init; }
    public required string Method { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required long Bytes { get; init; }
    public required string Md5Local { get; init; }
    public required bool ReuseExisting { get; init; }
    public required string Md5Ref { get; init; }
    public required string SourceSw { get; init; }
    public required string VersionExpected { get; init; }
    public required long SpaceNeeded { get; init; }
    public required IReadOnlyList<FlashEntry> Cleanup { get; init; }
    public required bool SameVersion { get; init; }
    public required string TargetName { get; init; }

    /// <summary>"" (never), AFTER (once the new one booted) or BEFORE (does not fit otherwise; with checks).</summary>
    public required string DeleteInUse { get; init; }
    public required string ImagePrevious { get; init; }
    public required IReadOnlyList<string> Steps { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<string> Denials { get; init; }
    public required IReadOnlyList<string> Manual { get; init; }

    /// <summary>The plan as the window shows it (tab 4).</summary>
    public required string Text { get; init; }
}

public static class UpgradePlanner
{
    private const long MB = 1048576;

    private static string Mb(long b) => string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", b / (double)MB);

    /// <param name="local">The local image (for SERIAL / SSH), or null if no file was chosen or it does not exist.</param>
    public static UpgradePlan Build(UpgradeInfo info, UpgradeOptions opt, LocalImage? local, Translator t)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(t);
        var denials = new List<string>();
        var warnings = new List<string>();
        var steps = new List<string>();

        // File
        var name = string.Empty;
        long bytes = 0;
        var md5Local = string.Empty;
        var sourceSw = string.Empty;
        if (opt.Source == "USB")
        {
            if (info.Usb is null)
            {
                denials.Add(info.UsbDiagnostic.Length > 0 ? info.UsbDiagnostic : t.T("No USB detected on the switch. Connect it and click \"Read switch USB\"."));
            }
            else if (opt.FileUsb.Length == 0)
            {
                denials.Add(t.T("Choose the image on the USB."));
            }
            else
            {
                name = opt.FileUsb;
                var e = info.Usb.Entries.FirstOrDefault(x => Ci.Eq(x.Name, name));
                if (e is not null)
                {
                    bytes = e.Bytes;
                }
                sourceSw = info.UsbName;
            }
        }
        else if (local is null)
        {
            denials.Add(t.T("Choose the image file on your computer."));
        }
        else
        {
            name = local.Name;
            bytes = local.Bytes;
            md5Local = local.Md5;
            sourceSw = "flash:";
            if (opt.Md5Expected.Length > 0 && !Ci.Eq(opt.Md5Expected, md5Local))
            {
                denials.Add(Ci.F(t.T("The file MD5 ({0}) does NOT match Cisco's ({1}). Damaged or wrong file."), md5Local, opt.Md5Expected));
            }
        }
        // Same file already on flash (e.g. from an interrupted attempt): its MD5 is compared before sending it again
        var reuse = opt.Source == "SSH" && name.Length > 0 && bytes > 0 && md5Local.Length > 0
            && info.FlashEntries.FirstOrDefault(x => !x.IsDir && Ci.Eq(x.Name, name)) is { } ex && ex.Bytes == bytes;
        var type = string.Empty;
        if (Ci.IsMatch(name, "(?i)\\.tar$")) { type = "tar"; }
        else if (Ci.IsMatch(name, "(?i)\\.bin$")) { type = "bin"; }
        else if (name.Length > 0) { denials.Add(t.T("The image must be .bin or .tar.")); }

        // Compatibility
        if (name.Length > 0)
        {
            if (info.Family.Length > 0 && !name.ToLowerInvariant().StartsWith(info.Family.ToLowerInvariant(), StringComparison.Ordinal))
            {
                var m = Ci.F(t.T("File {0} is not from this device's family ({1})."), name, info.Family);
                if (opt.IgnoreFamily) { warnings.Add(m + t.T(" CONTINUING AT YOUR OWN RISK because you allowed another family: the switch may fail to boot.")); }
                else { denials.Add(m); }
            }
            if (info.OS == "IOS-XE" && type != "bin") { denials.Add(t.T("On IOS-XE the image is a .bin (cat9k...SPA.bin).")); }
            if (info.OS == "IOS" && type == "bin" && info.Members > 1) { denials.Add(t.T("It is a stack: use the .tar image (archive download-sw updates all members).")); }
        }
        if (opt.Source == "SERIAL")
        {
            if (info.OS == "IOS-XE") { denials.Add(t.T("Serial is not possible on IOS-XE: the 9200 ROMMON has no Xmodem and the image is ~500 MB. Use USB or SSH.")); }
            if (opt.Connection != "SERIAL") { denials.Add(t.T("To send over serial, the connection must be serial (tab 1).")); }
            if (type == "tar") { denials.Add(t.T("Over serial only the .bin can be sent (the boot loader does not install .tar).")); }
            if (info.Members > 1) { denials.Add(t.T("Over serial only one member is updated: on a stack use USB or SSH with the .tar.")); }
        }
        if (opt.Source == "SSH" && (!Validators.IsIPv4(opt.IP) || string.IsNullOrWhiteSpace(opt.User) || opt.Password.Length == 0))
        {
            denials.Add(t.T("To send over SSH I need the IP, username and password (tab 1), even if the control connection is serial."));
        }

        // Space. The running image is only deleted if the operator checked "Also include the running image":
        // after booting the new one if it fits next to it (AFTER), or before copying if it does not fit (BEFORE).
        long needed = 0;
        var available = info.FlashFree;
        IReadOnlyList<FlashEntry> cleanup = [];
        IReadOnlyList<string> manual = [];
        var sameVersion = false;
        var targetName = string.Empty;
        var deleteInUse = string.Empty;
        var lim = info.Cleanable.Where(c => !(reuse && !c.IsDir && Ci.Eq(c.Name, name))).ToList();
        var bytesLim = ImageNames.Sum(lim);
        var txtLim = string.Join(", ", lim.Select(c => Ci.F("{0} ({1})", c.Name, Mb(c.Bytes))));
        if (info.OS == "IOS-XE" && (opt.CleanFlash || opt.CleanAll))
        {
            warnings.Add(t.T("The classic IOS cleanup checkboxes do not apply to IOS-XE: install remove inactive is used there."));
        }
        if (name.Length > 0 && info.OS == "IOS")
        {
            var verNew = ImageNames.VersionFromFile(name);
            targetName = type == "tar" ? ImageNames.TarFolder(name) : name;
            sameVersion = (verNew.Length > 0 && Ci.Eq(verNew, info.Version)) || Ci.Eq(targetName, info.ImageTop);
            if (sameVersion)
            {
                warnings.Add(Ci.F(t.T("The switch already runs {0}: it is reinstalled (overwriting what is installed) only if you confirm it when clicking Update."), info.Version));
            }
            if (Ci.Eq(targetName, info.ImageTop))
            {
                if (opt.CleanAll) { deleteInUse = "BEFORE"; }
                else { denials.Add(Ci.F(t.T("The new image would be installed with the same name as the running one (flash:/{0}). To overwrite it check \"Also include the running image\"."), info.ImageTop)); }
            }
            // Leftovers of a previous attempt with the same name: copy would ask to overwrite
            if (lim.Any(c => Ci.Eq(c.Name, targetName) || (opt.Source == "USB" && Ci.Eq(c.Name, name))) && !opt.CleanFlash)
            {
                denials.Add(Ci.F(t.T("flash:/{0} already exists (from a previous attempt). Check \"delete the other images from flash\" to delete it before copying."), targetName));
            }
        }
        if (bytes > 0)
        {
            if (info.OS == "IOS-XE")
            {
                // install add expands the .bin into packages (~1x) plus temporary files; if the .bin is uploaded to flash, it takes 1x more
                needed = (long)(bytes * 1.2);
                if (sourceSw == "flash:" && !reuse) { needed += bytes; }
                if (opt.CleanInactive) { warnings.Add(t.T("Space can grow by cleaning inactive packages (install remove inactive).")); }
                if (needed > available && !opt.CleanInactive)
                {
                    denials.Add(Ci.F(t.T("No space in flash: ~{0} needed and {1} available. Check \"install remove inactive\"."), Mb(needed), Mb(available)));
                }
            }
            else
            {
                if (type == "tar")
                {
                    // Extracted next to the running image; if the .tar is uploaded to flash, it takes 1x more
                    needed = (long)(bytes * 1.05);
                    if (sourceSw == "flash:" && !reuse) { needed += bytes; }
                }
                else
                {
                    needed = reuse ? MB : bytes + MB;
                }
                if (opt.CleanFlash && lim.Count > 0)
                {
                    cleanup = lim;
                    available += bytesLim;
                }
                var withInUse = available + info.ImageBytes;
                if (deleteInUse == "BEFORE") { available = withInUse; }
                var tipUsb = sourceSw == "flash:" ? t.T(" or use the USB (the file does not take flash space)") : string.Empty;
                if (needed > available)
                {
                    if (!opt.CleanFlash && lim.Count > 0 && available + bytesLim >= needed)
                    {
                        denials.Add(Ci.F(t.T("No space in flash: ~{0} needed and {1} free. Check \"delete the other images from flash\" to delete {2}. The running image ({3}) is not touched."), Mb(needed), Mb(available), txtLim, info.ImageTop));
                    }
                    else if (opt.CleanAll && withInUse >= needed)
                    {
                        deleteInUse = "BEFORE";
                        available = withInUse;
                    }
                    else if (info.FlashFree + bytesLim + info.ImageBytes >= needed)
                    {
                        denials.Add(Ci.F(t.T("It does not fit next to the running image: ~{0} needed and, without the other images, there would be {1}. It would fit by also deleting the running one ({2}): check \"Also include the running image\" (deleted before copying, with checks){3}."),
                            Mb(needed), Mb(info.FlashFree + bytesLim), info.ImageTop, tipUsb));
                        manual = ImageNames.ManualSpaceProcedure(info, name, sourceSw, t);
                    }
                    else
                    {
                        denials.Add(Ci.F(t.T("It does not fit even deleting all images: ~{0} needed and flash could only free {1}.{2}"), Mb(needed), Mb(info.FlashFree + bytesLim + info.ImageBytes), tipUsb.Length > 0 ? t.T(" Use the USB.") : string.Empty));
                        manual = ImageNames.ManualSpaceProcedure(info, name, sourceSw, t);
                    }
                }
                else if (opt.CleanAll && deleteInUse.Length == 0)
                {
                    if (opt.Reload || opt.Source == "SERIAL") { deleteInUse = "AFTER"; }
                    else { warnings.Add(t.T("The running image is not deleted because the switch is not reloaded: without booting the new one it cannot be checked that it works.")); }
                }
                else if (!opt.CleanFlash && lim.Count > 0)
                {
                    warnings.Add(Ci.F(t.T("There are {0} old image(s) in flash ({1}); they are not deleted because there is space."), lim.Count, Mb(bytesLim)));
                }
            }
        }
        if (deleteInUse == "BEFORE")
        {
            if (opt.Md5Expected.Length == 0) { denials.Add(t.T("To delete the running image before copying I need the MD5 published by Cisco for the new image (tab 3): it guarantees that what will be installed is complete before you are left without an image.")); }
            if (info.Members > 1) { denials.Add(t.T("On a stack the running image is not deleted before copying.")); }
            if (opt.Connection == "SSH") { warnings.Add(t.T("If something fails after deleting the running image, recovery is over the console: keep the console cable connected and at hand.")); }
            warnings.Add(t.T("From the moment the running image is deleted until the installation finishes, the switch has no image in flash: it keeps running from RAM, but do NOT power it off or reload it."));
        }

        // Steps
        var verExpected = ImageNames.VersionFromFile(name);
        var md5Ref = opt.Md5Expected.Length > 0 ? opt.Md5Expected : md5Local;
        if (opt.SaveBefore) { steps.Add(t.T("Save the current configuration (write memory).")); }
        if (cleanup.Count > 0)
        {
            steps.Add(Ci.F(t.T("Clean flash keeping only the running image ({0}): delete {1}. If BOOT points to something that is deleted, it is first pinned to the running image."), info.ImageTop, txtLim));
        }
        // The new image is the running file itself, already on flash: its MD5 decides (equal = keep it, nothing is sent)
        var sameAsRunning = reuse && Ci.Eq(name, info.ImageTop);
        void StepBefore()
        {
            if (deleteInUse != "BEFORE")
            {
                return;
            }
            if (sameAsRunning)
            {
                steps.Add(Ci.F(t.T("flash:{0} is the running image and the same file as the new one: its MD5 is compared first. If it matches it is neither deleted nor sent; if not, it is deleted (after checking the new image against Cisco's MD5) and sent again."), name));
                return;
            }
            if (opt.Source == "USB")
            {
                steps.Add(Ci.F(t.T("Verify the MD5 of {0}{1} against Cisco's BEFORE touching the running image."), sourceSw, name));
            }
            steps.Add(Ci.F(t.T("DELETE THE RUNNING IMAGE (flash:/{0}). From here until the installation finishes the switch has no image in flash: do not power it off."), info.ImageTop));
        }
        if (opt.Source == "SERIAL")
        {
            var seg = TimeSpan.FromSeconds(bytes * 10.0 / Math.Max(1, opt.VelXmodem) * 1.12);
            StepBefore();
            steps.Add(t.T("Enable manual boot (boot manual), save and reload to the \"switch:\" boot loader."));
            steps.Add(Ci.F(t.T("Raise the console to {0} baud (set BAUD)."), opt.VelXmodem));
            steps.Add(string.Format(CultureInfo.CurrentCulture, t.T("Send {0} via Xmodem to flash: (estimated {1:hh\\:mm})."), name, seg));
            steps.Add(Ci.F(t.T("Return the console to {0} baud, set BOOT flash:{1}, set MANUAL_BOOT no and boot the new image."), opt.BaudRate, name));
            steps.Add(t.T("If the new IOS updates the boot loader, the switch reloads once more by itself: this is expected."));
            steps.Add(Ci.F(t.T("Remove boot manual, boot system flash:{0} and write memory."), name));
            steps.Add(t.T("Compare verify /md5 on the switch against the file MD5."));
            warnings.Add(string.Format(CultureInfo.CurrentCulture, t.T("Serial is very slow: ~{0:hh\\:mm} at {1} baud. Do not disconnect the cable."), seg, opt.VelXmodem));
        }
        else
        {
            if (opt.Source == "SSH") { steps.Add(t.T("Check AAA and enable \"ip scp server enable\" if needed (removed when finished).")); }
            if (info.OS == "IOS-XE" && opt.CleanInactive) { steps.Add(t.T("Free space: install remove inactive.")); }
            StepBefore();
            if (opt.Source == "SSH" && !(sameAsRunning && deleteInUse == "BEFORE"))
            {
                steps.Add(reuse
                    ? Ci.F(t.T("flash:{0} is already on the switch with the same size: its MD5 is compared with the local file and it is only sent again if it differs."), name)
                    : Ci.F(t.T("Send {0} via SCP to flash:."), name));
            }
            steps.Add(Ci.F(t.T("Compare verify /md5 {0}{1} against {2}."), sourceSw, name,
                opt.Md5Expected.Length > 0 ? t.T("the Cisco MD5") : md5Local.Length > 0 ? t.T("the local file MD5") : t.T("nothing (you did not enter the Cisco MD5: it is only recorded)")));
            if (info.OS == "IOS-XE")
            {
                steps.Add(Ci.F(t.T("install add file {0}{1} activate commit (the switch reloads by itself; if it does not and you checked reload, reload is sent)."), sourceSw, name));
            }
            else if (type == "tar")
            {
                if (sameVersion)
                {
                    steps.Add(Ci.F(t.T("archive tar /xtract {0}{1} flash: (same version: archive download-sw would reject it)."), sourceSw, name));
                    steps.Add(Ci.F(t.T("boot system flash:/{0}/{0}.bin and write memory."), targetName));
                }
                else
                {
                    steps.Add(Ci.F(t.T("archive download-sw /leave-old-sw {0}{1} (does not delete the running image; if it does not fit it stops without touching anything)."), sourceSw, name));
                }
                if (sourceSw == "flash:") { steps.Add(t.T("Delete the .tar from flash: when finished.")); }
            }
            else
            {
                if (sourceSw != "flash:") { steps.Add(Ci.F(t.T("copy {0}{1} flash:{1} and verify the MD5 again."), sourceSw, name)); }
                steps.Add(Ci.F(t.T("boot system flash:{0} and write memory.{1}"), name, deleteInUse.Length == 0 ? t.T(" The previous image stays in flash as a backup.") : string.Empty));
            }
            if (info.OS != "IOS-XE")
            {
                if (opt.Reload) { steps.Add(t.T("Reload and wait for boot.")); }
                else { warnings.Add(t.T("No reload: the new version takes effect at the next reload.")); }
            }
        }
        if (info.OS == "IOS-XE" || opt.Reload || opt.Source == "SERIAL")
        {
            steps.Add(Ci.F(t.T("Reconnect and confirm the version{0}."), verExpected.Length > 0 ? " (" + verExpected + ")" : string.Empty));
        }
        if (deleteInUse == "AFTER")
        {
            steps.Add(Ci.F(t.T("Only if it booted and the new version was confirmed: delete the previous image (flash:/{0}). If anything does not add up, it is kept."), info.ImageTop));
        }
        if (opt.Md5Expected.Length == 0) { warnings.Add(t.T("You did not enter the Cisco MD5: it cannot be confirmed that the file is the original.")); }
        if (info.BootManual && opt.Source != "SERIAL") { warnings.Add(t.T("The switch has \"Manual Boot = yes\": after reloading it will stay at the boot loader. Remove it first (no boot manual).")); }

        var sb = new StringBuilder();
        void L(string x) => sb.Append(x).Append('\n');
        L(Ci.F(t.T("DEVICE   : {0}  {1}  serial {2}  ({3} {4})"), info.Hostname, info.Model, info.Serial, info.OS, info.Version));
        L(Ci.F(t.T("IMAGE    : {0}  {1}  -> version {2}"), name, bytes != 0 ? Mb(bytes) : string.Empty, verExpected.Length > 0 ? verExpected : t.T("(could not be read from the name)")));
        var load = opt.Source switch { "USB" => t.T("USB on the switch"), "SERIAL" => t.T("Xmodem over the console"), "SSH" => t.T("SCP over SSH"), _ => string.Empty };
        L(Ci.F(t.T("LOAD     : {0}   CONNECTION: {1}"), load, opt.Connection == "SSH" ? "SSH " + opt.IP : opt.PortCom));
        if (md5Local.Length > 0)
        {
            L(Ci.F(t.T("MD5      : file {0}{1}"), md5Local, opt.Md5Expected.Length > 0 ? (Ci.Eq(opt.Md5Expected, md5Local) ? t.T("  = Cisco (OK)") : t.T("  <> Cisco")) : string.Empty));
        }
        L(string.Empty);
        if (denials.Count > 0)
        {
            L(t.T("CANNOT CONTINUE:"));
            denials.ForEach(b => L("  X " + b));
            L(string.Empty);
        }
        L(t.T("STEPS:"));
        for (var i = 0; i < steps.Count; i++)
        {
            L(Ci.F("  {0,2}. {1}", i + 1, steps[i]));
        }
        if (warnings.Count > 0)
        {
            L(string.Empty);
            L(t.T("WARNINGS:"));
            warnings.ForEach(a => L("  ! " + a));
        }
        if (manual.Count > 0)
        {
            L(string.Empty);
            foreach (var m in manual) { L(m); }
        }
        return new UpgradePlan
        {
            Options = opt, Platform = info.OS, Method = opt.Source, Type = type, Name = name, Bytes = bytes, Md5Local = md5Local, ReuseExisting = reuse, Md5Ref = md5Ref,
            SourceSw = sourceSw, VersionExpected = verExpected, SpaceNeeded = needed, Cleanup = cleanup, SameVersion = sameVersion, TargetName = targetName,
            DeleteInUse = deleteInUse, ImagePrevious = info.ImageTop, Steps = steps, Warnings = warnings, Denials = denials, Manual = manual, Text = sb.ToString(),
        };
    }
}
