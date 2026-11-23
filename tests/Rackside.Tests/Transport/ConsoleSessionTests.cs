using Rackside.Core.Localization;
using Rackside.Core.Restore;
using Rackside.Core.Security;
using Rackside.Transport.Console;

namespace Rackside.Tests.Transport;

public class ConsoleSessionTests
{
    private static ConsoleSession Session(FakeChannel ch, SecretMasker? masker = null, StringWriter? transcript = null) =>
        new(ch, Translator.English, masker ?? new SecretMasker(), transcript is null ? null : new SessionTranscript(transcript, masker ?? new SecretMasker()));

    /// <summary>A switch with login + enable: user -> password -> SW1> -> enable -> password -> SW1#.</summary>
    private static FakeChannel LoginSwitch(string user = "admin", string password = "Pa55w0rd!", string enable = "En4ble")
    {
        var state = "start";
        var ch = new FakeChannel();
        ch.OnLine = l =>
        {
            switch (state)
            {
                case "start": state = "user"; return "\r\nUser Access Verification\r\n\r\nUsername: ";
                case "user": ch.Echo = false; state = l == user ? "pass" : "user"; return "Password: ";
                case "pass": ch.Echo = true; state = l == password ? "exec" : "user"; return l == password ? "SW1>" : "% Login invalid\r\n\r\nUsername: ";
                case "exec":
                    if (l == "enable") { ch.Echo = false; state = "enable"; return "Password: "; }
                    return "SW1>";
                case "enable": ch.Echo = true; state = l == enable ? "priv" : "exec"; return l == enable ? "SW1#" : "% Bad secrets\r\n\r\nSW1>";
                default: return "SW1#";
            }
        };
        return ch;
    }

    [Fact]
    public void Connect_logs_in_and_enters_enable()
    {
        var ch = LoginSwitch();
        Assert.Equal("SW1", Session(ch).Connect("admin", "Pa55w0rd!", "En4ble"));
        Assert.Equal(["", "admin", "Pa55w0rd!", "enable", "En4ble"], ch.Lines);
    }

