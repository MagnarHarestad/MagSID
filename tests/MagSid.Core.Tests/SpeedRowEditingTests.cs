using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Core.Tests;

/// <summary>An Fxy row belongs to the time it sits at: a note sounds on through it, and no edit of
/// a note writes over it. And it is a row of the file like any other.</summary>
public class SpeedRowEditingTests
{
    private static readonly SpeedRow Speed = new(5, 3);

    private static NoteRow N(string note) => new(NoteName.Parse(note), 1);

    private static Pattern Make(params PatternRow[] rows)
    {
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        return pattern;
    }

    [Fact]
    public void A_note_sounds_on_through_it()
    {
        var pattern = Make(N("C-4"), new HoldRow(), Speed, new HoldRow(), N("E-4"));

        Assert.Equal(4, PatternNotes.Read(pattern)[0].Length);
    }

    [Fact]
    public void A_note_written_across_it_leaves_it_there()
    {
        var pattern = Make(new HoldRow(), new HoldRow(), Speed, new HoldRow());

        PatternNotes.SetNote(pattern, 0, NoteName.Parse("C-4"), 1, length: 4);

        Assert.Equal(Speed, pattern.Rows[2]);
        Assert.Equal(4, PatternNotes.Read(pattern)[0].Length);
    }

    [Fact]
    public void A_note_cannot_start_on_it_or_be_moved_onto_it()
    {
        var pattern = Make(N("C-4"), new HoldRow(), Speed, new HoldRow());

        PatternNotes.SetNote(pattern, 2, NoteName.Parse("E-4"), 1);
        PatternNotes.Move(pattern, 0, 2, NoteName.Parse("E-4"));
        PatternNotes.SetRelease(pattern, 2);

        Assert.Equal(Speed, pattern.Rows[2]);
        Assert.IsType<NoteRow>(pattern.Rows[0]);
    }

    [Fact]
    public void Shortening_a_note_to_it_puts_the_release_on_the_row_after()
    {
        var pattern = Make(N("C-4"), new HoldRow(), Speed, new HoldRow(), new HoldRow());

        Assert.True(PatternNotes.SetLength(pattern, 0, 2));

        Assert.Equal(Speed, pattern.Rows[2]);
        Assert.IsType<NoteOffRow>(pattern.Rows[3]);
    }

    [Fact]
    public void With_only_it_left_a_note_cannot_be_shortened()
    {
        var pattern = Make(N("C-4"), Speed, N("E-4"));

        Assert.False(PatternNotes.SetLength(pattern, 0, 1));

        Assert.Equal(Speed, pattern.Rows[1]);
        Assert.Equal(2, PatternNotes.Read(pattern)[0].Length);
    }

    [Fact]
    public void Growing_a_note_past_it_stops_where_asked()
    {
        var pattern = Make(N("C-4"), new NoteOffRow(), Speed, new HoldRow(), new HoldRow());

        Assert.True(PatternNotes.SetLength(pattern, 0, 2));

        Assert.Equal(Speed, pattern.Rows[2]);
        Assert.IsType<NoteOffRow>(pattern.Rows[3]);
    }

    [Fact]
    public void Erasing_and_clearing_leave_it()
    {
        var pattern = Make(N("A-3"), new HoldRow(), N("C-4"), Speed, new HoldRow(), new NoteOffRow());

        PatternNotes.Erase(pattern, 2);

        Assert.IsType<NoteOffRow>(pattern.Rows[2]);
        Assert.Equal(Speed, pattern.Rows[3]);

        var starting = Make(N("A-3"), Speed, new HoldRow(), new HoldRow());
        PatternNotes.ClearRange(starting, 1, 3);

        Assert.Equal(Speed, starting.Rows[1]);
        Assert.IsType<NoteOffRow>(starting.Rows[2]);
    }

    [Fact]
    public void Gluing_across_it_keeps_it_and_a_line_steps_over_it()
    {
        var glued = Make(N("C-4"), Speed, N("E-4"), new HoldRow());
        Assert.True(PatternNotes.Glue(glued, 0));
        Assert.Equal(Speed, glued.Rows[1]);
        Assert.IsType<HoldRow>(glued.Rows[2]);

        var drawn = Make(new HoldRow(), Speed, new HoldRow(), new HoldRow());
        PatternNotes.DrawLine(drawn, 0, NoteName.Parse("C-4"), 2, NoteName.Parse("E-4"), 1);
        Assert.Equal(Speed, drawn.Rows[1]);
        Assert.IsType<NoteRow>(drawn.Rows[0]);
        Assert.IsType<NoteRow>(drawn.Rows[2]);
    }

    [Fact]
    public void A_slide_cannot_take_its_place_and_runs_on_through_it()
    {
        var pattern = Make(N("C-4"), new SlideRow(8), Speed, new HoldRow());

        Assert.False(PatternSlides.Slidable(pattern)[2]);
        Assert.Equal(8, PatternSlides.Rates(pattern)[3]);

        PatternSlides.SetRates(pattern, new Dictionary<int, int> { [2] = 20, [3] = 0 });

        Assert.Equal(Speed, pattern.Rows[2]);
        Assert.Equal(new SlideRow(0), pattern.Rows[3]);
    }

    [Fact]
    public void It_goes_through_the_file_as_it_reads()
    {
        const string text = """
            var v_Song_Speed = 4

            INSTRUMENTS:
            01, $41, $00, $f7
            END:

            PAT01:
            "F-4", 01
            "F64", 00
            "F60", 00
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

        var song = PatternsFileReader.Parse(text);
        var rows = song.Patterns[0].Rows;

        Assert.IsType<NoteRow>(rows[0]);
        Assert.Equal(new SpeedRow(5, 3), rows[1]);
        Assert.Equal(new SpeedRow(5, 5), rows[2]);

        string written = PatternsFileWriter.Render(song);
        Assert.Contains("\"F64\", 00", written);
        Assert.Contains("\"F60\", 00", written);
        Assert.Equal(written, PatternsFileWriter.Render(PatternsFileReader.Parse(written)));
    }

    [Fact]
    public void A_speed_of_nothing_is_refused()
    {
        const string text = """
            var v_Song_Speed = 4

            INSTRUMENTS:
            01, $41, $00, $f7
            END:

            PAT01:
            "F04", 00
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

        Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text));
    }

    [Fact]
    public void Each_row_costs_three_bytes_of_pattern_and_the_player_only_pays_once()
    {
        var plain = new Song();
        plain.Instruments.Add(new Instrument { Number = 1, Waveform = 0x41 });
        var pattern = Make(N("C-4"), new HoldRow(), new HoldRow(), new HoldRow());
        plain.Patterns.Add(pattern);
        foreach (var order in plain.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        var withOne = SongCloner.Clone(plain);
        withOne.Patterns[0].Rows[3] = new SpeedRow(1, 1);
        var withTwo = SongCloner.Clone(withOne);
        withTwo.Patterns[0].Rows[2] = new SpeedRow(2, 2);

        Assert.Equal(SongSizeCalculator.Measure(plain).PlayerBytes + 24, SongSizeCalculator.Measure(withOne).PlayerBytes);
        Assert.Equal(SongSizeCalculator.Measure(withOne).PlayerBytes, SongSizeCalculator.Measure(withTwo).PlayerBytes);
    }
}
