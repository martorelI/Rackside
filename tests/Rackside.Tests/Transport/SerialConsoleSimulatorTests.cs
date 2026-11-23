using System.Text.Json;
using Rackside.Core.Localization;
using Rackside.Core.Security;
using Rackside.Transport.Console;

namespace Rackside.Tests.Transport;

/// <summary>Serial console against sim/configure.py pty (a 2960-X with login + enable, SW09).</summary>
public class SerialConsoleSimulatorTests
{
    [SkippableFact]
    public void Login_enable_commands_and_configuration_over_the_pty()
    {
        Skip.IfNot(Simulator.CanRunPty, "requires Linux or WSL (pty) and Python");
        using var sim = Simulator.Start("configure.py", ["pty"], new Dictionary<string, string> { ["PTYFILE"] = "{dir}/pty.txt" }, waitFile: "{dir}/pty.txt");
        var masker = new SecretMasker();
        masker.Register("Pa55w0rd!", "En4ble", "NewComm2026");
        var transcriptPath = Path.Combine(sim.Dir, "console_session.txt");
        using (var channel = SerialChannel.Open(sim.ReadFile("pty.txt"), 9600, Translator.English, pty: true))
        using (var transcript = SessionTranscript.Create(transcriptPath, masker))
        {
            var s = new ConsoleSession(channel, Translator.English, masker, transcript);
            Assert.Equal("SW09", s.Connect("admin", "Pa55w0rd!", "En4ble"));
            s.RunCommand("terminal length 0", 20);
            Assert.Contains("WS-C2960X-24PD-L", s.RunCommand("show version"), StringComparison.Ordinal);

            var r = s.ApplyPlan(
                [new("lldp run"), new("snmp-server community NewComm2026 RO"), new("mls qos")], 3);
            Assert.Equal(["mls qos"], r.Errors.Select(e => e.Line));

            var running = s.RunCommand("show running-config");
            Assert.Contains("snmp-server community NewComm2026 RO", running, StringComparison.Ordinal);
        }
        var transcriptText = File.ReadAllText(transcriptPath);
        Assert.Contains("SW09#", transcriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("NewComm2026", transcriptText, StringComparison.Ordinal);

        // The simulator dumps its state to state.json: lldp applied, nothing saved.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        JsonElement sw09 = default;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                sw09 = JsonDocument.Parse(sim.ReadFile("state.json")).RootElement.GetProperty("SW09");
                if (sw09.GetProperty("lldp").GetBoolean())
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
            {
            }
            Thread.Sleep(300);
        }
        Assert.True(sw09.GetProperty("lldp").GetBoolean());
        Assert.False(sw09.GetProperty("saved").GetBoolean());
    }
}
