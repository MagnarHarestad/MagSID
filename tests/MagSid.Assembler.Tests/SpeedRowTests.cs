using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// Fxy rows: a pattern changes the song's speed and funk speed. Gated like slide and vibrato: a song
/// without one is assembled as before, which the whole image tests of every other song pin, and these
/// pin what it costs and that the 6502 player and the sequencer do the same with it.
/// </summary>
public class SpeedRowTests
{
    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    private static string Golden(string file) => Path.Combine(
        AppContext.BaseDirectory, "Goldens", HardRestart.KeepsWaveform ? "GateOff" : "", file);

    /// <summary>The rows turned into plain holds: the same song without the feature.</summary>
    private static Song Without(Song song)
    {
        var plain = SongCloner.Clone(song);
        foreach (var pattern in plain.Patterns)
        {
            for (int row = 0; row < pattern.Rows.Count; row++)
            {
                if (pattern.Rows[row] is SpeedRow)
                    pattern.Rows[row] = new HoldRow();
            }
        }

        return plain;
    }

    [Theory]
    [InlineData("speed_only.s", "speed_only.bin")]
    [InlineData("speed_slide.s", "speed_slide.bin")]
    [InlineData("speed_funk.s", "speed_funk.bin")]
    public void Whole_image_matches_what_c6510_produced(string file, string golden)
    {
        var song = PatternsFileReader.Read(Fixture(file));
        Assert.True(SongImageBuilder.FeaturesOf(song).SpeedRows);

        Assert.Equal(File.ReadAllBytes(Golden(golden)), SongImageBuilder.Build(song));
        Assert.Equal(SongSizeCalculator.Measure(song).TotalBytes, SongImageBuilder.Build(song).Length);
    }

    [Theory]
    [InlineData("speed_only.s")]
    [InlineData("speed_slide.s")]
    [InlineData("speed_funk.s")]
    public void The_emulated_player_writes_what_the_sequencer_writes(string file)
    {
        var song = PatternsFileReader.Read(Fixture(file));

        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

        int frames = SongCoverageAnalyzer.PassFrames(song) + 2000;
        for (int frame = 0; frame < frames; frame++)
        {
            fromCpu.Clear();
            fromSequencer.Clear();
            cpu.Call(0x1003);
            sequencer.Frame();

            Assert.True(fromCpu.Count == fromSequencer.Count
                        && fromCpu.All(w => fromSequencer.TryGetValue(w.Key, out int v) && v == w.Value),
                $"{file}, frame {frame}: the 6502 wrote {Format(fromCpu)}, the sequencer {Format(fromSequencer)}");
        }
    }

    private static string Format(Dictionary<int, int> writes) =>
        string.Join(" ", writes.OrderBy(w => w.Key).Select(w => $"D4{w.Key:X2}=${w.Value:X2}"));

    /// <summary>One pattern on every channel, so the three reach an Fxy row together.</summary>
    private static Song OnePattern(int speed, params PatternRow[] rows)
    {
        var song = new Song { Speed = speed };
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        return song;
    }

    private static List<int> TickLengths(Song song, int count)
    {
        var sequencer = new SongSequencer(song, (_, _) => { });
        var ticks = new List<int>();
        for (int frame = 0; ticks.Count <= count && frame < 1000; frame++)
        {
            if (sequencer.Frame())
                ticks.Add(frame);
        }

        return ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToList();
    }

    [Fact]
    public void The_row_lasts_the_new_speed_and_the_next_the_funk_speed()
    {
        var song = OnePattern(3,
            new HoldRow(), new SpeedRow(5, 1),
            new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        Assert.Equal([4, 6, 2, 6, 2, 6], TickLengths(song, 6));
    }

    [Fact]
    public void The_speed_set_stays_through_the_songs_loop()
    {
        var song = OnePattern(3, new SpeedRow(1, 1), new HoldRow());

        Assert.Equal([2, 2, 2, 2, 2, 2], TickLengths(song, 6));
    }

    [Fact]
    public void When_two_channels_change_speed_on_one_tick_channel_1_wins()
    {
        var song = new Song { Speed = 3 };
        foreach (var (number, speed) in new[] { (1, 1), (2, 5) })
        {
            var pattern = new Pattern { Number = number };
            pattern.Rows.Add(new SpeedRow(speed, speed));
            pattern.Rows.Add(new HoldRow());
            pattern.Rows.Add(new HoldRow());
            song.Patterns.Add(pattern);
        }

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));

        Assert.Equal([2, 2], TickLengths(song, 2));
    }

    [Fact]
    public void A_resume_carries_on_at_the_speed_the_row_set()
    {
        var song = PatternsFileReader.Read(Fixture("speed_only.s"));
        var from = SongSequencer.FindExactPosition(song, channel: 0, orderIndex: 0, rowIndex: 3);
        Assert.NotNull(from);
        Assert.Equal(5, from!.TickSpeed(song));

        var top = new SongSequencer(song, (_, _) => { });
        for (int frame = 0; frame < from.Frame; frame++)
            top.Frame();

        var resumed = new SongSequencer(song, (_, _) => { }, from);
        for (int frame = 0; frame < 200; frame++)
        {
            Assert.Equal(top.Frame(), resumed.Frame());
            Assert.Equal(top.Capture().SpeedCounter, resumed.Capture().SpeedCounter);
        }
    }

    [Fact]
    public void A_song_without_one_pays_nothing_and_with_one_pays_what_the_size_says()
    {
        var song = PatternsFileReader.Read(Fixture("speed_only.s"));
        var plain = Without(song);

        Assert.False(SongImageBuilder.FeaturesOf(plain).SpeedRows);
        Assert.Equal(new ZeroPageLayout(new PlayerFeatures(false, false)).ByteCount + 2,
                     new ZeroPageLayout(new PlayerFeatures(false, false, SpeedRows: true)).ByteCount);
        Assert.Equal(SongSizeCalculator.Measure(plain).PlayerBytes + 24, SongSizeCalculator.Measure(song).PlayerBytes);
    }

    [Theory]
    [InlineData("F64", 5, 3, "F64")]
    [InlineData("F60", 5, 5, "F60")]
    [InlineData("F66", 5, 5, "F60")]
    [InlineData("f1f", 0, 14, "F1F")]
    [InlineData("FF1", 14, 0, "FF1")]
    public void The_text_reads_as_frames_a_row(string text, int speed, int funk, string written)
    {
        Assert.True(SpeedRow.TryParse(text, out var row));
        Assert.Equal(new SpeedRow(speed, funk), row);
        Assert.Equal(written, row.Text);
    }

    [Theory]
    [InlineData("F-4")]
    [InlineData("F#4")]
    [InlineData("F04")]
    [InlineData("F00")]
    [InlineData("164")]
    [InlineData("F6")]
    public void Anything_else_is_not_a_speed_row(string text)
    {
        Assert.False(SpeedRow.TryParse(text, out _));
    }
}
