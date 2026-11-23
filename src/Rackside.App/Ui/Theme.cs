namespace Rackside.App.Ui;

/// <summary>Colors and fonts. The accent is the same blue as the HTML records.</summary>
internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(0x1E, 0x6B, 0xB8);
    public static readonly Color AccentDark = Color.FromArgb(0x15, 0x55, 0x96);
    public static readonly Color AccentSoft = Color.FromArgb(0xE8, 0xF1, 0xFA);
    public static readonly Color Sidebar = Color.FromArgb(0x13, 0x1A, 0x24);
    public static readonly Color SidebarHover = Color.FromArgb(0x1F, 0x29, 0x37);
    public static readonly Color SidebarText = Color.FromArgb(0xC9, 0xD1, 0xDC);
    public static readonly Color Background = Color.FromArgb(0xF3, 0xF5, 0xF8);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE1, 0xE5, 0xEB);
    public static readonly Color TextColor = Color.FromArgb(0x1F, 0x29, 0x37);
    public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);
    public static readonly Color Danger = Color.FromArgb(0xB0, 0x00, 0x20);
    public static readonly Color DangerSoft = Color.FromArgb(0xFD, 0xEC, 0xEE);
    public static readonly Color Success = Color.FromArgb(0x1E, 0x7B, 0x34);
    public static readonly Color WarningColor = Color.FromArgb(0xB3, 0x6B, 0x00);
    public static readonly Color WarningSoft = Color.FromArgb(0xFF, 0xF4, 0xE0);
    public static readonly Color Disabled = Color.FromArgb(0xE9, 0xEC, 0xF0);
    public static readonly Color DisabledText = Color.FromArgb(0xA0, 0xA6, 0xB0);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Title = new("Segoe UI Semibold", 17f);
    public static readonly Font Section = new("Segoe UI Semibold", 11f);
    public static readonly Font Nav = new("Segoe UI", 10.5f);
    public static readonly Font Mono = CreateMono();

    private static Font CreateMono()
    {
        using var probe = new Font("Cascadia Mono", 9.25f);
        return probe.Name == "Cascadia Mono" ? new Font("Cascadia Mono", 9.25f) : new Font("Consolas", 9.5f);
    }

    public static Image? Logo { get; } = LoadLogo();

    private static Image? LoadLogo()
    {
        var s = typeof(Theme).Assembly.GetManifestResourceStream("Rackside.App.logo.png");
        return s is null ? null : Image.FromStream(s);
    }
}
