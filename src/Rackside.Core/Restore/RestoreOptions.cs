namespace Rackside.Core.Restore;

/// <summary>The data the operator confirms or edits before generating the configuration.</summary>
public sealed class RestoreOptions
{
    public string ModelSource { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string InterfaceManagement { get; set; } = string.Empty;
    public string IP { get; set; } = string.Empty;
    public string Mask { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public string UserAdmin { get; set; } = string.Empty;
    public string PasswordAdmin { get; set; } = string.Empty;
    public string EnableSecret { get; set; } = string.Empty;
    public bool CreateVlans { get; set; } = true;
    public bool RemoveCertificate { get; set; } = true;
    public bool IncludeUsers { get; set; }
    /// <summary>ExtraAllowedCommands from settings.json (regexes; they never skip the block list).</summary>
    public IReadOnlyList<string> ExtraAllowedCommands { get; set; } = [];

    /// <summary>The values proposed when a backup is loaded.</summary>
    public static RestoreOptions FromSource(SourceConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var o = new RestoreOptions
        {
            ModelSource = source.Model.Trim(),
            Hostname = source.Hostname.Trim(),
            Domain = source.Domain.Trim(),
            Gateway = source.Gateway.Trim(),
            UserAdmin = source.UserAdmin.Trim(),
            InterfaceManagement = "Vlan1",
        };
        var picked = source.Svis.FirstOrDefault(s => Ci.Eq(s.Name, "Vlan1") && s.IP.Length > 0)
            ?? source.Svis.FirstOrDefault(s => s.IP.Length > 0);
        if (picked is not null)
        {
            o.InterfaceManagement = picked.Name;
            o.IP = picked.IP;
            o.Mask = picked.Mask;
        }
        return o;
    }
}
