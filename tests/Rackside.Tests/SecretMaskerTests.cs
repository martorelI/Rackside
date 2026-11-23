using Rackside.Core.Security;

namespace Rackside.Tests;

/// <summary>Secrets hidden in everything written to disk (security rule 3: passwords and secrets are masked).</summary>
public class SecretMaskerTests
{
    [Theory]
    [InlineData("enable secret 5 $1$abcd$efgh", "enable secret 5 ********")]
    [InlineData("username admin privilege 15 secret 9 $9$xyz", "username admin privilege 15 secret 9 ********")]
    [InlineData("snmp-server community Public123 RO 10", "snmp-server community ******** RO 10")]
    [InlineData("snmp-server host 10.0.0.50 version 2c Comm1", "snmp-server host 10.0.0.50 version 2c ********")]
    [InlineData("snmp-server user mon GRP v3 auth sha AuthK priv aes 128 PrivK", "snmp-server user mon GRP v3 auth sha ******** priv aes 128 ********")]
    [InlineData(" key-string 7 0822455D0A16", " key-string 7 ********")]
    [InlineData("Password: hunter2", "Password: ********")]
    [InlineData("service password-encryption", "service password-encryption")]
    [InlineData("crypto key generate rsa modulus 2048", "crypto key generate rsa modulus 2048")]
    public void Keyword_secrets_are_hidden(string input, string expected) =>
        Assert.Equal(expected, new SecretMasker().Protect(input));

    [Fact]
    public void Typed_values_are_hidden_anywhere()
    {
        var m = new SecretMasker();
        m.Register("S3cret!x", null, "");
        Assert.Equal("login with ******** done", m.Protect("login with S3cret!x done"));
    }

    [Fact]
    public void Very_short_typed_values_are_only_hidden_as_whole_words()
    {
        var m = new SecretMasker();
        m.Register("abc");
        Assert.Equal("value ******** but abcd stays", m.Protect("value abc but abcd stays"));
    }

    [Fact]
    public void Masking_twice_changes_nothing()
    {
        var once = new SecretMasker().Protect("enable secret 5 $1$x");
        Assert.Equal(once, new SecretMasker().Protect(once));
    }
}
