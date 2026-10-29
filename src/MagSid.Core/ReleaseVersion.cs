using System.Globalization;

namespace MagSid.Core;

/// <summary>
/// MagSID's release numbers, as the releases are tagged: v1.6, v1.61, v1.62, v1.63 - decimals,
/// not version parts, so 1.6 comes after 1.53 and before 1.61. Compared as such, a release on
/// the website is newer than the editor running or it is not.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>The number in a tag or version string - "v1.63", "1.63", "1.63+3475d9a" - or null
    /// when there is none.</summary>
    public static decimal? Parse(string? text)
    {
        string number = (text ?? "").Trim().TrimStart('v', 'V').Split('+', '-', ' ')[0];
        return decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : null;
    }

    /// <summary>The number of an assembly's version: 1.63 for 1.63.0.0, 1.6 for 1.6.0.0.</summary>
    public static decimal? Of(Version? version) => version is null ? null : Parse($"{version.Major}.{version.Minor}");

    /// <summary>True when <paramref name="latest"/> is a later release than <paramref name="current"/>;
    /// false when either is not a release number.</summary>
    public static bool IsNewer(string? latest, decimal? current) =>
        Parse(latest) is { } released && current is { } running && released > running;
}
