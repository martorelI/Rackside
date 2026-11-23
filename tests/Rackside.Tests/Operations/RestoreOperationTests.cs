using Rackside.Core.Localization;
using Rackside.Core.Restore;
using Rackside.Core.Security;
using Rackside.Operations.Restore;

namespace Rackside.Tests.Operations;

/// <summary>Restore: a 9200L (IOS-XE) backup with a rejected line, applied to a 2960-XR.</summary>
public sealed class RestoreOperationTests : IDisposable
{
    private readonly string dir = Path.Combine(RepoPaths.Root, "tests", "out", "dotnet", "restore_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private static FakeCatalyst NewSwitch() => new()
    {
        Ifs = ["Vlan1", .. Enumerable.Range(1, 52).Select(i => "GigabitEthernet1/0/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        Ver = "Cisco IOS Software, C2960X Software, Version 15.2(7)E6, RELEASE\r\nModel number                    : WS-C2960XR-48TS-I\r\nSystem serial number            : FOC0000TEST",
    };

    private RestoreRun Run(SourceConfig source, string? hostname = null)
    {
        var o = RestoreOptions.FromSource(source);
        o.PasswordAdmin = "NewPass123";
        o.EnableSecret = "Enable123";
        if (hostname is not null)
        {
            o.Hostname = hostname;
        }
        return new RestoreRun { Options = o, PasswordAdmin2 = "NewPass123", EnableSecret2 = "Enable123", PortCom = "COM3", FolderRecord = Path.Combine(dir, "drp"), Save = true };
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void Review_then_apply_with_a_changed_hostname(string language)
    {
        var t = Translator.For(language);
        var sw = NewSwitch();
        var ui = new FakeOperator { QuestionAnswer = true };
        var op = new RestoreOperation(t, new SecretMasker(), ui, (_, _) => sw);
        var source = SourceConfig.ReadFile(RepoPaths.Combine("tests", "fixtures", "backups", "xe_9200l_with_error.txt"));

        var review = op.Review(source, Run(source));
        Assert.Equal(RestoreMode.Convert, review.Plan.Mode);
        var firstFolder = review.Folder;

        var r = op.Apply(review, Run(source, "SW23"));
        Assert.NotNull(r);
        Assert.Equal("SW23", sw.Host);
        Assert.Equal(RestoreOutcome.SuccessfulWithNotes, r.Outcome);
        Assert.Single(r.Errors);
        Assert.True(r.Saved);
        Assert.NotEqual(firstFolder, review.Folder);
        Assert.StartsWith("DRP_SW23_", Path.GetFileName(review.Folder), StringComparison.Ordinal);
        foreach (var f in new[] { "log.txt", "report.txt", "console_session.txt", "applied_config.txt", "interface_mapping.csv" })
        {
            Assert.True(File.Exists(Path.Combine(review.Folder, f)), f);
        }
        var log = File.ReadAllText(Path.Combine(review.Folder, "log.txt"));
        Assert.Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, t.T("===== Result: {0} ({1} switch error(s), saved={2}) ====="), t.T("SUCCESSFUL WITH NOTES"), 1, "True"), log, StringComparison.Ordinal);
        Assert.Contains(t.T("Hostname"), log + File.ReadAllText(review.PathReport), StringComparison.Ordinal);
        Assert.True(File.Exists(r.RecordPath), "DRP record");
        Assert.StartsWith("Report_DRP_SW23_", Path.GetFileName(r.RecordPath), StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", File.ReadAllText(r.RecordPath), StringComparison.Ordinal);
        foreach (var f in Directory.GetFiles(review.Folder))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("NewPass123", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Enable123", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_is_sent_if_the_operator_does_not_confirm()
    {
        var sw = NewSwitch();
        var ui = new FakeOperator { QuestionAnswer = false };
        var op = new RestoreOperation(Translator.English, new SecretMasker(), ui, (_, _) => sw);
        var source = SourceConfig.ReadFile(RepoPaths.Combine("tests", "fixtures", "backups", "xe_9200l_with_error.txt"));
        var review = op.Review(source, Run(source));
        Assert.Null(op.Apply(review, Run(source)));
        Assert.Equal("Switch", sw.Host);
        Assert.DoesNotContain(sw.Cfg, c => c.StartsWith("hostname", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_reports_the_tab()
    {
        var op = new RestoreOperation(Translator.English, new SecretMasker(), new FakeOperator());
        var source = SourceConfig.ReadFile(RepoPaths.Combine("tests", "fixtures", "backups", "xe_9200l_with_error.txt"));
        var run = Run(source) with { PortCom = string.Empty, EnableSecret2 = "other", FolderRecord = " " };
        run.Options.IP = "10.0.0.1";
        run.Options.Mask = "255.0.255.0";
        var p = op.Validate(source, run);
        Assert.Contains((0, "Enter the COM port."), p);
        Assert.Contains((1, "Invalid mask."), p);
        Assert.Contains((1, "The enable secrets do not match."), p);
        Assert.Contains((2, "Enter the folder where the DRP record will be saved."), p);
    }
}
