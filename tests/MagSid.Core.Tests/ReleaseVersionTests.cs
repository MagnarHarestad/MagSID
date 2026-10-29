using MagSid.Core;

namespace MagSid.Core.Tests;

/// <summary>Release numbers as MagSID tags them - decimals - and which is newer.</summary>
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.63", 1.63)]
    [InlineData("1.6", 1.6)]
    [InlineData("v1.63+3475d9a", 1.63)]
    [InlineData(" V2.0 ", 2.0)]
    public void A_tag_or_version_is_its_number(string text, double number) =>
        Assert.Equal((decimal)number, ReleaseVersion.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xplatform-v0.6")]
    [InlineData("latest")]
    public void Text_that_is_no_release_number_is_none(string? text) => Assert.Null(ReleaseVersion.Parse(text));

    [Theory]
    [InlineData("v1.64", 1.63, true)]
    [InlineData("v1.6", 1.53, true)]
    [InlineData("v1.63", 1.63, false)]
    [InlineData("v1.53", 1.6, false)]
    [InlineData("v2.0", 1.63, true)]
    [InlineData("nightly", 1.63, false)]
    public void A_release_is_newer_by_its_number_as_a_decimal(string latest, double running, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.IsNewer(latest, (decimal)running));

    [Fact]
    public void An_assemblys_version_is_read_as_the_release_it_is()
    {
        Assert.Equal(1.63m, ReleaseVersion.Of(new Version(1, 63, 0, 0)));
        Assert.Equal(1.6m, ReleaseVersion.Of(new Version(1, 6)));
        Assert.Null(ReleaseVersion.Of(null));
    }
}
