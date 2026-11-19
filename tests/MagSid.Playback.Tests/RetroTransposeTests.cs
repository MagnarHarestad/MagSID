using System.Reflection;
using System.Windows.Forms;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Editor.App.Controls;

namespace MagSid.Playback.Tests;

/// <summary>
/// Retro's transpose: Shift+Q / Shift+A move the selected notes a semitone up / down, Shift+W /
/// Shift+S an octave - without a selection, the cursor's note. Driven through the grid's own mouse
/// and key handlers.
/// </summary>
public class RetroTransposeTests
{
    /// <summary>With loops unrolled the grid shows rows 0 C-4, 1 LS D-4, 2 LE02 E-4, 3-4 the second
    /// pass, 5 a hold, 6 a note off, 7 B-7.</summary>
    private const string Text = """
        var v_Song_Speed = 2

        INSTRUMENTS:
        01, $41, $00, $f7
        02, $21, $00, $f7
        END:

        PAT01:
        "C-4", 01
        LS
        "D-4", 02
        "E-4", 01
        LE02
        "...", 00
        "---", 00
        "B-7", 02
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
        var (_, toY) = At(grid, channel, to);
        typeof(TrackerGridControl).GetMethod("DragTo", Hidden)!.Invoke(grid, [toY]);
        Call(grid, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, x, toY, 0));
    }

    private static void Key(TrackerGridControl grid, Keys keys) => Call(grid, "OnKeyDown", new KeyEventArgs(keys));

    private static string Notes(Song song) => string.Join(" ", song.Patterns[0].Rows.Select(row => row switch
    {
        NoteRow note => $"{note.Note}/{note.InstrumentNumber}",
        LoopStartRow => "LS",
        LoopEndRow => "LE",
        NoteOffRow => "---",
        _ => "...",
    }));

    [Fact]
    public void Shift_Q_and_Shift_A_move_the_selected_notes_a_semitone_and_keep_their_instruments()
    {
        var (grid, song) = Grid();
        Drag(grid, 0, 0, 2);

        Key(grid, Keys.Shift | Keys.Q);
        Assert.Equal("C#4/1 LS D#4/2 F-4/1 LE ... --- B-7/2", Notes(song));

        Key(grid, Keys.Shift | Keys.A);
        Key(grid, Keys.Shift | Keys.A);
        Assert.Equal("B-3/1 LS C#4/2 D#4/1 LE ... --- B-7/2", Notes(song));
    }

    [Fact]
    public void Shift_W_and_Shift_S_move_them_an_octave()
    {
        var (grid, song) = Grid();
        Drag(grid, 0, 0, 2);

        Key(grid, Keys.Shift | Keys.W);
        Assert.Equal("C-5/1 LS D-5/2 E-5/1 LE ... --- B-7/2", Notes(song));

        Key(grid, Keys.Shift | Keys.S);
        Key(grid, Keys.Shift | Keys.S);
        Assert.Equal("C-3/1 LS D-3/2 E-3/1 LE ... --- B-7/2", Notes(song));
    }

    [Fact]
    public void A_loops_second_pass_is_its_first_pass_again_so_each_note_moves_once()
    {
        var (grid, song) = Grid();
        Drag(grid, 0, 1, 4);        // both passes of D-4 and E-4

        Key(grid, Keys.Shift | Keys.Q);

        Assert.Equal("C-4/1 LS D#4/2 F-4/1 LE ... --- B-7/2", Notes(song));
    }

    [Fact]
    public void Without_a_selection_the_cursors_note_moves()
    {
        var (grid, song) = Grid();
        Click(grid, 0, 5);          // a hold: nothing to move
        Key(grid, Keys.Shift | Keys.Q);
        Click(grid, 0, 0);
        Key(grid, Keys.Shift | Keys.Q);

        Assert.Equal("C#4/1 LS D-4/2 E-4/1 LE ... --- B-7/2", Notes(song));
    }

    [Fact]
    public void A_note_that_would_leave_the_range_stops_the_whole_transpose()
    {
        var (grid, song) = Grid();
        string? notice = null;
        grid.Notice += text => notice = text;
        Drag(grid, 0, 0, 7);        // up to B-7

        Key(grid, Keys.Shift | Keys.Q);

        Assert.Equal("C-4/1 LS D-4/2 E-4/1 LE ... --- B-7/2", Notes(song));
        Assert.NotNull(notice);
    }

    [Fact]
    public void Jam_mode_does_not_transpose()
    {
        var (grid, song) = Grid();
        Drag(grid, 0, 0, 2);
        grid.ToggleEditMode();

        Key(grid, Keys.Shift | Keys.W);

        Assert.Equal("C-4/1 LS D-4/2 E-4/1 LE ... --- B-7/2", Notes(song));
    }
}
