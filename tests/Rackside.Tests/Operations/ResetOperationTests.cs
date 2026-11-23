using Rackside.Core.Localization;
using Rackside.Core.Security;
using Rackside.Operations.Reset;

namespace Rackside.Tests.Operations;

/// <summary>Factory reset against FakeResetConsole.</summary>
public sealed class ResetOperationTests : IDisposable
{
    private readonly string dir = Path.Combine(RepoPaths.Root, "tests", "out", "dotnet", "reset_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private ResetRun Run() => new()
    {
        PortCom = "COM3", ConsoleUser = "admin", ConsolePassword = "PasswordConsole1", ConsoleEnable = "EnableConsole1",
        FolderRecord = Path.Combine(dir, "reset"), Ticket = "RMA-8841", Responsible = "technician", Reason = "Replacement unit back to stock",
    };

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void Review_confirm_with_the_exact_hostname_and_reset(string language)
    {
        var t = Translator.For(language);
        var sw = new FakeResetConsole();
        var op = new ResetOperation(t, new SecretMasker(), new FakeOperator { QuestionAnswer = true }, (_, _) => sw);
        var review = op.Review(Run());
        Assert.StartsWith("RESET_SW07_", Path.GetFileName(review.Folder), StringComparison.Ordinal);
        Assert.True(File.Exists(review.PathBackup));
        Assert.Contains("hostname SW07", File.ReadAllText(review.PathBackup), StringComparison.Ordinal);
        Assert.Contains("flash:c2960x-universalk9-mz.152-7.E3", review.Summary, StringComparison.Ordinal);
        Assert.Equal(["vlan.dat", "private-config.text"], review.Info.ToDelete);

        Assert.False(ResetOperation.CanReset(review, string.Empty));
        Assert.False(ResetOperation.CanReset(review, "sw07"));
        Assert.Throws<FormatException>(() => op.Reset(review, "sw07"));
        Assert.True(ResetOperation.CanReset(review, "SW07"));

        var r = op.Reset(review, "SW07");
        Assert.NotNull(r);
        Assert.Equal(ResetOutcome.Successful, r.Outcome);
        Assert.False(sw.Startup);
        Assert.False(sw.Vlan);
        Assert.Contains("no", sw.Lines);
        Assert.False(ResetOperation.CanReset(review, "SW07"));
        Assert.True(File.Exists(r.RecordPath), "erase record");
        Assert.StartsWith("Report_Reset_SW07_", Path.GetFileName(r.RecordPath), StringComparison.Ordinal);
        var log = File.ReadAllText(Path.Combine(review.Folder, "log.txt"));
        Assert.Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, t.T("===== Result: {0} ====="), t.T("SUCCESSFUL")), log, StringComparison.Ordinal);
        foreach (var f in Directory.GetFiles(Path.Combine(dir, "reset"), "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("PasswordConsole1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("EnableConsole1", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_is_erased_if_the_operator_does_not_confirm()
    {
        var sw = new FakeResetConsole();
        var op = new ResetOperation(Translator.English, new SecretMasker(), new FakeOperator { QuestionAnswer = false }, (_, _) => sw);
        var review = op.Review(Run());
        Assert.Null(op.Reset(review, "SW07"));
        Assert.True(sw.Startup);
        Assert.DoesNotContain("write erase", sw.Lines);
    }
}
