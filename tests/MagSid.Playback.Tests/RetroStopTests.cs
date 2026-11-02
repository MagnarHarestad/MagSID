using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Editor.App.Controls;
using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// Stopping in Retro parks the cursor on the row playing, in the numbering it played under. It used
/// to move the song position to that row's order entry, which restarted the numbers there and put
/// every other channel at the same entry number, another moment when the channels differ in length.
/// </summary>
public class RetroStopTests
{
    /// <summary>One frame a row. Channel 1 runs 32 rows, channel 2 runs 16 and channel 3 runs 12,
    /// so the two short ones have gone round by the time channel 1 is part-way.</summary>
    private const string Text = """
        var v_Song_Speed = 0

        INSTRUMENTS:
        01, $41, $00, $f7
        END:

        PAT01:
        "C-4", 01
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        END:

        PAT02:
        "D-4", 01
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        END:

        PAT03:
        "E-4", 01
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        END:

        PAT04:
        "F-4", 01
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        END:

        PAT05:
        "G-4", 01
        "...", 00
        "...", 00
        "...", 00
        END:

        PAT06:
        "A-4", 01
        "...", 00
        "...", 00
        "...", 00
        END:

        ORDER_CH1:
        1, 2, 3, 4
        END:

        ORDER_CH2:
        1, 2
        END:

        ORDER_CH3:
        5, 6, 5
        END:
        """;

    private static TrackerGridControl StoppedAt(Song song, int stopFrame)
    {
        long SampleOf(int frame) => (long)(frame * SongPlaybackEngine.SamplesPerFrame);

        var grid = new TrackerGridControl { AudioClockSamples = () => SampleOf(stopFrame) };
        grid.SetSong(song);
        grid.SetPlaybackActive(true);

        int current = 0;
        var sequencer = new SongSequencer(song, (_, _) => { });
        sequencer.RowTriggered += (channel, pattern, row, sequence, _) =>
            grid.SetChannelPlaybackAnchor(channel, pattern, row, sequence, SampleOf(current));
        for (current = 0; current <= stopFrame; current++)
            sequencer.Frame();

        grid.JumpEditCursorToLivePlaybackPosition();
        grid.SetPlaybackActive(false);
        return grid;
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(31)]
    public void The_row_keeps_its_number(int row)
    {
        var grid = StoppedAt(PatternsFileReader.Parse(Text), row);

        Assert.Equal(row, grid.CursorRow);
        Assert.Equal(0, grid.SongPosition);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(29)]
    public void Every_channel_shows_the_pattern_it_is_playing(int row)
    {
        var song = PatternsFileReader.Parse(Text);
        var grid = StoppedAt(song, row);

        var playing = SongSequencer.PositionAtFrame(song, row + 1).Channels.Select(c => c.CurrentPatternNumber);
        Assert.Equal(playing, grid.CurrentPatternsAtCursor().Select(p => p!.Number));
    }

    [Fact]
    public void A_song_gone_round_lands_on_that_place_in_its_first_pass()
    {
        var grid = StoppedAt(PatternsFileReader.Parse(Text), 32 + 20);

        Assert.Equal(20, grid.CursorRow);
        Assert.Equal(0, grid.SongPosition);
    }
}
