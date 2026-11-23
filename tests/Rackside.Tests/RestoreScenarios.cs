using System.Globalization;
using System.Text;
using Rackside.Core.Localization;
using Rackside.Core.Restore;
using Rackside.Core.Security;

namespace Rackside.Tests;

/// <summary>
/// The restore scenarios of tests/snapshots/restore: a backup from tests/fixtures/backups, a simulated
/// target (its interfaces and version) and the restore dialog with its default values.
/// </summary>
internal static class RestoreScenarios
{
    public sealed record Output(string Mode, string Config, string Changes, string Mapping);

    private static readonly Dictionary<string, (string[] Ifs, string Ver)> Targets = new(StringComparer.Ordinal)
    {
        ["2960xr_ios"] = (
            ["Vlan1", .. Range("GigabitEthernet1/0/", 48), .. Range("TenGigabitEthernet1/0/", 4)],
            "Cisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version 15.2(7)E6, RELEASE SOFTWARE (fc2)\r\nModel number                    : WS-C2960XR-48TS-I\r\nSystem serial number            : FOC0000TEST"),
        ["9200l_xe"] = (
            ["Vlan1", "GigabitEthernet0/0", .. Range("GigabitEthernet1/0/", 48), .. Range("TenGigabitEthernet1/1/", 4)],
            "Cisco IOS XE Software, Version 17.09.05\r\nCisco IOS Software [Cupertino], Catalyst L3 Switch Software (CAT9K_LITE_IOSXE), Version 17.9.5, RELEASE SOFTWARE (fc1)\r\nModel Number                       : C9200L-48P-4X\r\nSystem Serial Number               : JAE0000TEST"),
    };

    private const string Dir = "Directory of flash:/\r\n\r\n    3  drwx         512   Mar 1 1993 00:08:51 +00:00  c2960x-universalk9-mz.152-7.E3";

    public static Output Run(string scenario)
    {
        var sep = scenario.IndexOf("__", StringComparison.Ordinal);
        var source = SourceConfig.ReadFile(RepoPaths.Combine("tests", "fixtures", "backups", scenario[..sep] + ".txt"));
        var (ifs, ver) = Targets[scenario[(sep + 2)..]];
        var brief = new StringBuilder("Interface              IP-Address      OK? Method Status                Protocol\r\n");
        brief.AppendJoin("\r\n", ifs.Select(i => string.Format(CultureInfo.InvariantCulture, "{0,-22} unassigned      YES unset  down                  down", i)));
        var target = TargetInfo.Parse(ver, brief.ToString(), Dir, string.Empty);

        var opt = RestoreOptions.FromSource(source);
        opt.PasswordAdmin = "NewPass123";
        opt.EnableSecret = "Enable123";
        opt.CreateVlans = true;
        opt.RemoveCertificate = true;
        opt.IncludeUsers = true;

        var masker = new SecretMasker();
        masker.Register(opt.PasswordAdmin, opt.EnableSecret);
        var t = Translator.English;
        var plan = RestorePlanner.Build(source, target, opt, t, masker);
        return new Output(
            plan.ModeCode,
            RestoreTexts.Config(plan, masker, opt.PasswordAdmin, opt.EnableSecret),
            RestoreTexts.Changes(plan, t),
            RestoreTexts.Mapping(plan, t));
    }

    private static IEnumerable<string> Range(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => prefix + i.ToString(CultureInfo.InvariantCulture));
}
