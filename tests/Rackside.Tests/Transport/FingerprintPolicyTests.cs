using Rackside.Core.Localization;
using Rackside.Transport.Console;
using Rackside.Transport.Ssh;

namespace Rackside.Tests.Transport;

/// <summary>Security rule 2: the SSH fingerprint policy, without network.</summary>
public sealed class FingerprintPolicyTests : IDisposable
{
    private const string KeyA = "9TFTk9ozu2FY0yLbjoxmKp+VaIaWw4vi59QyWG1e+/c";
    private const string KeyB = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rackside_fp_" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Title, string Text)> asked = [];
    private readonly List<string> logs = [];

    private FingerprintStore Store => new(Path.Combine(dir, "SSHFingerprints", "ssh_fingerprints.csv"));

    private static readonly PresentedKey PresentedA = new("rsa-sha2-512", KeyA);

    private SshTrust Confirm(FingerprintRequest r, PresentedKey p, bool answer) =>
        FingerprintPolicy.Confirm(r, p, Store, Translator.English, (text, title) => { asked.Add((title, text)); return answer; }, (l, m) => logs.Add(l + " " + m), "tester");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void First_use_asks_and_saves_only_when_accepted()
    {
        var r = new FingerprintRequest("10.0.0.1", 22) { Hostname = "SW1", Serial = "FOC1" };
        Assert.Throws<FingerprintRejectedException>(() => Confirm(r, PresentedA, answer: false));
        Assert.Empty(Store.Read());
        var trust = Confirm(r, PresentedA, answer: true);
        Assert.Equal("Unverified SSH fingerprint", asked[^1].Title);
        Assert.Contains("SHA256:" + KeyA, asked[^1].Text, StringComparison.Ordinal);
        Assert.Equal("First use, accepted by the operator", trust.How);
        var saved = Assert.Single(Store.Read());
        Assert.Equal(("10.0.0.1:22", "rsa-sha2-512", KeyA, "SW1", "FOC1", "tester"), (saved.Device, saved.Algorithm, saved.Fingerprint, saved.Hostname, saved.Serial, saved.Operator));
    }

