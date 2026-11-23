namespace Rackside.Core.Restore;

/// <summary>The output of "dir": filesystem (flash, usbflash0...) and entries.</summary>
public sealed record DirListing(string Filesystem, IReadOnlyList<DirEntry> Entries)
{
    public static DirListing Parse(string? text)
    {
        var fs = Ci.TryMatch(text, "(?im)Directory of\\s+([A-Za-z0-9\\-]+):", out var m) ? m.Groups[1].Value : string.Empty;
        var entries = new List<DirEntry>();
        foreach (var l in Ci.Split(text, "\\r?\\n"))
        {
            if (Ci.TryMatch(l, "^\\s*\\d+\\s+([d-][rwx-]+)\\s+(\\d+)\\s+.*\\s(\\S+)\\s*$", out var md))
            {
                entries.Add(new DirEntry(md.Groups[3].Value, md.Groups[1].Value.StartsWith('d')));
            }
        }
        return new DirListing(fs, entries);
    }
}
