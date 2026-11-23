namespace Rackside.Core;

/// <summary>Input checks shared by the sections.</summary>
public static class Validators
{
    /// <summary>Dotted IPv4 address with every octet 0-255.</summary>
    public static bool IsIPv4(string? value)
    {
        if (!Ci.TryMatch(value, "^(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})$", out var m))
        {
            return false;
        }
        for (var o = 1; o <= 4; o++)
        {
            if (int.Parse(m.Groups[o].Value, System.Globalization.CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A valid IPv4 subnet mask (contiguous ones).</summary>
    public static bool IsMask(string? value)
    {
        if (!IsIPv4(value))
        {
            return false;
        }
        var b = System.Net.IPAddress.Parse(value!).GetAddressBytes();
        var n = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        var inv = ~n;
        return ((inv + 1) & inv) == 0;
    }
}
