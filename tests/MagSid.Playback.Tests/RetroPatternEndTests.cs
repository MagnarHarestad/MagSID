using System.Reflection;
using System.Windows.Forms;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Editor.App.Controls;

namespace MagSid.Playback.Tests;

/// <summary>
/// Shift+End ends the pattern after the cursor's row, which is where the channel's RST then shows.
/// On a loop's later pass that is refused - except on the loop's last row on its last pass, where
/// the rows after the loop begin: they go, and the loop stays whole.
/// </summary>
public class RetroPatternEndTests
{
    /// <summary>A loop of five rows played three times - grid rows 0-14 - and a hold after it, row 15.</summary>
    private const string Text = """
        var v_Song_Speed = 6

        INSTRUMENTS:
        01, $11, $00, $f0
        END:

        PAT01:
        "A-4", 01
        "...", 00
        "D-4", 01
        "...", 00
        "E-4", 01
        LE03
        "...", 00
        END:

        ORDER_CH1:
        1
        END:

        ORDER_CH2:
        1
        END:

        ORDER_CH3:
        1
        END:
        """;

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static (TrackerGridControl Grid, Song Song) GridAt(int row)
    {
        var song = PatternsFileReader.Parse(Text);
        var grid = new TrackerGridControl { Size = new System.Drawing.Size(900, 600) };
        grid.SetSong(song);
        typeof(TrackerGridControl).GetField("_cursorRow", Hidden)!.SetValue(grid, row);
        return (grid, song);
    }

    private static void ShiftEnd(TrackerGridControl grid) =>
        typeof(TrackerGridControl).GetMethod("OnKeyDown", Hidden, [typeof(KeyEventArgs)])!
            .Invoke(grid, [new KeyEventArgs(Keys.Shift | Keys.End)]);

    [Fact]
    public void On_the_last_row_of_a_loops_last_pass_it_cuts_what_comes_after_the_loop()
    {
        var (grid, song) = GridAt(14);

        ShiftEnd(grid);

        var rows = song.Patterns[0].Rows;
        Assert.Equal(6, rows.Count);
        Assert.Equal(new LoopEndRow(3), rows[^1]);
    }

    [Fact]
    public void On_any_other_row_of_a_later_pass_it_is_refused()
    {
        var (grid, song) = GridAt(12);
        string? notice = null;
        grid.Notice += text => notice = text;

        ShiftEnd(grid);

        Assert.Equal(7, song.Patterns[0].Rows.Count);
        Assert.NotNull(notice);
    }

    [Fact]
    public void On_the_first_pass_it_cuts_after_the_row_as_before()
    {
        var (grid, song) = GridAt(2);

        ShiftEnd(grid);

        Assert.Equal(3, song.Patterns[0].Rows.Count);
    }
}
