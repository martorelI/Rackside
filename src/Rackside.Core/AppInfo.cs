namespace Rackside.Core;

/// <summary>Product identity shown in the window, the About section and the HTML records.</summary>
public static class AppInfo
{
    public const string Name = "Rackside";
    public const string Author = "José Martorell";
    public const string SupportEmail = "support@martorell.mx";

    /// <summary>Shown in the records and About; keep it equal to VersionPrefix in Directory.Build.props (a test checks it).</summary>
    public const string Version = "1.1.2";
}
