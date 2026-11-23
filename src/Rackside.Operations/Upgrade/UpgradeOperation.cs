using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Core.Security;
using Rackside.Core.Upgrade;
using Rackside.Records;
using Rackside.Transport.Console;
using Rackside.Transport.Ssh;

namespace Rackside.Operations.Upgrade;

/// <summary>State of one "Update IOS" between the query and the update.</summary>
public sealed class UpgradeSession
{
    public required string Mark { get; init; }
    public required string Folder { get; set; }
    public UpgradeInfo? Info { get; set; }

    /// <summary>The fingerprint accepted in this operation: reconnections and SCP are pinned to it.</summary>
    public SshTrust? Trust { get; set; }

    public string InstallSummary { get; set; } = string.Empty;
    public string InfoText { get; set; } = string.Empty;
    public bool Done { get; set; }
}

public enum UpgradeOutcome
{
    Failed,
    Cancelled,
    SuccessfulWithNotes,
    Successful,
}

public static class UpgradeOutcomeExtensions
{
    public static string Code(this UpgradeOutcome o) => o switch
    {
        UpgradeOutcome.Failed => "FAILED",
        UpgradeOutcome.Cancelled => "CANCELLED",
        UpgradeOutcome.SuccessfulWithNotes => "SUCCESSFUL WITH NOTES",
        UpgradeOutcome.Successful => "SUCCESSFUL",
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };
}

/// <summary>What the update record needs.</summary>
public sealed record UpgradeResult(UpgradeSession Session, UpgradePlan Plan, UpgradeOutcome Outcome, string ErrorFatal, string Md5Switch, string VersionAfter,
    IReadOnlyList<PushCheck> Actions, IReadOnlyList<PushCheck> Verification, DateTime Start, DateTime End, string RecordPath);

/// <summary>"Update IOS".</summary>
public sealed class UpgradeOperation(string fingerprints, Translator t, SecretMasker masker, IOperatorUi ui, Func<string, int, IConsoleChannel>? openPort = null, CancellationToken cancel = default)
{
    /// <summary>Stop button of the current run (the window sets a new one before each run).</summary>
    public CancellationToken Cancellation { get; set; } = cancel;

    private const long MB = 1048576;
    private readonly Func<string, int, IConsoleChannel> open = openPort ?? ((port, baud) => SerialChannel.Open(port, baud, t));
    private readonly Dictionary<string, LocalImage> md5Cache = new(StringComparer.Ordinal);

    public OperationLog Log { get; } = new(t, masker);

    /// <summary>verify /md5 without any output for this long = stuck session.</summary>
    public int VerifyStallSeconds { get; set; } = 300;

    /// <summary>Waits between attempts and around the SSH boot (shorter in the tests).</summary>
    public int RetrySeconds { get; set; } = 20;

    private SwitchConnection? conn;

    private ConsoleSession S => conn?.Session ?? throw new InvalidOperationException("Not connected.");

    private static string Safe(string s) => Ci.Replace(s, "[\\\\/:*?\"<>|]", "_");

    private static string Mb(long b) => string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", b / (double)MB);

    private void Register(UpgradeOptions opt) => masker.Register(opt.Password, opt.Enable);