    [Fact]
    public void Trust_first_use_accepts_without_asking_only_when_nothing_is_saved()
    {
        var r = new FingerprintRequest("10.0.0.1", 22) { TrustFirstUse = true };
        Confirm(r, PresentedA, answer: false);
        Assert.Empty(asked);
        Assert.StartsWith("WARN ", logs[^1], StringComparison.Ordinal);

        // Now one is saved: a different key ALWAYS asks, even in a sweep with "accept new".
        var ex = Assert.Throws<FingerprintRejectedException>(() => Confirm(r, new PresentedKey("rsa-sha2-512", KeyB), answer: false));
        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);
        Assert.Equal("The SSH fingerprint does not match", asked[^1].Title);
        Assert.Equal(KeyA, Assert.Single(Store.Read()).Fingerprint);
    }

    [Fact]
    public void A_matching_saved_key_does_not_ask()
    {
        Store.Save("10.0.0.1:22", "rsa-sha2-512", KeyA, "SW1", string.Empty, "x");
        var trust = Confirm(new FingerprintRequest("10.0.0.1", 22), PresentedA, answer: false);
        Assert.Empty(asked);
        Assert.Equal("Matches the saved one", trust.How);
        Assert.StartsWith("OK ", logs[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_key_is_replaced_only_on_override()
    {
        Store.Save("10.0.0.1:22", "rsa-sha2-512", KeyB, "SW1", "FOC1", "x");
        var trust = Confirm(new FingerprintRequest("10.0.0.1", 22), PresentedA, answer: true);
        Assert.Contains("man-in-the-middle", asked[^1].Text, StringComparison.Ordinal);
        Assert.Contains("THEY DO NOT MATCH.", asked[^1].Text, StringComparison.Ordinal);
        Assert.Equal("Operator OVERRIDE (expected SHA256:" + KeyB + ")", trust.How);
        Assert.Contains("Accepted by tester", logs[^1], StringComparison.Ordinal);
        Assert.Equal(KeyA, Assert.Single(Store.Read()).Fingerprint);
    }

    [Fact]
    public void The_typed_fingerprint_wins_over_the_saved_one()
    {
        Store.Save("10.0.0.1:22", "rsa-sha2-512", KeyB, "SW1", string.Empty, "x");
        var trust = Confirm(new FingerprintRequest("10.0.0.1", 22) { Expected = "SHA256:" + KeyA + "=" }, PresentedA, answer: false);
        Assert.Empty(asked);
        Assert.Equal("Matches the one entered by the operator", trust.How);
    }

    [Fact]
    public void An_md5_reference_cannot_be_compared_with_sha256_and_asks()
    {
        var r = new FingerprintRequest("10.0.0.1", 22) { Expected = "a:b:c:d:e:f:10:11:12:13:14:15:16:17:18:19" };
        Assert.Throws<FingerprintRejectedException>(() => Confirm(r, PresentedA, answer: false));
        Assert.Contains("another format (MD5)", asked[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Base64_case_matters()
    {
        Store.Save("10.0.0.1:22", "rsa-sha2-512", KeyA.ToLowerInvariant(), "SW1", string.Empty, "x");
        Assert.Throws<FingerprintRejectedException>(() => Confirm(new FingerprintRequest("10.0.0.1", 22), PresentedA, answer: false));
    }

    [Fact]
    public void The_key_accepted_in_this_operation_is_reused_without_asking_or_saving()
    {
        var accepted = new SshTrust("10.0.0.1:22", "10.0.0.1", 22, "rsa-sha2-512", KeyA, PresentedA.Normal, "First use, accepted by the operator");
        Assert.Same(accepted, Confirm(new FingerprintRequest("10.0.0.1", 22) { Accepted = accepted }, PresentedA, answer: false));
        Assert.Empty(asked);
        Assert.Empty(Store.Read());
    }

    [Fact]
    public void The_store_reads_the_csv_and_keeps_one_row_per_device()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store.Path)!);
        File.WriteAllText(Store.Path,
            "\uFEFF\"Device\",\"Algorithm\",\"Fingerprint\",\"Hostname\",\"Serial\",\"Date\",\"How\",\"Operator\"\r\n" +
            "\"10.0.0.1:22\",\"ssh-rsa\",\"" + KeyB + "\",\"SW1\",\"FOC1\",\"2026-01-01 10:00:00\",\"Operator OVERRIDE (expected \"\"x\"\")\",\"jose\"\r\n" +
            "\"10.0.0.2:22\",\"ssh-rsa\",\"" + KeyA + "\",\"SW2, IDF\",\"\",\"2026-01-02 10:00:00\",\"Matches the saved one\",\"jose\"\r\n");
        var rows = Store.Read();
        Assert.Equal("Operator OVERRIDE (expected \"x\")", rows[0].How);
        Assert.Equal("SW2, IDF", rows[1].Hostname);
        Store.Save("10.0.0.1:22", "rsa-sha2-512", KeyA, "SW1", "FOC1", "ok");
        rows = Store.Read();
        Assert.Equal(["10.0.0.2:22", "10.0.0.1:22"], rows.Select(r => r.Device));
        Assert.StartsWith("\"Device\",\"Algorithm\"", File.ReadAllText(Store.Path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SHA256:abc=", "SHA256", "abc")]
    [InlineData("ssh-rsa 2048 SHA256:abc", "SHA256", "abc")]
    [InlineData("MD5:A:b:0C:dd:e:f:10:11:12:13:14:15:16:17:18:19", "MD5", "0a:0b:0c:dd:0e:0f:10:11:12:13:14:15:16:17:18:19")]
    [InlineData("0a-0b-0c-dd-0e-0f-10-11-12-13-14-15-16-17-18-19", "MD5", "0a:0b:0c:dd:0e:0f:10:11:12:13:14:15:16:17:18:19")]
    public void Normalizes_fingerprints(string input, string type, string value) =>
        Assert.Equal(new NormalFingerprint(type, value), NormalFingerprint.Parse(input));
}
