using Rackside.Core.Localization;
using Rackside.Core.Push;
using Rackside.Core.Security;

namespace Rackside.Tests;

public class PushPlannerTests
{
    private static readonly Translator T = Translator.English;

    [Fact]
    public void The_snapshot_parameter_set_is_valid()
    {
        Assert.Empty(PushPlanner.Validate(PushScenarios.Parameters(), T));
    }

    [Fact]
    public void Nothing_checked_is_rejected()
    {
        var e = PushPlanner.Validate(new PushParameters(), T);
        Assert.Single(e);
        Assert.Contains("at least one group", e[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hostname OTHER")]
    [InlineData("HOSTNAME OTHER")]
    [InlineData("exit")]
    [InlineData("configure terminal")]
    [InlineData("do reload")]
    [InlineData("event manager applet X")]
    [InlineData("interface Vlan10\n ip address 10.1.1.1 255.255.255.0")]
    [InlineData("privilege exec level 1 show")]
    public void Extra_commands_that_must_never_be_pushed(string text)
    {
        var r = PushPlanner.FilterExtra(text, T, new SecretMasker());
        Assert.Empty(r.Blocks);
        Assert.NotEmpty(r.Report);
    }

    [Fact]
    public void Allowed_extra_commands_keep_their_sublines()
    {
        var r = PushPlanner.FilterExtra("udld enable\r\n! comment\r\nerrdisable recovery interval 300", T, new SecretMasker());
        Assert.Equal(["udld enable", "errdisable recovery interval 300"], r.Blocks.Select(b => b.Header));
        Assert.Empty(r.Report);
    }

    [Fact]
    public void A_banner_uses_the_first_delimiter_it_does_not_contain()
    {
        var p = new PushParameters { Banner = { ApplyLogin = true, Login = "Call #123 or $5\r\n\r\n" } };
        var text = PushPlanner.PlatformText(PushPlanner.Lines(p, "IOS", null, T, new SecretMasker()), false, T, new SecretMasker());
        Assert.Contains("banner login %\nCall #123 or $5\n%\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_never_reach_the_preview()
    {
        var p = PushScenarios.Parameters();
        var preview = PushScenarios.Preview();
        foreach (var s in p.Secrets)
        {
            Assert.DoesNotContain(s, preview, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Removing_others_needs_the_running_config()
    {
        var p = new PushParameters { Ntp = { Apply = true, Servers = ["10.0.0.5"], RemoveOthers = true } };
        var withoutRun = PushPlanner.Lines(p, "IOS", null, T, new SecretMasker());
        Assert.Contains(withoutRun, i => i.Verif == "note");
        var withRun = PushPlanner.Lines(p, "IOS", "ntp server 10.0.0.5\nntp server 10.9.9.9\n", T, new SecretMasker());
        Assert.Equal(["ntp server 10.0.0.5", "no ntp server 10.9.9.9"], withRun.Select(i => i.Line));
    }
}