    /// <summary>Checks the connection data before connecting.</summary>
    public void Validate(UpgradeOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);
        if (string.IsNullOrWhiteSpace(opt.FolderRecord)) { throw new FormatException(t.T("Enter the evidence folder (tab 1).")); }
        if (opt.Connection == "SERIAL")
        {
            if (string.IsNullOrWhiteSpace(opt.PortCom)) { throw new FormatException(t.T("Enter the COM port (tab 1).")); }
        }
        else
        {
            if (!Validators.IsIPv4(opt.IP)) { throw new FormatException(t.T("Enter a valid IP for SSH (tab 1).")); }
            if (string.IsNullOrWhiteSpace(opt.User) || opt.Password.Length == 0) { throw new FormatException(t.T("Enter the username and password to log in to the switch (tab 1).")); }
        }
    }

    private void Sleep(int seconds)
    {
        if (Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)))
        {
            throw new OperationCanceledException(t.T("Operation stopped by the operator."), Cancellation);
        }
    }

    private void Close()
    {
        conn?.Dispose();
        conn = null;
    }

    private FingerprintRequest Request(UpgradeSession ses, UpgradeOptions opt) => new(opt.IP, opt.PortSsh)
    {
        Expected = opt.FingerprintExpected.Length > 0 ? opt.FingerprintExpected : null,
        Accepted = ses.Trust,
        Hostname = ses.Info?.Hostname ?? string.Empty,
        Serial = ses.Info?.Serial ?? string.Empty,
    };

    /// <summary>Opens the connection (serial or SSH), logs in and leaves the session at "#".</summary>
    private void Connect(UpgradeSession ses, UpgradeOptions opt, int attempts = 1)
    {
        var cred = new Credentials(opt.User, opt.Password, opt.Enable);
        for (var n = 1; n <= attempts; n++)
        {
            try
            {
                if (opt.Connection == "SSH")
                {
                    conn = SwitchConnection.OverSsh(Request(ses, opt), cred, ses.Folder, new FingerprintStore(fingerprints), t, masker, ui, Log, Cancellation);
                    ses.Trust = conn.Trust;
                }
                else
                {
                    conn = SwitchConnection.Over(open(opt.PortCom, opt.BaudRate), Path.Combine(ses.Folder, "console_session.txt"), cred, t, masker, Cancellation);
                }
                Log.Ok(Ci.F(t.T("Connected via {0}: {1}#"), opt.Connection == "SSH" ? t.T("SSH ") + opt.IP : opt.PortCom, conn.Hostname));
                return;
            }
            catch (Exception ex) when (ex is SwitchException or IOException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException
                && ex is not FingerprintRejectedException)
            {
                Close();
                if (n >= attempts || Cancellation.IsCancellationRequested)
                {
                    throw;
                }
                Log.Warn(Ci.F(t.T("Connection attempt {0} failed: {1}. Retrying..."), n, ex.Message));
                Sleep(RetrySeconds);
            }
        }
    }

    private FlashListing? Dir(string path) => FlashListing.Parse(LastDir = S.RunCommand("dir " + path, 120));

    private string LastDir { get; set; } = string.Empty;

    private long DirSize(string path)
    {
        var l = Dir(path);
        if (l is null)
        {
            return 0;
        }
        long total = 0;
        foreach (var e in l.Entries)
        {
            if (e.IsDir)
            {
                var sub = Dir(path.TrimEnd('/') + "/" + e.Name);
                total += sub is null ? 0 : ImageNames.Sum(sub.Entries);
            }
            else
            {
                total += e.Bytes;
            }
        }
        return total;
    }

    /// <summary>Looks for the switch USB drive; if there is no readable one, explains why.</summary>
    private (FlashListing? Usb, string Name, string Problem, string Diagnostic) FindUsb()
    {
        var names = new List<string>();
        var fsText = S.RunCommand("show file systems", 60);
        if (!Ci.IsMatch(fsText, "(?i)Invalid input"))
        {
            foreach (Match m in Regex.Matches(fsText, "(?i)(?<![\\w-])(usb[a-z]*\\d+(?:-\\d+)?:)"))
            {
                var n = m.Groups[1].Value.ToLowerInvariant();
                if (!Ci.IsMatch(n, "token") && !names.Contains(n))
                {
                    names.Add(n);
                }
            }
        }
        foreach (var n in new[] { "usbflash0:", "usbflash1:" })
        {
            if (!names.Contains(n))
            {
                names.Add(n);
            }
        }
        var problem = string.Empty;
        foreach (var dev in names)
        {
            var l = Dir(dev);
            if (l is not null)
            {
                return (l, dev, string.Empty, string.Empty);
            }
            if (problem.Length == 0 && Ci.IsMatch(LastDir, "(?i)Invalid DOS media|no media|needs formatting|unexpected values"))
            {
                problem = dev;
            }
        }
        var (text, logged, events) = ImageNames.UsbDiagnostic(S.RunCommand("show logging | include USB", 60), S.RunCommand("show logging | include Buffer logging", 60), problem, t);
        if (events.Count > 0)
        {
            Log.Info(Ci.F(t.T("USB events in the switch log: {0}"), string.Join(" | ", events.TakeLast(4))));
        }
        Log.Warn(Ci.F(t.T("USB: {0}"), logged));
        return (null, string.Empty, problem, text);
    }

    /// <summary>"Connect and query": version, boot, flash, USB. Changes nothing.</summary>
    public UpgradeSession Query(UpgradeOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);
        Register(opt);
        Validate(opt);
        var mark = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var ses = new UpgradeSession { Mark = mark, Folder = Path.Combine(opt.FolderRecord, "UPDATE_" + mark) };
        Directory.CreateDirectory(ses.Folder);
        Log.Paths.Clear();
        Log.Paths.Add(Path.Combine(opt.FolderRecord, "Upgrades.log"));
        Log.Paths.Add(Path.Combine(ses.Folder, "log.txt"));
        Log.Info(t.T("===== Version query ====="));
        ui.Status(t.T("Connecting..."), 10);
        UpgradeInfo info;
        try
        {
            Connect(ses, opt);
            ui.Status(t.T("Reading version, boot and storage..."), 40);
            var txtVer = S.RunCommand("show version", 60);
            if (!Ci.IsMatch(txtVer, "(?i)cisco"))
            {
                throw new SwitchException(t.T("The connected device does not look like a Cisco switch."));
            }
            info = new UpgradeInfo { Hostname = conn!.Hostname };
            var ver = Core.Restore.TargetInfo.Parse(txtVer, string.Empty, string.Empty, string.Empty, t.T);
            info.OS = ver.OS;
            info.Model = ver.Model;
            info.Serial = ver.Serial;
            info.Version = ver.Version;
            info.ImageSystem = Ci.TryMatch(txtVer, "(?im)System image file is\\s+\"?([^\"\\r\\n]+)\"?", out var m) ? m.Groups[1].Value.Trim() : string.Empty;
            info.Uptime = Ci.TryMatch(txtVer, "(?im)^\\S+ uptime is (.+)$", out m) ? m.Groups[1].Value.Trim() : string.Empty;
            if (info.OS == "IOS-XE")
            {
                info.ModeXE = Ci.IsMatch(info.ImageSystem, "(?i)packages\\.conf") || Ci.IsMatch(txtVer, "(?i)\\bINSTALL\\b") ? "INSTALL" : "BUNDLE";
            }
            info.Members = Math.Max(1, Ci.Split(S.RunCommand("show switch", 30), "\\r?\\n").Count(l => Ci.IsMatch(l, "^\\s*\\*?\\s*\\d+\\s+(Active|Master|Member|Standby)\\b")));
            var txtBoot = S.RunCommand("show boot", 30);
            info.Boot = Ci.TryMatch(txtBoot, "(?im)^\\s*BOOT (?:path-list|variable)\\s*[:=]\\s*(\\S+)", out m) ? Ci.Split(m.Groups[1].Value, "[;,]")[0] : string.Empty;
            info.BootManual = Ci.IsMatch(txtBoot, "(?im)^\\s*Manual Boot\\s*[:=]\\s*yes");
            var flash = Dir("flash:");
            var bu = FindUsb();
            // Size of the current image (file or folder) for the space calculation
            var pathImg = Ci.Replace(info.ImageSystem, "^[A-Za-z0-9\\-]+:/*", string.Empty);
            if (pathImg.Length > 0)
            {
                info.ImageTop = pathImg.Split('/')[0];
                var e = flash?.Entries.FirstOrDefault(x => Ci.Eq(x.Name, info.ImageTop));
                if (e is not null)
                {
                    if (e.IsDir)
                    {
                        var sub = Dir("flash:/" + info.ImageTop);
                        info.ImageBytes = sub is null ? 0 : ImageNames.Sum(sub.Entries);
                    }
                    else
                    {
                        info.ImageBytes = e.Bytes;
                    }
                }
            }
            if (info.OS == "IOS-XE")
            {
                ses.InstallSummary = S.RunCommand("show install summary", 60);
            }
            if (info.OS == "IOS" && flash is not null)
            {
                var cleanable = ImageNames.CleanupCandidates(flash.Entries, info.ImageTop);
                foreach (var c in cleanable.Where(c => c.IsDir))
                {
                    c.Bytes = DirSize("flash:/" + c.Name);
                }
                info.Cleanable = cleanable;
            }
            info.BootTop = info.Boot.Length > 0 ? Ci.Replace(info.Boot, "^[A-Za-z0-9\\-]+:/*", string.Empty).Split('/')[0] : string.Empty;
            info.FlashTotal = flash?.Total ?? 0;
            info.FlashFree = flash?.Free ?? 0;
            info.FlashEntries = flash?.Entries ?? [];
            (info.Usb, info.UsbName, info.UsbProblem, info.UsbDiagnostic) = bu;
            info.Family = ImageNames.Family(info);
        }
        finally
        {
            Close();
        }
        ses.Info = info;
        if (ses.Trust is { } hs)
        {
            // Complete the fingerprint record with the device that was identified
            new FingerprintStore(fingerprints).Save(hs.Device, hs.Algorithm, hs.Fingerprint, info.Hostname, info.Serial, hs.How);
        }
        var renamed = Path.Combine(opt.FolderRecord, Safe(Ci.F("UPDATE_{0}_{1}", info.Hostname, mark)));
        Directory.Move(ses.Folder, renamed);
        ses.Folder = renamed;
        Log.Paths[1] = Path.Combine(ses.Folder, "log.txt");
        ses.InfoText = VersionInfo(info, ses.InstallSummary);
        Log.Ok(Ci.F(t.T("{0}: {1} {2}, model {3}, serial {4}"), info.Hostname, info.OS, info.Version, info.Model, info.Serial));
        ui.Status(Ci.F(t.T("{0} has {1} {2}. Check tab 2 to look for the image."), info.Hostname, info.OS, info.Version), 100);
        return ses;
    }

    /// <summary>"Read switch USB".</summary>
    public void ReadUsb(UpgradeSession ses, UpgradeOptions opt)
    {
        ArgumentNullException.ThrowIfNull(ses);
        ArgumentNullException.ThrowIfNull(opt);
        Register(opt);
        Validate(opt);
        var info = ses.Info ?? throw new FormatException(t.T("First click \"Connect and query\"."));
        ui.Status(t.T("Reading the switch USB..."), 20);
        try
        {
            Connect(ses, opt);
            (info.Usb, info.UsbName, info.UsbProblem, info.UsbDiagnostic) = FindUsb();
        }
        finally
        {
            Close();
        }
    }

    /// <summary>The local file with its MD5, cached by path, size and date.</summary>
    public LocalImage? LocalFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }
        var fi = new FileInfo(path);
        var key = Ci.F("{0}|{1}|{2}", fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks);
        if (md5Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        ui.Status(Ci.F(t.T("Computing MD5 of {0}..."), fi.Name));
        using var fs = fi.OpenRead();
        var r = new LocalImage(fi.Name, fi.FullName, fi.Length, Convert.ToHexStringLower(MD5.HashData(fs)));
        md5Cache[key] = r;
        return r;
    }

    /// <summary>The plan with the current options.</summary>
    public UpgradePlan Plan(UpgradeSession ses, UpgradeOptions opt)
    {
        ArgumentNullException.ThrowIfNull(ses);
        ArgumentNullException.ThrowIfNull(opt);
        Register(opt);
        Validate(opt);
        var info = ses.Info ?? throw new FormatException(t.T("First click \"Connect and query\"."));
        return UpgradePlanner.Build(info, opt, opt.Source == "USB" ? null : LocalFile(opt.FileLocal), t);
    }

    private string SwitchMd5(string path)
    {
        // verify prints dots all the time: a long silence means the session is stuck, not that it is still working
        var txt = S.RunLong("verify /md5 " + path, 1800, stallSeconds: VerifyStallSeconds);
        return Ci.TryMatch(txt, "=\\s*([0-9a-fA-F]{32})", out var m)
            ? m.Groups[1].Value.ToLowerInvariant()
            : throw new SwitchException(Ci.F(t.T("Could not get the MD5 of {0}: {1}"), path, Ci.Replace(txt, "[\\r\\n]+", " | ").Trim()));
    }

    /// <summary>Waits for IOS to finish booting on the console.</summary>
    private void WaitSerialBoot(int minutes, int retriesBoot = 0)
    {
        var accum = new StringBuilder();
        (string, string)[] pat =
        [
            ("BootLoader", "(?:^|\\n)\\s*(switch:|rommon\\s*\\d*\\s*>)\\s*$"),
            ("Dialog", "(?i)initial configuration dialog\\?\\s*\\[yes/no\\]:\\s*$"),
            ("Autoinst", "(?i)terminate autoinstall\\?\\s*\\[yes\\]:\\s*$"),
            ("Return", "(?i)Press RETURN to get started"),
            ("Login", "(?i)(Username|User Name|login|Password):\\s*$"),
            ("Prompt", "(?:^|\\n)\\s*[A-Za-z0-9][\\w\\.\\-]*[>#]\\s*$"),
        ];
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalMinutes < minutes)
        {
            var left = (int)Math.Max(10, (minutes * 60) - clock.Elapsed.TotalSeconds);
            var r = S.Wait(pat, left, silenceMs: 3000, pushSeconds: 30)!;
            accum.Append(r.Text);
            var updatedBL = Ci.IsMatch(accum.ToString(), "(?i)(Upgrading Boot Loader|Boot Upgrade image auto-rebooting)");
            switch (r.Key)
            {
                case "BootLoader":
                    if (retriesBoot > 0 && Ci.IsMatch(r.Match.Value, "switch:"))
                    {
                        retriesBoot--;
                        Log.Warn(updatedBL ? t.T("The new IOS updated the boot loader and reloaded; it stopped at \"switch:\". Sending \"boot\".") : t.T("The switch stopped at the boot loader; sending \"boot\"."));
                        S.SendLine("boot");
                    }
                    else if (updatedBL)
                    {
                        throw new SwitchException(t.T("The new IOS updated the boot loader and reloaded, and the switch was left at \"switch:\" (manual boot enabled). The image DID boot: type \"boot\" on the console and then \"no boot manual\" + write memory."));
                    }
                    else
                    {
                        throw new SwitchException(t.T("The switch was left at the boot loader / ROMMON: the image did not boot. Check it on the console (set BOOT / boot with the previous image)."));
                    }
                    break;
                case "Dialog":
                    Log.Warn(t.T("The switch booted WITHOUT configuration (initial dialog)."));
                    S.SendLine("no");
                    break;
                case "Autoinst" or "Return":
                    S.SendLine(string.Empty);
                    break;
                default:
                    return;
            }
        }
        throw new SwitchException(Ci.F(t.T("The switch did not finish booting in {0} minutes."), minutes));
    }

    /// <summary>Waits for the switch to go down and answer again on the SSH port.</summary>
    private void WaitSshBoot(string ip, int port, int minutes)
    {
        var clock = Stopwatch.StartNew();
        var sawDrop = false;
        var consecutive = 0;
        Sleep(RetrySeconds);
        while (clock.Elapsed.TotalMinutes < minutes)
        {
            var opened = SwitchConnection.TcpOpen(ip, port, 2000);
            if (!opened)
            {
                sawDrop = true;
                consecutive = 0;
            }
            else if (sawDrop || clock.Elapsed.TotalMinutes > 6)
            {
                consecutive++;
                if (consecutive >= 2)
                {
                    Sleep(RetrySeconds);
                    return;
                }
            }
            ui.Status(string.Format(CultureInfo.CurrentCulture, t.T("Waiting for boot ({0:mm\\:ss}). SSH {1}."), clock.Elapsed, opened ? t.T("responds") : t.T("no response")));
            Sleep(Math.Max(1, RetrySeconds / 2));
        }
        throw new SwitchException(Ci.F(t.T("The switch did not answer over SSH again in {0} minutes."), minutes));
    }

    private static bool Bad(string output, out string line)
    {
        var ok = Ci.TryMatch(output, "(?im)^\\s*%\\s*(Error|Invalid|Cannot|Can not).*$", out var m);
        line = ok ? m.Value.Trim() : string.Empty;
        return ok;
    }

    private string DeleteCommand(FlashEntry e) => e.IsDir ? "delete /force /recursive flash:/" + e.Name : "delete /force flash:/" + e.Name;

    /// <summary>Leaves only the running image in flash. First makes sure BOOT points to it.</summary>
    private void FlashCleanup(UpgradeInfo info, UpgradePlan plan, Action<string, string, string> step, string keep = "")
    {
        var flash = Dir("flash:");
        if (flash is null || !flash.Entries.Any(e => Ci.Eq(e.Name, info.ImageTop)))
        {
            throw new SwitchException(Ci.F(t.T("The running image ({0}) was not found in flash:. Nothing is deleted."), info.ImageTop));
        }
        // Never the running image nor "keep" (the new image, already verified on flash by its MD5)
        var candidates = ImageNames.CleanupCandidates(flash.Entries, info.ImageTop).Where(c => keep.Length == 0 || !Ci.Eq(c.Name, keep)).ToList();
        if (candidates.Count == 0)
        {
            step(t.T("Flash cleanup"), "OK", t.T("There were no other images"));
            return;
        }
        if (info.BootTop.Length == 0 || candidates.Any(c => Ci.Eq(c.Name, info.BootTop)))
        {
            S.SendConfigLine("configure terminal");
            var rb = S.SendConfigLine("boot system " + info.ImageSystem);
            S.SendConfigLine("end");
            if (rb.Errors.Count > 0)
            {
                throw new SwitchException(Ci.F(t.T("Could not secure boot on the running image: {0}. Nothing is deleted."), string.Join(" / ", rb.Errors)));
            }
            if (!Ci.IsMatch(S.RunLong("write memory", 180), "\\[OK\\]"))
            {
                throw new SwitchException(t.T("write memory did not confirm [OK] while securing boot. Nothing is deleted."));
            }
            step(t.T("Boot secured on the running image"), "OK", Ci.F(t.T("boot system {0} + write memory (BOOT pointed to \"{1}\")"), info.ImageSystem, info.BootTop));
        }
        foreach (var c in candidates)
        {
            ui.Status(Ci.F(t.T("Cleaning flash: {0}"), c.Name));
            if (Bad(S.RunLong(DeleteCommand(c), 900), out var err))
            {
                throw new SwitchException(Ci.F(t.T("Could not delete flash:/{0}: {1}"), c.Name, err));
            }
            step(Ci.F(t.T("Delete {0}flash:/{1}"), c.IsDir ? t.T("folder ") : string.Empty, c.Name), "OK", t.T("It is not the running image"));
        }
        var after = Dir("flash:");
        if (after is null || !after.Entries.Any(e => Ci.Eq(e.Name, info.ImageTop)))
        {
            throw new SwitchException(Ci.F(t.T("The running image ({0}) no longer appears in flash: after cleaning. Do NOT reload the switch: check it on the console."), info.ImageTop));
        }
        info.FlashFree = after.Free;
        if (after.Free < plan.SpaceNeeded)
        {
            throw new SwitchException(string.Format(CultureInfo.CurrentCulture, t.T("After cleaning there are {0:N1} MB free and ~{1:N1} MB are needed. Stopped before copying; the running image is untouched."), after.Free / (double)MB, plan.SpaceNeeded / (double)MB));
        }
        step(t.T("Flash space after cleanup"), "OK", string.Format(CultureInfo.CurrentCulture, t.T("{0:N1} MB free"), after.Free / (double)MB));
    }

    /// <summary>The text of tab 2.</summary>
    public string VersionInfo(UpgradeInfo info, string installSummary)
    {
        ArgumentNullException.ThrowIfNull(info);
        var sb = new StringBuilder();
        void L(string x) => sb.Append(x).Append('\n');
        L(t.T("CURRENT VERSION"));
        L(Ci.F(t.T("  Hostname        : {0}"), info.Hostname));
        L(Ci.F(t.T("  Model           : {0}"), info.Model));
        L(Ci.F(t.T("  Serial          : {0}"), info.Serial));
        L(Ci.F(t.T("  System          : {0} {1}"), info.OS, info.Version));
        L(Ci.F(t.T("  Running image   : {0}"), info.ImageSystem));
        if (info.OS == "IOS-XE")
        {
            L(Ci.F(t.T("  Mode            : {0}{1}"), info.ModeXE, info.ModeXE == "BUNDLE" ? t.T("  (install add converts it to INSTALL)") : string.Empty));
        }
        L(Ci.F(t.T("  Boot (BOOT)     : {0}{1}"), info.Boot, info.BootManual ? t.T("   WARNING: Manual Boot = yes") : string.Empty));
        L(Ci.F(t.T("  Stack           : {0} member(s)"), info.Members));
        L(Ci.F(t.T("  Uptime          : {0}"), info.Uptime));
        L(Ci.F(t.T("  Flash           : {0} free of {1}"), Mb(info.FlashFree), Mb(info.FlashTotal)));
        if (info.Cleanable.Count > 0)
        {
            L(Ci.F(t.T("  Other images    : {0} ({1}) that are not the running one: {2}"), info.Cleanable.Count, Mb(ImageNames.Sum(info.Cleanable)), string.Join(", ", info.Cleanable.Select(c => c.Name))));
        }
        if (info.Usb is not null) { L(Ci.F(t.T("  USB             : {0} ({1} free)"), info.UsbName, Mb(info.Usb.Free))); }
        else if (info.UsbDiagnostic.Length > 0) { L(t.T("  USB             : cannot be used (details in tab 3, next to \"Read switch USB\")")); }
        else { L(t.T("  USB             : not detected (no drive connected or the model has no USB storage port)")); }
        L(string.Empty);
        L(t.T("HOW TO FIND THE IMAGE"));
        L(t.T("  software.cisco.com > Software Download (requires an account with an active contract)"));
        L(Ci.F(t.T("  Search for the model: {0}"), info.Model));
        if (info.OS == "IOS-XE")
        {
            L(Ci.F(t.T("  Expected file: {0}.<version>.SPA.bin"), info.Family));
        }
        else if (info.Family.Length > 0)
        {
            L(Ci.F(t.T("  Recommended    : {0}-tar.<version>.tar  (includes the web manager; installed with archive download-sw)"), info.Family));
            L(Ci.F(t.T("  Alternative    : {0}-mz.<version>.bin   (only option if sent over serial)"), info.Family));
        }
        L(t.T("  Pick the version marked as suggested (star) and copy the MD5 shown on the page:"));
        L(t.T("  the tool compares it with the file and with what the switch computes."));
        if (info.Members > 1 && info.OS == "IOS")
        {
            L(t.T("  It is a stack: use the .tar (archive download-sw updates all members)."));
        }
        if (installSummary.Length > 0)
        {
            L(string.Empty);
            L(t.T("SHOW INSTALL SUMMARY"));
            L(installSummary);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Text of the warning shown when the operator checks "Allow an image from another family".
    /// The UI unchecks it if the operator does not accept.
    /// </summary>
    public string OtherFamilyWarning(UpgradeSession? ses) => Ci.F(t.T("DANGER: you are about to remove the protection that prevents loading an image that is not from this switch's family ({0}).{1}{1}An image from another family or other hardware can:{1}  - leave the switch unable to boot (it stays at the boot loader or reboots in a loop);{1}  - force you to recover it over the console with Xmodem (more than an hour) or to replace it;{1}  - boot without the features or license the configuration uses and take down the service that depends on it;{1}  - if the file does not come from Cisco, put tampered software on your network.{1}{1}Cisco does not support combinations outside the compatibility matrix. Use it only if Cisco (TAC or the official documentation) explicitly indicates that file for this model, with the MD5 verified and console access at hand.{1}{1}Do you understand the risk and want to allow it?"),
        ses?.Info is { Family.Length: > 0 } i ? i.Family : t.T("(query the switch first)"), "\r\n");

    /// <summary>Asks the operator before allowing another family; logs the decision. False = uncheck the box.</summary>
    public bool ConfirmOtherFamily(UpgradeSession? ses)
    {
        var fam = ses?.Info is { Family.Length: > 0 } i ? i.Family : t.T("(query the switch first)");
        if (!ui.Warn(OtherFamilyWarning(ses), t.T("Image from another family")))
        {
            return false;
        }
        Log.Warn(Ci.F(t.T("Operator {0} allowed loading an image from another family (expected {1})."), Environment.UserName, fam));
        return true;
    }

    /// <summary>Asks the operator before allowing the running image to be deleted too. False = uncheck.</summary>
    public bool ConfirmCleanAll()
    {
        const string n = "\r\n";
        var txt = t.T("You are about to allow deleting the running image too, so flash keeps only the new one.") + n + n +
            t.T("How the tool does it:") + n +
            t.T("  - If the new one fits next to the current one: the current one is deleted AT THE END, only after the switch boots the new one and the version is confirmed. This is the safe way (requires \"Reload when finished\").") + n +
            t.T("  - If it does not fit: it is deleted BEFORE copying. While copying and installing, the switch has no image in flash. If power is lost at that moment, someone reloads it or the copy fails, it can only be recovered over the console (Xmodem, more than an hour). For that case the MD5 published by Cisco is required and the new image is verified before deleting anything.") + n + n +
            t.T("This does not leave the switch \"factory new\": the configuration is not touched (Factory reset is for that). It only leaves flash without old images.") + n + n +
            t.T("Allow deleting the running image?");
        if (!ui.Warn(txt, t.T("Also delete the running image")))
        {
            return false;
        }
        Log.Warn(Ci.F(t.T("Operator {0} allowed deleting the running image too."), Environment.UserName));
        return true;
    }

    /// <summary>Xmodem from the boot loader. Returns the MD5 on the switch and whether it matches.</summary>
    private (string Md5, bool Ok, string Detail) XmodemFlow(UpgradePlan plan, Action<string, string, string> step)
    {
        var opt = plan.Options;
        if (conn!.Channel is not IXmodemPort xp)
        {
            throw new SwitchException(t.T("Xmodem requires a serial connection."));
        }
        var serial = conn.Channel as SerialChannel;
        var name = plan.Name;
        ui.Status(t.T("Enabling manual boot..."), 5);
        S.SendConfigLine("configure terminal");
        var r = S.SendConfigLine("boot manual");
        S.SendConfigLine("end");
        if (r.Errors.Count > 0)
        {
            throw new SwitchException(Ci.F(t.T("boot manual: {0}"), string.Join(" / ", r.Errors)));
        }
        if (!Ci.IsMatch(S.RunLong("write memory", 180), "\\[OK\\]"))
        {
            throw new SwitchException(t.T("write memory did not confirm [OK]."));
        }
        step(t.T("Manual boot enabled and saved"), "OK", t.T("boot manual + write memory"));

        ui.Status(t.T("Reloading to the boot loader..."), 8);
        S.ReloadWithoutSaving();
        if (S.Wait([("BL", "(?:^|\\n)\\s*switch:\\s*$"), ("IOS", "(?i)Press RETURN to get started")], 900, silenceMs: 2000, pushSeconds: 60)!.Key != "BL")
        {
            throw new SwitchException(t.T("The switch booted IOS instead of stopping at the boot loader."));
        }
        step(t.T("Boot loader reached"), "OK", t.T("switch:"));
        (string, string)[] bl = [("BL", "(?:^|\\n)\\s*switch:\\s*$")];
        S.SendLine("flash_init");
        S.Wait(bl, 180, silenceMs: 1500, pushSeconds: 30);

        // Speed
        var original = serial?.BaudRate ?? opt.BaudRate;
        if (serial is not null && opt.VelXmodem != original)
        {
            S.SendLine(Ci.F("set BAUD {0}", opt.VelXmodem));
            S.Sleep(800);
            serial.BaudRate = opt.VelXmodem;
            xp.DiscardInBuffer();
            S.SendLine(string.Empty);
            if (S.Wait(bl, 15, silenceMs: 800, noThrow: true) is null)
            {
                serial.BaudRate = original;
                xp.DiscardInBuffer();
                S.SendLine(string.Empty);
                if (S.Wait(bl, 15, silenceMs: 800, noThrow: true) is null)
                {
                    throw new SwitchException(t.T("The console was lost when changing the speed."));
                }
                step(t.T("Speed change"), "NOTE", Ci.F(t.T("Stays at {0} baud"), original));
            }
            else
            {
                step(t.T("Speed change"), "OK", Ci.F(t.T("{0} baud"), opt.VelXmodem));
            }
        }

        ui.Status(t.T("Starting Xmodem..."), 10);
        S.Read();
        S.SendLine("copy xmodem: flash:" + name);
        S.Sleep(1500);
        var res = Xmodem.Send(xp, File.ReadAllBytes(opt.FileLocal), t, Log.Write, (txt, pct) => ui.Status(txt, pct), Cancellation);
        step(t.T("Xmodem transfer"), "OK", string.Format(CultureInfo.CurrentCulture, t.T("{0:N0} bytes in {1:hh\\:mm\\:ss}, {2} retries, {3}-byte blocks"), res.Bytes, res.Duration, res.Retries, res.BlockSize));
        var saved = S.Wait(bl, 600, silenceMs: 2000, pushSeconds: 60)!;
        if (Ci.IsMatch(saved.Text, "(?i)(error|fail)"))
        {
            throw new SwitchException(Ci.F(t.T("The boot loader reported an error while saving: {0}"), Ci.Replace(saved.Text, "[\\r\\n]+", " | ").Trim()));
        }
        if (serial is not null && serial.BaudRate != original)
        {
            S.SendLine(Ci.F("set BAUD {0}", original));
            S.Sleep(800);
            serial.BaudRate = original;
            xp.DiscardInBuffer();
            S.SendLine(string.Empty);
            S.Wait(bl, 20, silenceMs: 800);
        }
        S.SendLine("set BOOT flash:" + name);
        S.Wait(bl, 60, silenceMs: 1000);
        step(t.T("Boot pointing to the new image"), "OK", Ci.F(t.T("set BOOT flash:{0}"), name));
        // If the new IOS updates the boot loader it reloads by itself: with MANUAL_BOOT=no it boots again without stopping
        S.SendLine("set MANUAL_BOOT no");
        S.Wait(bl, 60, silenceMs: 1000);
        step(t.T("Automatic boot in the boot loader"), "OK", t.T("set MANUAL_BOOT no"));

        ui.Status(t.T("Booting the new image..."), 85);
        S.SendLine("boot");
        WaitSerialBoot(40, 2);
        S.Connect(opt.User, opt.Password, opt.Enable);
        S.RunCommand("terminal length 0", 20);
        S.SendConfigLine("configure terminal");
        S.SendConfigLine("no boot manual");
        S.SendConfigLine("boot system flash:" + name);
        S.SendConfigLine("end");
        var rw = S.RunLong("write memory", 180);
        step(t.T("Automatic boot restored"), Ci.IsMatch(rw, "\\[OK\\]") ? "OK" : "NOTE", Ci.F(t.T("no boot manual, boot system flash:{0}, write memory"), name));

        var md5 = SwitchMd5("flash:" + name);
        var ok = md5 == plan.Md5Local || md5 == res.Md5Padding;
        return (md5, ok, md5 == plan.Md5Local ? t.T("identical to the file") : md5 == res.Md5Padding ? t.T("identical to the file + Xmodem padding") : t.T("does NOT match"));
    }

    /// <summary>
    /// "Update", with its confirmations. Returns null if the
    /// operator did not confirm. Throws <see cref="FormatException"/> with the denials if the plan cannot run.
    /// </summary>
    public UpgradeResult? Update(UpgradeSession ses, UpgradeOptions opt)
    {
        ArgumentNullException.ThrowIfNull(ses);
        var plan = Plan(ses, opt);
        if (plan.Denials.Count > 0)
        {
            throw new FormatException(Ci.F(t.T("Cannot update:{0}- {1}"), "\r\n", string.Join("\r\n- ", plan.Denials)));
        }
        var info = ses.Info!;
        var txtConf = Ci.F(t.T("{0} ({1}, serial {2}) will be updated from {3} to {4}.{5}{5}{6}{5}{5}The switch will reload. Continue?"), info.Hostname, info.Model, info.Serial, info.Version,
            plan.VersionExpected.Length > 0 ? plan.VersionExpected : plan.Name, "\r\n", string.Join("\r\n", plan.Steps.Select(x => "- " + x)));
        if (!ui.Ask(txtConf, t.T("Update IOS")))
        {
            return null;
        }
        if (plan.SameVersion && !ui.Warn(Ci.F(t.T("The switch already runs {0} (flash:/{1}).{2}{2}Do you want to reinstall it anyway (overwrite what is installed with {3})?"), info.Version, info.ImageTop, "\r\n", plan.Name), t.T("The switch already has that version")))
        {
            return null;
        }
        if (plan.DeleteInUse == "BEFORE")
        {
            const string n = "\r\n";
            var txtBefore = Ci.F(t.T("The new image only fits by deleting the running one FIRST (flash:/{0})."), info.ImageTop) + n + n +
                t.T("Before deleting, the MD5 of the new image is verified against Cisco's. Then, while copying and installing, the switch has no image in flash (it keeps running from RAM):") + n +
                t.T("  - Do NOT power it off or reload it until the tool finishes.") + n +
                t.T("  - If the copy fails, the tool does NOT reload: you can load the image again without losing service.") + n +
                t.T("  - If the switch reloads without an image, it stays at \"switch:\" and can only be recovered over the console with Xmodem.") + n + n +
                (opt.Connection == "SSH" ? t.T("You are connected over SSH: keep the console cable at hand.") + n + n : string.Empty) +
                t.T("Delete the running image and continue?");
            if (!ui.Warn(txtBefore, t.T("Delete the running image before copying")))
            {
                return null;
            }
        }
        var otherFamily = opt.IgnoreFamily && info.Family.Length > 0 && plan.Name.Length > 0 && !plan.Name.ToLowerInvariant().StartsWith(info.Family.ToLowerInvariant(), StringComparison.Ordinal);
        if (otherFamily && !ui.Warn(Ci.F(t.T("Last confirmation: {0} is NOT from family {1}. If it does not boot, recovery is over the console.{2}{2}Install it anyway?"), plan.Name, info.Family, "\r\n"), t.T("Image from another family")))
        {
            return null;
        }

        var start = DateTime.Now;
        var actions = new List<PushCheck>();
        var verification = new List<PushCheck>();
        var final = UpgradeOutcome.Failed;
        var errorFatal = string.Empty;
        var versionAfter = string.Empty;
        var md5Sw = string.Empty;
        var reload = false;
        var noImage = false;
        var scpEnabled = false;
        void Step(string action, string result, string detail)
        {
            actions.Add(new PushCheck(action, result, detail));
            Log.Write(result == "OK" ? "OK" : "WARN", Ci.F("{0}: {1} {2}", action, t.T(result), detail));
        }
        void Verif(string check, bool ok, string detail) => verification.Add(new PushCheck(check, ok ? "OK" : "FAIL", detail));
        void DeleteBefore()
        {
            // Only with everything verified: Cisco MD5 against the new image, and then the running one is deleted.
            ui.Status(t.T("Verifying the new image before deleting the running one..."), 8);
            if (plan.Method == "USB")
            {
                var mu = SwitchMd5(plan.SourceSw + plan.Name);
                var okU = mu == opt.Md5Expected;
                Verif(t.T("MD5 of the new image before deleting the running one"), okU, Ci.F("{0} {1} {2}", mu, okU ? "=" : "<>", opt.Md5Expected));
                if (!okU)
                {
                    throw new SwitchException(t.T("The MD5 of the image on the USB does not match Cisco's. Nothing was deleted."));
                }
            }
            else if (!Ci.Eq(plan.Md5Local, opt.Md5Expected))
            {
                throw new SwitchException(t.T("The file MD5 does not match Cisco's. Nothing was deleted."));
            }
            var ent = Dir("flash:")?.Entries.FirstOrDefault(e => Ci.Eq(e.Name, plan.ImagePrevious));
            if (ent is not null)
            {
                noImage = true;
                ui.Status(Ci.F(t.T("Deleting the running image flash:/{0}..."), plan.ImagePrevious));
                if (Bad(S.RunLong(DeleteCommand(ent), 900), out var err))
                {
                    throw new SwitchException(Ci.F(t.T("Could not delete the running image: {0}"), err));
                }
                Step(t.T("Delete the running image (before copying)"), "OK", Ci.F(t.T("flash:/{0}. From here the switch has no image in flash until finished."), plan.ImagePrevious));
            }
            var freeB = Dir("flash:")?.Free ?? 0;
            if (freeB < plan.SpaceNeeded)
            {
                throw new SwitchException(string.Format(CultureInfo.CurrentCulture, t.T("After deleting the running image there are {0:N1} MB free and ~{1:N1} MB are needed."), freeB / (double)MB, plan.SpaceNeeded / (double)MB));
            }
        }
        void DeleteAfter()
        {
            // Only if the new one booted and the version was confirmed; never the running one.
            var okV = verification.Any(v => v.Check == t.T("Version after reload") && v.Result == "OK");
            var imgNow = Ci.TryMatch(S.RunCommand("show version | include System image file", 30), "\"([^\"]+)\"", out var mi)
                ? Ci.Replace(mi.Groups[1].Value, "^[A-Za-z0-9\\-]+:/*", string.Empty).Split('/')[0] : string.Empty;
            if (!okV || imgNow.Length == 0 || Ci.Eq(imgNow, plan.ImagePrevious))
            {
                Step(t.T("Delete the previous image"), "NOTE", Ci.F(t.T("flash:/{0} is kept: it was not confirmed that the new one is running"), plan.ImagePrevious));
                return;
            }
            var ent = Dir("flash:")?.Entries.FirstOrDefault(e => Ci.Eq(e.Name, plan.ImagePrevious));
            if (ent is not null)
            {
                var okB = !Bad(S.RunLong(DeleteCommand(ent), 900), out _);
                Step(t.T("Delete the previous image (the new one already booted)"), okB ? "OK" : "NOTE", Ci.F(t.T("flash:/{0}"), plan.ImagePrevious));
            }
        }
        void RemoveScp()
        {
            S.SendConfigLine("configure terminal");
            S.SendConfigLine("no ip scp server enable");
            S.SendConfigLine("end");
            S.RunLong("write memory", 180);
            scpEnabled = false;
            Step(t.T("SCP server disabled"), "OK", t.T("no ip scp server enable"));
        }
        Log.Info(Ci.F(t.T("===== Update of {0}: {1} via {2} ====="), info.Hostname, plan.Name, plan.Method));
        if (otherFamily)
        {
            Step(t.T("Image from another family allowed by the operator"), "NOTE", Ci.F(t.T("{0} is not from family {1}; under the responsibility of {2}"), plan.Name, info.Family, Environment.UserName));
        }
        try
        {
            ui.Status(t.T("Connecting..."), 2);
            Connect(ses, opt);
            var d2 = conn!.Version();
            if (info.Serial.Length > 0 && !Ci.Eq(d2.Serial, info.Serial))
            {
                throw new SwitchException(Ci.F(t.T("The connected device (serial {0}) is not the one that was queried (serial {1})."), d2.Serial, info.Serial));
            }
            if (opt.SaveBefore)
            {
                Step(t.T("Save current configuration"), Ci.IsMatch(S.RunLong("write memory", 180), "\\[OK\\]") ? "OK" : "NOTE", t.T("write memory"));
            }
            // Same .bin already on flash (an interrupted attempt, or the running image itself): its MD5 is checked
            // BEFORE cleaning or deleting anything. If it is the expected one, it is kept and nothing is sent.
            var already = false;
            if (plan.Md5Ref.Length > 0 && plan.Type == "bin" && Dir("flash:")?.Entries.Any(e => !e.IsDir && Ci.Eq(e.Name, plan.Name)) == true)
            {
                ui.Status(t.T("Checking the copy already on flash (MD5)..."), 3);
                var md5Prev = SwitchMd5("flash:" + plan.Name);
                if (md5Prev == plan.Md5Ref)
                {
                    already = true;
                    md5Sw = md5Prev;
                    Step(t.T("Image already on flash"), "OK", Ci.F(t.T("flash:{0} already has the expected MD5 ({1}): it is neither deleted nor copied again."), plan.Name, md5Prev));
                }
                else if (plan.Method == "SSH" && !Ci.Eq(plan.Name, plan.ImagePrevious))
                {
                    Step(t.T("Image already on flash"), "NOTE", Ci.F(t.T("flash:{0} has a different MD5 ({1}): it is deleted and sent again."), plan.Name, md5Prev));
                    if (Bad(S.RunLong("delete /force flash:" + plan.Name, 300), out var err))
                    {
                        throw new SwitchException(Ci.F(t.T("Could not delete flash:{0}: {1}"), plan.Name, err));
                    }
                }
            }
            if (plan.Cleanup.Count > 0)
            {
                ui.Status(t.T("Cleaning flash (only the running image stays)..."), 4);
                FlashCleanup(info, plan, Step, already ? plan.Name : string.Empty);
            }
            bool alreadyConnected;
            if (plan.Method == "SERIAL" && !already)
            {
                if (plan.DeleteInUse == "BEFORE")
                {
                    DeleteBefore();
                }
                var rx = XmodemFlow(plan, Step);
                noImage = false;
                md5Sw = rx.Md5;
                Verif(t.T("MD5 on the switch"), rx.Ok, Ci.F("{0} ({1})", rx.Md5, rx.Detail));
                reload = true;
                alreadyConnected = true;
            }
            else
            {
                alreadyConnected = false;
                var src = already ? "flash:" : plan.SourceSw;
                if (plan.Method == "SSH")
                {
                    var cfg = S.RunCommand("show running-config | include ^aaa new-model|^aaa authorization exec|^ip scp server enable", 120);
                    if (!Ci.IsMatch(cfg, "(?m)^aaa new-model") || !Ci.IsMatch(cfg, "(?m)^aaa authorization exec"))
                    {
                        throw new SwitchException(t.T("SCP on IOS needs \"aaa new-model\" and \"aaa authorization exec default local\". They are not configured automatically because a mistake there locks you out of the switch: configure it by hand or use the USB."));
                    }
                    if (!Ci.IsMatch(cfg, "(?m)^ip scp server enable"))
                    {
                        S.SendConfigLine("configure terminal");
                        S.SendConfigLine("ip scp server enable");
                        S.SendConfigLine("end");
                        scpEnabled = true;
                        Step(t.T("SCP server temporarily enabled"), "OK", t.T("ip scp server enable"));
                    }
                }
                if (plan.Platform == "IOS-XE" && opt.CleanInactive)
                {
                    ui.Status(t.T("Freeing space (install remove inactive)..."), 5);
                    var tr = S.RunLong("install remove inactive", 1800, [("(?i)remove the above files\\?\\s*\\[y/n\\]\\s*:?\\s*$", "y")]);
                    Step(t.T("Clean inactive packages"), Ci.IsMatch(tr, "(?i)FAILED") ? "NOTE" : "OK", t.T("install remove inactive"));
                }
                if (plan.DeleteInUse == "BEFORE" && already)
                {
                    Step(t.T("Delete the running image (before copying)"), "OK", Ci.F(t.T("Not needed: flash:{0} is already the new image (same MD5)."), plan.Name));
                }
                else if (plan.DeleteInUse == "BEFORE")
                {
                    DeleteBefore();
                }
                if (plan.Method == "SSH")
                {
                    if (!already)
                    {
                        // An SSH control session left idle during a long transfer can hang afterwards: it is closed
                        // now and a new one (pinned to the same verified fingerprint) is opened after the transfer.
                        if (opt.Connection == "SSH")
                        {
                            Log.Info(t.T("The control session is closed during the transfer and reopened afterwards."));
                            Close();
                        }
                        ui.Status(Ci.F(t.T("Sending {0} via SCP..."), plan.Name), 10);
                        // Security rule 2: before SCP the fingerprint is always validated.
                        var trust = FingerprintPolicy.Confirm(Request(ses, opt), SshConnector.ReadHostKey(opt.IP, opt.PortSsh, t), new FingerprintStore(fingerprints), t, ui.Warn, Log.Write);
                        ses.Trust = trust;
                        var dur = Scp.Upload(trust, opt.User, opt.Password, opt.FileLocal, plan.Name, t, Log.Write, x => ui.Status(x), Cancellation);
                        Step(t.T("SCP transfer"), "OK", string.Format(CultureInfo.CurrentCulture, t.T("{0:N0} bytes in {1:hh\\:mm\\:ss}"), plan.Bytes, dur));
                        if (opt.Connection == "SSH")
                        {
                            Connect(ses, opt, 3);
                        }
                    }
                }

                ui.Status(t.T("Verifying MD5 on the switch..."), 40);
                if (md5Sw.Length == 0)
                {
                    md5Sw = SwitchMd5(src + plan.Name);
                }
                if (plan.Md5Ref.Length > 0)
                {
                    var ok = md5Sw == plan.Md5Ref;
                    Verif(t.T("MD5 on the switch"), ok, Ci.F("{0} {1} {2}", md5Sw, ok ? "=" : "<>", plan.Md5Ref));
                    if (!ok)
                    {
                        throw new SwitchException(t.T("The MD5 computed by the switch does not match: not installing."));
                    }
                }
                else
                {
                    verification.Add(new PushCheck(t.T("MD5 on the switch"), "NOTE", Ci.F(t.T("{0} (no reference MD5)"), md5Sw)));
                }

                if (plan.Platform == "IOS-XE")
                {
                    reload = InstallAdd(plan, opt, src, () => { if (scpEnabled) { RemoveScp(); } }, Step, verification);
                }
                else if (plan.Type == "tar")
                {
                    if (plan.SameVersion)
                    {
                        // archive download-sw does not reinstall the same version: it is extracted and the boot is set
                        ui.Status(t.T("archive tar /xtract (may take 5 to 15 minutes)..."), 50);
                        var tx = S.RunLong(Ci.F("archive tar /xtract {0}{1} flash:", src, plan.Name), 3600);
                        if (Ci.TryMatch(tx, "(?im)^\\s*(%\\s*)?Error\\b.*$", out var me))
                        {
                            throw new SwitchException(Ci.F(t.T("archive tar failed: {0}"), me.Value.Trim()));
                        }
                        var binNew = Ci.F("flash:/{0}/{0}.bin", plan.TargetName);
                        var lx = Dir("flash:/" + plan.TargetName);
                        if (lx is null || !lx.Entries.Any(e => Ci.Eq(e.Name, plan.TargetName + ".bin")))
                        {
                            throw new SwitchException(Ci.F(t.T("archive tar finished but {0} was not found."), binNew));
                        }
                        S.SendConfigLine("configure terminal");
                        var rb = S.SendConfigLine("boot system " + binNew);
                        S.SendConfigLine("end");
                        if (rb.Errors.Count > 0)
                        {
                            throw new SwitchException(Ci.F(t.T("boot system: {0}"), string.Join(" / ", rb.Errors)));
                        }
                        Step(t.T("archive tar /xtract (reinstall of the same version)"), Ci.IsMatch(S.RunLong("write memory", 180), "\\[OK\\]") ? "OK" : "NOTE", Ci.F(t.T("boot system {0} + write memory"), binNew));
                    }
                    else
                    {
                        ui.Status(t.T("archive download-sw (may take 10 to 20 minutes)..."), 50);
                        var ta = S.RunLong(Ci.F("archive download-sw /leave-old-sw {0}{1}", src, plan.Name), 3600);
                        if (!Ci.IsMatch(ta, "(?i)All software images installed"))
                        {
                            throw new SwitchException(Ci.F(t.T("archive download-sw did not finish well: {0}"), string.Join(" | ", Ci.Split(ta, "\\r?\\n").TakeLast(4))));
                        }
                        Step(t.T("archive download-sw"), "OK", t.T("All software images installed"));
                    }
                    noImage = false;
                    if (src == "flash:")
                    {
                        S.SendConfigLine("delete /force flash:" + plan.Name);
                        Step(t.T("Delete the .tar from flash:"), "OK", string.Empty);
                    }
                }
                else
                {
                    if (src != "flash:")
                    {
                        ui.Status(Ci.F(t.T("Copying {0} to flash:..."), plan.Name), 50);
                        var tc = S.RunLong(Ci.F("copy {0}{1} flash:{1}", src, plan.Name), 3600, [("(?i)Destination filename \\[" + Regex.Escape(plan.Name) + "\\]\\?\\s*$", string.Empty)]);
                        if (Ci.TryMatch(tc, "(?i)%\\s*Error.*", out var mc))
                        {
                            throw new SwitchException(Ci.F(t.T("copy failed: {0}"), mc.Value));
                        }
                        var md5F = SwitchMd5("flash:" + plan.Name);
                        Verif(t.T("MD5 after copying to flash:"), md5F == md5Sw, md5F);
                        if (md5F != md5Sw)
                        {
                            throw new SwitchException(t.T("The copy in flash: does not match the USB."));
                        }
                    }
                    S.SendConfigLine("configure terminal");
                    var rb = S.SendConfigLine("boot system flash:" + plan.Name);
                    S.SendConfigLine("end");
                    if (rb.Errors.Count > 0)
                    {
                        throw new SwitchException(Ci.F(t.T("boot system: {0}"), string.Join(" / ", rb.Errors)));
                    }
                    var rw = S.RunLong("write memory", 180);
                    noImage = false;
                    Step(t.T("Boot pointing to the new image"), Ci.IsMatch(rw, "\\[OK\\]") ? "OK" : "NOTE", Ci.F(t.T("boot system flash:{0} + write memory"), plan.Name));
                }
                if (scpEnabled)
                {
                    RemoveScp();
                }
                if (plan.Platform != "IOS-XE")
                {
                    var txtBoot = S.RunCommand("show boot", 30);
                    var okBoot = Ci.IsMatch(txtBoot, Regex.Escape(Ci.Replace(Ci.Replace(plan.Name, "(?i)-tar\\.", "-mz."), "(?i)\\.(tar|bin)$", string.Empty)));
                    Verif(t.T("BOOT points to the new image"), okBoot, Regex.Match(txtBoot, "(?im)^\\s*BOOT (?:path-list|variable)\\s*[:=]\\s*(\\S+)").Groups[1].Value);
                    if (opt.Reload)
                    {
                        ui.Status(t.T("Reloading..."), 70);
                        S.ReloadWithoutSaving();
                        reload = true;
                        Step(t.T("Reload"), "OK", t.T("reload"));
                    }
                }
            }

            if (reload && !alreadyConnected)
            {
                ui.Status(t.T("Waiting for boot with the new version..."), 80);
                if (opt.Connection == "SSH")
                {
                    Close();
                    WaitSshBoot(opt.IP, opt.PortSsh, 40);
                    Connect(ses, opt, 6);
                }
                else
                {
                    WaitSerialBoot(40);
                    S.Connect(opt.User, opt.Password, opt.Enable);
                    S.RunCommand("terminal length 0", 20);
                }
            }
            if (reload)
            {
                versionAfter = conn!.Version().Version;
                if (plan.VersionExpected.Length > 0)
                {
                    Verif(t.T("Version after reload"), Ci.Eq(versionAfter, plan.VersionExpected), Ci.F(t.T("{0} (expected {1})"), versionAfter, plan.VersionExpected));
                }
                else
                {
                    Verif(t.T("Version after reload"), !Ci.Eq(versionAfter, info.Version), Ci.F(t.T("{0} (before {1})"), versionAfter, info.Version));
                }
                if (plan.DeleteInUse == "AFTER")
                {
                    DeleteAfter();
                }
                if (plan.Platform == "IOS-XE")
                {
                    var committed = Ci.IsMatch(S.RunCommand("show install summary", 60), "(?im)\\bC\\s+.*" + Regex.Escape(versionAfter));
                    verification.Add(new PushCheck(t.T("Committed packages (install commit)"), committed ? "OK" : "NOTE", committed ? t.T("State C (committed)") : t.T("Check show install summary")));
                }
            }
            ses.Done = true;
            final = verification.Any(v => v.Result == "FAIL") ? UpgradeOutcome.Failed
                : verification.Any(v => v.Result != "OK") || actions.Any(a => a.Result != "OK") || !reload ? UpgradeOutcome.SuccessfulWithNotes
                : UpgradeOutcome.Successful;
        }
        catch (Exception ex) when (ex is SwitchException or OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException
            or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or FormatException)
        {
            errorFatal = masker.Protect(ex.Message);
            if (ex is OperationCanceledException)
            {
                final = UpgradeOutcome.Cancelled;
                errorFatal = t.T("Stopped by the operator: check the switch state on the console before reloading it.");
            }
            if (noImage)
            {
                errorFatal = Ci.F(t.T("WARNING: FLASH HAS NO BOOTABLE IMAGE (deleted {0}). DO NOT RELOAD OR POWER OFF THE SWITCH: it keeps running from RAM. Load the image again without reloading (for example: copy usbflash0:<image>.bin flash: and boot system flash:<image>.bin + write memory, or repeat the update). If it already reloaded, it stays at \"switch:\" and is recovered over the console with Xmodem. Detail: {1}"), plan.ImagePrevious, errorFatal);
            }
            Log.Error(errorFatal);
            if (scpEnabled && conn is not null)
            {
                try
                {
                    S.SendConfigLine("configure terminal");
                    S.SendConfigLine("no ip scp server enable");
                    S.SendConfigLine("end");
                }
                catch (Exception inner) when (inner is SwitchException or IOException or ConnectionClosedException or InvalidOperationException or Renci.SshNet.Common.SshException)
                {
                }
            }
        }
        finally
        {
            Close();
        }
        var end = DateTime.Now;
        var recordPath = RecordWriter.Write(Path.Combine(ses.Folder, RecordWriter.FileName("Upgrade", info.Hostname, start)), () => new HtmlRecords(t).Upgrade(new UpgradeRecordData
        {
            Result = final.Code(), ErrorFatal = errorFatal, Ticket = opt.Ticket, Responsible = opt.Responsible,
            StartText = RecordWriter.Stamp(start), EndText = RecordWriter.Stamp(end), Duration = RecordWriter.Duration(start, end),
            Hostname = info.Hostname, Model = info.Model, Serial = info.Serial, Members = info.Members, OS = info.OS, Version = info.Version,
            VersionExpected = plan.VersionExpected, VersionAfter = versionAfter, ImageName = plan.Name, Bytes = plan.Bytes, Method = plan.Method,
            Connection = opt.Connection, IP = opt.IP, PortCom = opt.PortCom,
            Fingerprint = ses.Trust is { } tr ? Ci.F("{0} {1} ({2})", tr.Algorithm, tr.Normal, tr.How) : string.Empty,
            Md5Expected = opt.Md5Expected, Md5Local = plan.Md5Local, Md5Switch = md5Sw, Folder = ses.Folder, Actions = actions, Verification = verification,
        }), Log, t);
        Log.Info(Ci.F(t.T("===== Result: {0} ====="), t.T(final.Code())));
        ui.Status(Ci.F(t.T("Update: {0}. Evidence in {1}"), t.T(final.Code()), ses.Folder), 100);
        return new UpgradeResult(ses, plan, final, errorFatal, md5Sw, versionAfter, actions, verification, start, end, recordPath);
    }

    /// <summary>install add ... activate commit; answers only "proceed [y/n]" (up to 3 times). Returns whether the switch reloads.</summary>
    private bool InstallAdd(UpgradePlan plan, UpgradeOptions opt, string src, Action removeScp, Action<string, string, string> step, List<PushCheck> verification)
    {
        // install add reloads the switch: the temporary SCP server is removed first, while the session is alive
        removeScp();
        ui.Status(t.T("install add ... activate commit (10 to 25 minutes, the switch reloads by itself)..."), 50);
        S.Read();
        S.SendLine(Ci.F("install add file {0}{1} activate commit", src, plan.Name));
        (string, string)[] pat =
        [
            ("YN", "(?i)Do you want to proceed(?: with reload)?\\?\\s*\\[y/n\\]\\s*:?\\s*$"),
            ("RELOAD", "(?i)(reloading|rebooting|restarting system|Initializing Hardware|Press RETURN to get started)"),
            ("PROMPT", ConsoleSession.PromptRegex),
            ("QUESTION", ConsoleSession.QuestionRegex),
        ];
        var answered = 0;
        var all = new StringBuilder();
        var reload = false;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalMinutes < 90)
        {
            WaitResult r;
            try
            {
                r = S.Wait(pat, 300, silenceMs: 3000, returnText: true)!;
            }
            catch (ConnectionClosedException)
            {
                reload = true;
                break;
            }
            all.Append(r.Text);
            if (Ci.IsMatch(all.ToString(), "(?i)FAILED"))
            {
                throw new SwitchException(Ci.F(t.T("install add failed: {0}"), Regex.Match(all.ToString(), "(?im)^.*FAILED.*$").Value.Trim()));
            }
            if (r.Key == "QUESTION" || (r.Key == "YN" && answered >= 3))
            {
                throw S.StopUnexpectedQuestion(all.ToString(), "install add ... activate commit");
            }
            if (r.Key == "YN")
            {
                S.SendLine("y");
                answered++;
            }
            else if (r.Key == "RELOAD")
            {
                reload = true;
                break;
            }
            else if (r.Key == "PROMPT")
            {
                break;
            }
            ui.Status(Ci.F(t.T("install add in progress... {0}"), all.ToString().Split('\n').Where(l => l.Trim().Length > 0).LastOrDefault() ?? string.Empty));
        }
        var txt = all.ToString();
        if (Ci.TryMatch(txt, "(?im)^.*\\bFAILED\\b.*$", out var mf))
        {
            throw new SwitchException(Ci.F(t.T("install add failed: {0}"), mf.Value.Trim()));
        }
        if (reload)
        {
            step(t.T("install add / activate / commit"), "OK", t.T("The switch is reloading"));
        }
        else if (Ci.IsMatch(txt, "(?i)SUCCESS:\\s*install_add_activate_commit"))
        {
            step(t.T("install add / activate / commit"), "OK", t.T("Installed and committed (SUCCESS), but the switch did not reload by itself"));
            if (opt.Reload)
            {
                ui.Status(t.T("Reloading to boot the new version..."), 70);
                S.ReloadWithoutSaving();
                reload = true;
                step(t.T("Reload"), "OK", t.T("reload (install did not reload by itself)"));
            }
            else
            {
                verification.Add(new PushCheck(t.T("Running version"), "NOTE", t.T("Installed and committed; takes effect when you reload")));
            }
        }
        else
        {
            throw new SwitchException(Ci.F(t.T("install add finished without SUCCESS or reload: {0}"), string.Join(" | ", txt.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(3))));
        }
        return reload;
    }
}