    [Fact]
    public void A_wrong_enable_secret_is_reported()
    {
        var ex = Assert.Throws<SwitchException>(() => Session(LoginSwitch()).Connect("admin", "Pa55w0rd!", "wrong"));
        Assert.Contains("Login rejected by the switch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_a_user_without_one_stops()
    {
        var ex = Assert.Throws<SwitchException>(() => Session(LoginSwitch()).Connect(string.Empty, "x", "y"));
        Assert.Contains("asks for a username", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rommon_is_detected()
    {
        var ch = new FakeChannel { OnLine = _ => "switch: " };
        var ex = Assert.Throws<SwitchException>(() => Session(ch).Connect("a", "b", "c"));
        Assert.Contains("ROMMON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_setup_dialog_is_answered_no()
    {
        var first = true;
        var ch = new FakeChannel();
        ch.OnLine = l =>
        {
            if (first) { first = false; return "Would you like to enter the initial configuration dialog? [yes/no]: "; }
            return l == "no" ? "\r\nPress RETURN to get started!\r\n" : "Switch#";
        };
        Assert.Equal("Switch", Session(ch).Connect(null, null, null));
        Assert.Contains("no", ch.Lines);
    }

    [Fact]
    public void Commands_page_through_more_and_drop_echo_and_prompt()
    {
        var ch = new FakeChannel
        {
            OnLine = l => l == "show version" ? "line 1\r\nline 2\r\n --More-- " : "SW1#",
            OnKey = c => c == ' ' ? "\b\b\b\b\b\b\b\b\b        \b\b\b\b\b\b\b\b\bline 3\r\nSW1#" : null,
        };
        var s = Session(ch);
        // The line break before --More-- is removed with it (known quirk;
        // the app sends "terminal length 0" right after connecting).
        Assert.Equal("line 1\r\nline 2line 3", s.RunCommand("show version"));
        Assert.Contains(' ', ch.Keys);
    }

    [Fact]
    public void A_config_line_reports_mode_messages_and_errors()
    {
        var ch = new FakeChannel
        {
            OnLine = l => l switch
            {
                "interface Gi1/0/1" => "SW1(config-if)#",
                "mls qos" => "               ^\r\n% Invalid input detected at '^' marker.\r\n\r\nSW1(config)#",
                "spanning-tree portfast" => "%Warning: portfast should only be enabled on ports connected to a single host.\r\n% Portfast has been configured on Gi1/0/1\r\nSW1(config-if)#",
                _ => "SW1(config)#",
            },
        };
        var s = Session(ch);
        Assert.Equal("config-if", s.SendConfigLine("interface Gi1/0/1").Mode);
        var bad = s.SendConfigLine("mls qos");
        Assert.Equal("config", bad.Mode);
        Assert.Equal(["% Invalid input detected at '^' marker."], bad.Errors);
        var warn = s.SendConfigLine("spanning-tree portfast");
        Assert.Empty(warn.Errors);
        Assert.Equal(["% Portfast has been configured on Gi1/0/1"], warn.Messages);
    }

    [Fact]
    public void Only_expected_questions_are_answered()
    {
        var ch = new FakeChannel
        {
            OnLine = l => l switch
            {
                "crypto key zeroize rsa" => "Do you really want to remove these keys? [yes/no]: ",
                "yes" => "SW1(config)#",
                _ => "SW1(config)#",
            },
        };
        var s = Session(ch);
        var r = s.SendConfigLine("crypto key zeroize rsa", answers: [("(?i)remove these keys\\? \\[yes/no\\]:\\s*$", "yes")]);
        Assert.Equal("config", r.Mode);
        Assert.Contains("yes", ch.Lines);
    }

    [Fact]
    public void An_unexpected_confirm_is_answered_n_and_stops()
    {
        var ch = new FakeChannel
        {
            OnLine = l => l == "no vlan 10" ? "Destination filename [vlan.dat]? [confirm]" : "SW1(config)#",
            OnKey = c => c == 'n' ? "n\r\nSW1(config)#" : null,
        };
        var masker = new SecretMasker();
        masker.Register("Secret123");
        var ex = Assert.Throws<UnexpectedQuestionException>(() => Session(ch, masker).SendConfigLine("no vlan 10"));
        Assert.Contains("Answered \"n\"", ex.Message, StringComparison.Ordinal);
        Assert.Equal(['n'], ch.Keys);
    }

    [Fact]
    public void An_unexpected_yes_no_question_is_answered_no()
    {
        var ch = new FakeChannel
        {
            OnLine = l => l switch
            {
                "vtp mode server" => "This will erase all VLANs. Continue? [yes/no]: ",
                _ => "SW1(config)#",
            },
        };
        var ex = Assert.Throws<UnexpectedQuestionException>(() => Session(ch).SendConfigLine("vtp mode server"));
        Assert.Contains("Answered \"no\"", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["vtp mode server", "no"], ch.Lines);
    }

    [Fact]
    public void An_unknown_question_gets_ctrl_c()
    {
        var ch = new FakeChannel
        {
            OnLine = l => l == "copy run start" ? "Destination filename [startup-config]? " : "SW1#",
            OnKey = c => c == '\u0003' ? "^C\r\nSW1#" : null,
        };
        var ex = Assert.Throws<UnexpectedQuestionException>(() => Session(ch).SendConfigLine("copy run start"));
        Assert.Contains("Ctrl+C", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaving_configuration_mode_stops_the_apply()
    {
        var ch = new FakeChannel { OnLine = l => l == "end" ? "SW1#" : "SW1(config)#" };
        var s = Session(ch);
        var blocks = new List<ConfigBlock> { new("hostname X"), new("end"), new("never sent") };
        var ex = Assert.Throws<SwitchException>(() => s.ApplyPlan(blocks, 3));
        Assert.Contains("took the switch out of configuration mode", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("never sent", ch.Lines);
    }

    [Fact]
    public void Apply_collects_errors_and_skips_children_of_a_failed_header()
    {
        var ch = new FakeChannel
        {
            OnLine = l => l switch
            {
                "interface Gi9/0/1" => "% Invalid input detected at '^' marker.\r\nSW1(config)#",
                "interface Gi1/0/1" => "SW1(config-if)#",
                "description up" or "switchport mode access" => "SW1(config-if)#",
                "exit" => "SW1(config)#",
                "end" => "SW1#",
                _ => "SW1(config)#",
            },
        };
        var blocks = new List<ConfigBlock>
        {
            new("interface Gi9/0/1", [" description lost"]),
            new("interface Gi1/0/1", [" description up", " switchport mode access"]),
            new("banner motd", isBanner: true, bannerText: ["Hello"], bannerType: "motd") { Delimiter = "#" },
        };
        var statuses = new List<int>();
        var r = Session(ch).ApplyPlan(blocks, 6, (_, p) => statuses.Add(p));
        Assert.Equal(["interface Gi9/0/1", "  description lost"], r.Errors.Select(e => e.Line));
        Assert.Equal("Not applied: the header line failed", r.Errors[1].Answer);
        Assert.Contains("banner motd #", ch.Lines);
        Assert.Equal("end", ch.Lines[^1]);
        Assert.Equal(100, statuses[^1]);
    }

    [Fact]
    public void The_transcript_masks_secrets_split_across_reads()
    {
        var masker = new SecretMasker();
        masker.Register("Pa55w0rd!");
        var sw = new StringWriter();
        var ch = new FakeChannel();
        var s = Session(ch, masker, sw);
        ch.Emit("username admin secret Pa55");
        s.Read();
        ch.Emit("w0rd!\r\nSW1#");
        s.Read();
        Assert.DoesNotContain("Pa55", sw.ToString(), StringComparison.Ordinal);
        Assert.Contains("********", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_reports_the_last_output_masked()
    {
        var masker = new SecretMasker();
        masker.Register("Secret123");
        var ch = new FakeChannel();
        ch.Emit("community Secret123 RO\r\nstill working");
        var ex = Assert.Throws<SwitchException>(() => Session(ch, masker).Wait([("PROMPT", ConsoleSession.PromptRegex)], timeoutSeconds: 1, silenceMs: 100));
        Assert.Contains("Timed out (1s)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret123", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_cancels_a_wait()
    {
        using var cts = new CancellationTokenSource(300);
        var s = new ConsoleSession(new FakeChannel(), Translator.English, new SecretMasker(), cancel: cts.Token);
        var ex = Assert.Throws<OperationCanceledException>(() => s.Wait([("PROMPT", ConsoleSession.PromptRegex)], timeoutSeconds: 30));
        Assert.Equal("Operation stopped by the operator.", ex.Message);
    }

    [Theory]
    [InlineData("\u001b[2Jhello\u001b[0m", "hello")]
    [InlineData("a\r\nb --More-- \b\b\b\b\b\b\b\b\b         \b\b\b\b\b\b\b\b\bc", "a\nbc")]
    [InlineData("x\u0007y\u007f", "xy")]
    public void Clean_text(string raw, string expected) => Assert.Equal(expected, ConsoleSession.CleanText(raw));
}
