using System.Reflection;
using System.Windows.Forms;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Editor.App;
using MagSid.Editor.App.Controls;

namespace MagSid.Playback.Tests;

/// <summary>
/// Retro's selection: drag with the left button over one channel's rows, Ctrl+C copies them as the
/// grid shows them, Ctrl+V writes them from the cursor down - across patterns, growing the last.
/// Driven through the grid's own mouse and key handlers.
/// </summary>
public class RetroCopyPasteTests
{
    private const string Text = """
        var v_Song_Speed = 2

        INSTRUMENTS:
        01, $41, $00, $f7
        02, $21, $00, $f7
        END:

        PAT01:
        "C-4", 01
        "D-4", 01
        "E-4", 02
        "F-4", 02
        END:

        PAT02:
        "G-4", 01
        "...", 00
        "...", 00
        "---", 00
        END:

        ORDER_CH1:
        1, 2
        END:

        ORDER_CH2:
        2
        END:

        ORDER_CH3:
        2
        END:
        """;

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static int Constant(string name) =>
        (int)typeof(TrackerGridControl).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static void Call(TrackerGridControl grid, string handler, EventArgs args) =>
        typeof(TrackerGridControl).GetMethod(handler, Hidden, [args.GetType()])!.Invoke(grid, [args]);

    private static (TrackerGridControl Grid, Song Song) Grid()
    {
        var song = PatternsFileReader.Parse(Text);
        var grid = new TrackerGridControl { Size = new System.Drawing.Size(900, 600) };
        grid.SetSong(song);
        return (grid, song);
    }

    /// <summary>A point in a channel's note field, on a grid row as the view now shows it.</summary>
    private static (int X, int Y) At(TrackerGridControl grid, int channel, int row)
    {
        int top = (int)typeof(TrackerGridControl).GetProperty("TopRow", Hidden)!.GetValue(grid)!;
        int x = Constant("RowNumWidth") + (channel * Constant("ChannelColWidth")) + Constant("OrderColWidth")
                + Constant("MarkerColWidth") + 4;
        int y = Constant("HeaderHeight") + ((row - top) * Constant("RowHeight")) + 2;
        return (x, y);
    }

    private static void Click(TrackerGridControl grid, int channel, int row)
    {
        var (x, y) = At(grid, channel, row);
        Call(grid, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        Call(grid, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    }

    private static void Drag(TrackerGridControl grid, int channel, int from, int to)
    {
        var (x, y) = At(grid, channel, from);
        Call(grid, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        // What OnMouseMove does with the left button held; OnMouseMove itself also sets the pointer's
        // shape, which needs a window.
        var (_, toY) = At(grid, channel, to);
        typeof(TrackerGridControl).GetMethod("DragTo", Hidden)!.Invoke(grid, [toY]);
        Call(grid, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, x, toY, 0));
    }

    private static void Key(TrackerGridControl grid, Keys keys) => Call(grid, "OnKeyDown", new KeyEventArgs(keys));

    private static (int Channel, int From, int To)? Selection(TrackerGridControl grid) =>
        ((int Channel, int From, int To)?)typeof(TrackerGridControl).GetField("_selection", Hidden)!.GetValue(grid);

    [Fact]
    public void A_drag_selects_rows_of_one_channel_and_a_click_clears_them()
    {
        var (grid, _) = Grid();

        Drag(grid, 0, 3, 1);
        Assert.Equal((0, 3, 1), Selection(grid));

        Click(grid, 1, 2);
        Assert.Null(Selection(grid));
    }

    [Fact]
    public void Ctrl_C_copies_the_rows_as_the_grid_shows_them_across_patterns()
    {
        var (grid, song) = Grid();

        Drag(grid, 0, 2, 5);
        Key(grid, Keys.Control | Keys.C);

        var first = song.Patterns[0].Rows;
        var second = song.Patterns[1].Rows;
        Assert.Equal([first[2], first[3], second[0], second[1]], PatternClipboard.SelectedRows);
    }

    [Fact]
    public void Ctrl_V_writes_them_from_the_cursor_down_and_selects_what_it_wrote()
    {
        var (grid, song) = Grid();
        Drag(grid, 0, 0, 1);
        Key(grid, Keys.Control | Keys.C);

        Click(grid, 1, 1);
        Key(grid, Keys.Control | Keys.V);

        var pasted = song.Patterns[1].Rows;
        Assert.Equal(new NoteRow(NoteName.Parse("C-4"), 1), pasted[1]);
        Assert.Equal(new NoteRow(NoteName.Parse("D-4"), 1), pasted[2]);
        Assert.Equal((1, 1, 2), Selection(grid));
    }

    [Fact]
    public void A_paste_past_the_end_grows_the_last_pattern()
    {
        var (grid, song) = Grid();
        Drag(grid, 0, 0, 3);
        Key(grid, Keys.Control | Keys.C);

        Click(grid, 0, 6);
        Key(grid, Keys.Control | Keys.V);

        var last = song.Patterns[1].Rows;
        Assert.Equal(6, last.Count);
        Assert.Equal(new NoteRow(NoteName.Parse("F-4"), 2), last[5]);
    }

    [Fact]
    public void Jam_mode_copies_but_does_not_paste()
    {
        var (grid, song) = Grid();
        Drag(grid, 0, 0, 0);
        Key(grid, Keys.Control | Keys.C);
        grid.ToggleEditMode();

        Click(grid, 0, 3);
        Key(grid, Keys.Control | Keys.V);

        Assert.Equal(new NoteRow(NoteName.Parse("F-4"), 2), song.Patterns[0].Rows[3]);
        Assert.Single(PatternClipboard.SelectedRows);
    }
}
