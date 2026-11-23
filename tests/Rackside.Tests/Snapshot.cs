using System.Text;

namespace Rackside.Tests;

/// <summary>
/// Expected outputs in tests/snapshots (generated configuration, change report, interface mapping, push
/// preview and lines, update plans, HTML records). A test fails at the first line that differs. When a
/// change of behavior is intentional, regenerate them with RACKSIDE_UPDATE_SNAPSHOTS=1 and review the diff.
/// </summary>
internal static class Snapshot
{
    private static readonly bool Update = Environment.GetEnvironmentVariable("RACKSIDE_UPDATE_SNAPSHOTS") == "1";

    public static string PathOf(params string[] parts) => RepoPaths.Combine(["tests", "snapshots", .. parts]);

    public static void Match(string actual, params string[] parts)
    {
        var file = PathOf(parts);
        var what = string.Join('/', parts);
        if (Update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, actual, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(file), $"{what} is missing (RACKSIDE_UPDATE_SNAPSHOTS=1 creates it)");
        var expected = File.ReadAllText(file);
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }
        if (string.Equals(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal))
        {
            Assert.Fail($"{what}: only the line endings differ (CRLF / LF). The checkout converted them: see .gitattributes.");
        }
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var i = 0;
        while (i < Math.Min(e.Length, a.Length) && string.Equals(e[i], a[i], StringComparison.Ordinal))
        {
            i++;
        }
        Assert.Fail($"{what} differs from line {i + 1}:\n  expected: {(i < e.Length ? e[i] : "<end>")}\n  actual:   {(i < a.Length ? a[i] : "<end>")}");
    }
}
