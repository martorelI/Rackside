namespace Rackside.Tests;

/// <summary>Locates files of the repository (snapshots, fixtures, simulators).</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rackside.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Rackside.slnx not found above " + AppContext.BaseDirectory);
    }
}
