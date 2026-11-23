using System.Security.Cryptography;
using Rackside.Core.Localization;
using Rackside.Core.Security;
using Rackside.Transport.Console;

namespace Rackside.Tests.Transport;

/// <summary>Xmodem from the 2960-X boot loader against sim/console_xmodem_2960.py (pty).</summary>
public class XmodemSimulatorTests
{
    [SkippableFact]
    public void Copies_a_file_from_the_boot_loader()
    {
        Skip.IfNot(Simulator.CanRunPty, "requires Linux or WSL (pty) and Python");
        using var sim = Simulator.Start("console_xmodem_2960.py", [], new Dictionary<string, string> { ["PTYFILE"] = "{dir}/pty.txt" }, waitFile: "{dir}/pty.txt");
        var t = Translator.English;
        using var channel = SerialChannel.Open(sim.ReadFile("pty.txt"), 9600, t, pty: true);
        var s = new ConsoleSession(channel, t, new SecretMasker());
        Assert.Equal("SW07", s.Connect("admin", "x", "y"));

        // To the boot loader: boot manual + reload without saving.
        s.SendConfigLine("configure terminal");
        s.SendConfigLine("boot manual");
        s.SendConfigLine("end");
        s.SendLine("reload");
        Assert.Equal("SAVE", s.Wait([("SAVE", "(?i)Save\\? \\[yes/no\\]:\\s*$")], 20)!.Key);
        s.SendLine("no");
        Assert.Equal("CONFIRM", s.Wait([("CONFIRM", "\\[confirm\\]\\s*$")], 20)!.Key);
        s.SendLine(string.Empty);
        Assert.Equal("BL", s.Wait([("BL", "(?:^|\\n)\\s*switch:\\s*$")], 30)!.Key);

        var data = Enumerable.Range(0, 20000).Select(i => (byte)(i % 251)).ToArray();
        s.SendLine("copy xmodem: flash:test.bin");
        s.Wait([("BEGIN", "(?i)Begin the Xmodem")], 20);
        var r = Xmodem.Send(channel, data, t);
        Assert.Equal(20, r.Blocks);
        Assert.Equal(1024, r.BlockSize);
        Assert.Equal("OK", s.Wait([("OK", "successfully copied to \"flash:test\\.bin\"[\\s\\S]*switch:\\s*$")], 30)!.Key);

        var padded = data.Concat(Enumerable.Repeat((byte)0x1A, 20 * 1024 - data.Length)).ToArray();
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(padded)), r.Md5Padding);
        Assert.Contains($"received {padded.Length} bytes", File.ReadAllText(Path.Combine(sim.Dir, "console_xmodem_2960.log")), StringComparison.Ordinal);
    }
}
