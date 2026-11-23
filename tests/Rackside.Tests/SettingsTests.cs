using System.Text.Json.Nodes;
using Rackside.Core.Settings;

namespace Rackside.Tests;

/// <summary>Reading, validating and saving settings.json.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rackside_settings_" + Guid.NewGuid().ToString("N"));

    private string File_ => AppSettings.PathFor(dir);

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private void Json(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, text);
    }

    [Fact]
    public void No_file_creates_it_with_the_defaults()
    {
        var s = AppSettings.Read(File_);
        Assert.True(File.Exists(File_));
        Assert.Equal("en", s.Language);
        Assert.Empty(s.Warnings);
        Assert.Equal("en", JsonNode.Parse(File.ReadAllText(File_))!["Language"]!.GetValue<string>());
        var p = DataPaths.From(dir, s);
        Assert.Equal(Path.Combine(dir, "Inventory", "switches.csv"), p.InventoryCsv);
        Assert.Equal(Path.Combine(dir, "SSHFingerprints", "ssh_fingerprints.csv"), p.Fingerprints);
    }

    [Fact]
    public void Custom_values()
    {
        Json("""{ "language": "ES", "DataFolder": "data", "Folders": { "Pushes": "Changes" }, "PreferredCOMPort": "com7", "Push": { "TimeZone": "PST" }, "ExtraAllowedCommands": ["^device-sensor\\s"] }""");
        var s = AppSettings.Read(File_);
        Assert.Empty(s.Warnings);
        Assert.Equal("es", s.Language);
        Assert.Equal("COM7", s.PreferredCOMPort);
        Assert.Equal("PST", s.Push.TimeZone);
        Assert.Equal(["^device-sensor\\s"], s.ExtraAllowedCommands);
        Assert.Equal(Path.Combine(dir, "data", "Changes"), DataPaths.From(dir, s).Pushes);
    }

    [Fact]
    public void Invalid_values_use_the_default_and_are_reported()
    {
        Json("""{ "Language": "fr", "Folders": { "Restores": "a/b" }, "PreferredCOMPort": "tty0", "ExtraAllowedCommands": ["(", "ok"] }""");
        var s = AppSettings.Read(File_);
        Assert.Equal("en", s.Language);
        Assert.Equal("Restores", s.Folders.Restores);
        Assert.Equal("COM3", s.PreferredCOMPort);
        Assert.Equal(["ok"], s.ExtraAllowedCommands);
        Assert.Equal(4, s.Warnings.Count);
    }

    [Fact]
    public void Broken_json_uses_defaults_and_keeps_the_file()
    {
        Json("{ broken");
        var s = AppSettings.Read(File_);
        Assert.Single(s.Warnings);
        Assert.Equal("{ broken", File.ReadAllText(File_));
    }

    [Fact]
    public void Saving_the_language_keeps_the_rest()
    {
        Json("""{ "Language": "en", "Custom": 5, "Push": { "TimeZone": "PST" } }""");
        AppSettings.SaveLanguage(File_, "es");
        var j = JsonNode.Parse(File.ReadAllText(File_))!;
        Assert.Equal("es", j["Language"]!.GetValue<string>());
        Assert.Equal(5, j["Custom"]!.GetValue<int>());
        Assert.Equal("PST", j["Push"]!["TimeZone"]!.GetValue<string>());
    }
}
