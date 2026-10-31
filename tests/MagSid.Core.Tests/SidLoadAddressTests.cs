namespace MagSid.Core.Tests;

/// <summary>
/// The rules governing where an exported .sid may load. These matter because getting one wrong
/// produces a file that looks fine and plays silence: a tune overlapping $D000-$DFFF has its
/// middle written into the VIC and SID chips instead of RAM, and nothing about the .sid itself
/// says so.
/// </summary>
public class SidLoadAddressTests
{
    [Theory]
    [InlineData(0x02, 48)]
    [InlineData(0x20, 48)]
    [InlineData(0xD0, 48)]      // ends exactly at $FF
    [InlineData(0xAC, 80)]
    public void A_zero_page_block_that_fits_is_accepted(int first, int bytes)
    {
        Assert.Null(SidLoadAddress.ValidateZeroPage(first, bytes));
    }

    [Theory]
    [InlineData(0x00, 48, "$02")]
    [InlineData(0x01, 48, "$02")]
    [InlineData(0xD1, 48, "$D0")]
    [InlineData(0xC0, 80, "$B0")]
    [InlineData(0x100, 48, "$D0")]
    public void A_zero_page_block_that_does_not_fit_is_refused_saying_what_would(int first, int bytes, string fits)
    {
        Assert.Contains(fits, SidLoadAddress.ValidateZeroPage(first, bytes));
    }

    /// <summary>Around BellaCiao's real size, so the ranges here are the ones a song actually
    /// covers rather than a convenient round number.</summary>
    private const int TypicalSong = 1051;

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x2000)]
    [InlineData(0x4400)]
    [InlineData(0x0400)]        // the lowest address allowed at all
    [InlineData(0xCB00)]        // ends at $CF1A, one page clear of I/O
    public void AcceptsUsableAddresses(int address)
    {
        Assert.Null(SidLoadAddress.Validate(address, TypicalSong));
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0020)]        // where the player's own zero-page block sits
    [InlineData(0x0100)]        // the stack
    [InlineData(0x03FF)]
    public void RejectsAddressesBelowUsableRam(int address)
    {
        Assert.NotNull(SidLoadAddress.Validate(address, TypicalSong));
    }

    [Fact]
    public void RejectsAnAddressInsideIo()
    {
        Assert.NotNull(SidLoadAddress.Validate(0xD400, TypicalSong));
    }

    /// <summary>The address itself is in RAM, which is exactly why this case needs its own test:
    /// only the length reveals that the tail lands in the SID chip.</summary>
    [Fact]
    public void RejectsAnAddressWhoseTailReachesIo()
    {
        Assert.Null(SidLoadAddress.Validate(0xCB00, TypicalSong));
        Assert.NotNull(SidLoadAddress.Validate(0xCC00, TypicalSong));
    }

    [Fact]
    public void RejectsATuneThatWouldRunPastTheTopOfMemory()
    {
        Assert.Null(SidLoadAddress.Validate(0x10000 - TypicalSong, TypicalSong));
        Assert.NotNull(SidLoadAddress.Validate(0x10001 - TypicalSong, TypicalSong));
    }

    [Theory]
    [InlineData("$2000", 0x2000)]
    [InlineData("2000", 0x2000)]
    [InlineData("0x2000", 0x2000)]
    [InlineData("0X4400", 0x4400)]
    [InlineData("  $abc  ", 0x0ABC)]
    [InlineData("F", 0x000F)]
    public void ParsesTheAddressFormsThisProjectUses(string text, int expected)
    {
        Assert.Equal(expected, SidLoadAddress.TryParse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$")]
    [InlineData("12345")]       // more than 16 bits
    [InlineData("$G000")]
    [InlineData("4 400")]
    [InlineData(null)]
    public void RejectsTextThatIsNotA16BitHexNumber(string? text)
    {
        Assert.Null(SidLoadAddress.TryParse(text));
    }
}
