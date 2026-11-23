using System.Text;
using Rackside.Core;

namespace Rackside.Transport.Ssh;

/// <summary>A fingerprint ready to compare: SHA256 (base64 without "=") or MD5 (aa:bb:... lower case).</summary>
public sealed record NormalFingerprint(string Type, string Value)
{
    public static NormalFingerprint Empty { get; } = new(string.Empty, string.Empty);

    /// <summary>SHA256 in base64 (with or without "SHA256:" and "=") or hexadecimal MD5 (aa:bb:... with or without zeros).</summary>
    public static NormalFingerprint Parse(string? fingerprint)
    {
        var h = (fingerprint ?? string.Empty).Trim();
        if (h.Length == 0)
        {
            return Empty;
        }
        h = Ci.Replace(h, "^(?i)(ssh-\\S+|ecdsa-\\S+|rsa-\\S+)\\s+(\\d+\\s+)?", string.Empty);
        if (Ci.IsMatch(h, "^(?i)MD5:"))
        {
            h = h[4..];
        }
        if (Ci.IsMatch(h, "^([0-9a-fA-F]{1,2}[:\\-]){15}[0-9a-fA-F]{1,2}$"))
        {
            return new("MD5", string.Join(':', Ci.Split(h, "[:\\-]").Select(x => x.ToLowerInvariant().PadLeft(2, '0'))));
        }
        return new("SHA256", Ci.Replace(h, "^(?i)SHA256:", string.Empty).TrimEnd('='));
    }

    /// <summary>Same type and exactly the same value (case matters in base64).</summary>
    public bool SameAs(NormalFingerprint other) =>
        other is not null && Ci.Eq(Type, other.Type) && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override string ToString() => Ci.Eq(Type, "SHA256") ? "SHA256:" + Value : "MD5:" + Value;
}

/// <summary>A row of SSHFingerprints/ssh_fingerprints.csv.</summary>
public sealed record SavedFingerprint(string Device, string Algorithm, string Fingerprint, string Hostname, string Serial, string Date, string How, string Operator);

/// <summary>
/// SSHFingerprints/ssh_fingerprints.csv (every field quoted, UTF-8). One row per device (ip:port).
/// </summary>
public sealed class FingerprintStore(string path)
{
    private static readonly string[] Columns = ["Device", "Algorithm", "Fingerprint", "Hostname", "Serial", "Date", "How", "Operator"];

    public string Path { get; } = path;

    public IReadOnlyList<SavedFingerprint> Read()
    {
        if (!File.Exists(Path))
        {
            return [];
        }
        var rows = Csv.Parse(File.ReadAllText(Path, Encoding.UTF8));
        if (rows.Count == 0)
        {
            return [];
        }
        var header = rows[0];
        int Col(string name) => header.FindIndex(h => Ci.Eq(h, name));
        var idx = Columns.Select(Col).ToArray();
        string Get(List<string> r, int i) => idx[i] >= 0 && idx[i] < r.Count ? r[idx[i]] : string.Empty;
        return rows.Skip(1).Where(r => r.Count > 1 || (r.Count == 1 && r[0].Length > 0))
            .Select(r => new SavedFingerprint(Get(r, 0), Get(r, 1), Get(r, 2), Get(r, 3), Get(r, 4), Get(r, 5), Get(r, 6), Get(r, 7)))
            .ToList();
    }

    /// <summary>The last row saved for the device, or null.</summary>
    public SavedFingerprint? Find(string device) => Read().LastOrDefault(f => Ci.Eq(f.Device, device));

    /// <summary>Replaces the device's row.</summary>
    public void Save(string device, string algorithm, string fingerprint, string hostname, string serial, string how, DateTime? now = null, string? user = null)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var all = Read().Where(f => !Ci.Eq(f.Device, device)).ToList();
        all.Add(new SavedFingerprint(device, algorithm, fingerprint, hostname, serial,
            (now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), how, user ?? Environment.UserName));
        var sb = new StringBuilder();
        sb.Append(Csv.Line(Columns)).Append("\r\n");
        foreach (var f in all)
        {
            sb.Append(Csv.Line([f.Device, f.Algorithm, f.Fingerprint, f.Hostname, f.Serial, f.Date, f.How, f.Operator])).Append("\r\n");
        }
        File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(true));
    }
}
