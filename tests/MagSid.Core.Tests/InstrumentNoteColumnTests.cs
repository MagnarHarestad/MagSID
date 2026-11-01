using MagSid.Core.Model;

namespace MagSid.Core.Tests;

public class InstrumentNoteColumnTests
{
    private const string Root = "ROOT";

    [Theory]
    [InlineData("|C-4|", "C-4|C-4|")]                       // the report: frame 2 of 3, nothing before it
    [InlineData("||E-4", "E-4|E-4|E-4")]
    [InlineData("|C-4|E-4", "C-4|C-4|E-4")]
    [InlineData("C-4||E-4", "C-4|C-4|E-4")]                 // a gap holds the note before it
    [InlineData("C-4|||G-4", "C-4|C-4|C-4|G-4")]
    [InlineData("|C-4||E-4||", "C-4|C-4|C-4|E-4||")]
    public void A_blank_frame_ahead_of_a_later_note_gets_a_note(string typed, string expected)
    {
        Assert.Equal(expected, string.Join('|', InstrumentNoteColumn.FillGaps(typed.Split('|'), Root)));
    }

    [Theory]
    [InlineData("C-4|E-4|G-4")]
    [InlineData("C-4||")]                                   // the column ends after its last note
    [InlineData("||")]                                      // no notes at all is no note program
    [InlineData("")]
    public void A_column_with_no_gaps_is_left_as_it_is(string typed)
    {
        Assert.Equal(typed, string.Join('|', InstrumentNoteColumn.FillGaps(typed.Split('|'), Root)));
    }

    [Theory]
    [InlineData("C-4|ROOT|", "C-4|ROOT|")]
    [InlineData("C-4||ROOT", "C-4||ROOT")]                  // nothing later to reach, so no gap
    [InlineData("|C-4|ROOT", "C-4|C-4|ROOT")]
    [InlineData("ROOT||E-4", "ROOT||E-4")]                  // past the end: left for the refusal
    [InlineData("C-4|ROOT||E-4", "C-4|ROOT||E-4")]
    public void Nothing_from_the_end_marker_on_is_touched(string typed, string expected)
    {
        Assert.Equal(expected, string.Join('|', InstrumentNoteColumn.FillGaps(typed.Split('|'), Root)));
    }

    [Fact]
    public void An_empty_table_stays_empty()
    {
        Assert.Empty(InstrumentNoteColumn.FillGaps([], Root));
    }
}
