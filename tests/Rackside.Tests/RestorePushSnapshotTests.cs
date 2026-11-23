namespace Rackside.Tests;

/// <summary>
/// Restore (tests/snapshots/restore: a backup from tests/fixtures/backups restored on a simulated target)
/// and push (preview, lines per switch, validation messages) against their snapshots.
/// </summary>
public class RestorePushSnapshotTests
{
    private static readonly string[] RestoreFiles = ["config.txt", "changes.txt", "mapping.txt", "mode.txt"];

    public static TheoryData<string> RestoreCases()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(Snapshot.PathOf("restore")).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(dir));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RestoreCases))]
    public void Restore_case_is_complete(string scenario)
    {
        var dir = Snapshot.PathOf("restore", scenario);
        foreach (var f in RestoreFiles)
        {
            Assert.True(File.Exists(Path.Combine(dir, f)), $"{scenario}/{f} is missing");
        }
        var backup = scenario[..scenario.IndexOf("__", StringComparison.Ordinal)];
        Assert.True(File.Exists(RepoPaths.Combine("tests", "fixtures", "backups", backup + ".txt")), $"fixture {backup}.txt is missing");
        Assert.Contains(File.ReadAllText(Path.Combine(dir, "mode.txt")).Trim(), new[] { "DIRECT", "CONVERT" });
    }

    [Fact]
    public void There_are_restore_cases_and_a_push_preview()
    {
        Assert.True(RestoreCases().Count >= 6);
        Assert.True(File.Exists(Snapshot.PathOf("push", "preview.txt")));
    }

    [Theory]
    [MemberData(nameof(RestoreCases))]
    public void Core_reproduces_the_restore_snapshots(string scenario)
    {
        var actual = RestoreScenarios.Run(scenario);
        Snapshot.Match(actual.Mode, "restore", scenario, "mode.txt");
        Snapshot.Match(actual.Config, "restore", scenario, "config.txt");
        Snapshot.Match(actual.Changes, "restore", scenario, "changes.txt");
        Snapshot.Match(actual.Mapping, "restore", scenario, "mapping.txt");
    }

    [Fact]
    public void Core_reproduces_the_push_preview_snapshot()
    {
        Snapshot.Match(PushScenarios.Preview(), "push", "preview.txt");
    }

    [Theory]
    [InlineData("IOS", true, "lines_ios.txt")]
    [InlineData("IOS-XE", false, "lines_xe_without_acl.txt")]
    public void Core_reproduces_the_push_lines_for_a_switch(string platform, bool withAcl, string file)
    {
        Snapshot.Match(PushScenarios.Lines(platform, withAcl), "push", file);
    }

    [Fact]
    public void Core_reproduces_the_push_validation_messages()
    {
        Snapshot.Match(PushScenarios.Validation().Replace("\r\n", "\n", StringComparison.Ordinal), "push", "validation.txt");
    }

}
