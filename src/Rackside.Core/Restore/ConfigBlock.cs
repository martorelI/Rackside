namespace Rackside.Core.Restore;

/// <summary>A top-level configuration line with its sublines, or a banner with its text.</summary>
public sealed class ConfigBlock
{
    public ConfigBlock(string header, IEnumerable<string>? children = null, bool isBanner = false,
        IEnumerable<string>? bannerText = null, string bannerType = "")
    {
        Header = header;
        Children = children is null ? [] : [.. children];
        IsBanner = isBanner;
        BannerText = bannerText is null ? [] : [.. bannerText];
        BannerType = bannerType;
    }

    public string Header { get; set; }
    public List<string> Children { get; set; }
    public bool IsBanner { get; }
    public List<string> BannerText { get; }
    public string BannerType { get; }

    /// <summary>Character that delimits the banner when it is sent (chosen so it is not in the text).</summary>
    public string Delimiter { get; set; } = string.Empty;

    /// <summary>Copy with its own lists.</summary>
    public ConfigBlock Clone() => new(Header, Children, IsBanner, BannerText, BannerType);
}
