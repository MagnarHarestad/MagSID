using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The filter sweep goes back and forth between where it started and its end, at a signed rate a
/// frame. A setup with a rate starts one from its own cutoff when none is running, and leaves a
/// running one as it is. Only a song with a sweep has the code, and only its setups carry a rate
/// and an end.
/// </summary>
public class FilterSweepTests
{
    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    private static string Golden(string file) => Path.Combine(
        AppContext.BaseDirectory, "Goldens", HardRestart.KeepsWaveform ? "GateOff" : "", file);

    private static Song OneInstrument(string filter, params string[] rows)
    {
        string text = $$"""
            var v_Song_Speed = 2

            INSTRUMENTS:
            01, $41, $00, $ea, {{filter}}
            END:

            PAT01:
            {{string.Join("\n", rows)}}
            END:

            PAT02:
            "...", 00
            END:

            ORDER_CH1:
            1
            END:

            ORDER_CH2:
            2
            END:

            ORDER_CH3:
            2
            END:
            """;
        return PatternsFileReader.Parse(text);
    }

    /// <summary>$D416 as it stands after each frame.</summary>
    private static List<int> Cutoffs(Song song, int frames)
    {
        int cutoff = -1;
        var sequencer = new SongSequencer(song, (register, value) =>
        {
            if (register == 0x16)
                cutoff = value & 0xFF;
        });

        var cutoffs = new List<int>();
        for (int frame = 0; frame < frames; frame++)
        {
            sequencer.Frame();
            cutoffs.Add(cutoff);
        }

        return cutoffs;
    }

    private static readonly string[] TwoNotes =
        ["\"C-2\", 01", "\"...\", 00", "\"...\", 00", "\"...\", 00", "\"C-2\", 01", "\"...\", 00", "\"...\", 00", "\"...\", 00"];

    [Fact]
    public void A_sweep_goes_to_its_end_and_back_and_starts_over_on_every_note()
    {
        // Rows of 3 frames: a note on frame 0 and again on frame 12, its program starting a frame on.
        var cutoffs = Cutoffs(OneInstrument("FLT ($1f $f1 $08) $08 ($1f $f1 $08 +1 $0c)", TwoNotes), 18);

        Assert.Equal([0x08, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0B, 0x0A, 0x09, 0x08, 0x09], cutoffs.GetRange(1, 11));
        Assert.Equal([0x08, 0x08, 0x09, 0x0A, 0x0B], cutoffs.GetRange(13, 5));
    }

    [Fact]
    public void A_sweep_already_running_goes_on_as_it_is()
    {
        // The second note comes while the sweep is on its way back up, and does not turn it round.
        var song = OneInstrument("FLT ($3f $f3 $fe -1 $f8)",
            "\"C-2\", 01", "\"...\", 00", "\"C-2\", 01", "\"...\", 00", "\"...\", 00", "\"...\", 00");

        var cutoffs = Cutoffs(song, 11);

        Assert.Equal([0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC], cutoffs.GetRange(1, 10));
    }

    [Fact]
    public void GRGs_Lightforce_sweep_turns_every_192_frames()
    {
        // Lightforce 2006 by Glenn Rune Gallefoss sweeps $D416 between $FF and $3F a step a frame;
        // a cutoff is $01..$FE here, so the same sweep a step lower.
        var song = OneInstrument("FLT ($3f $13 $fe -1 $3e)", "\"C-2\", 01", "\"...\", 00");

        var cutoffs = Cutoffs(song, 800);
        var turns = Enumerable.Range(2, cutoffs.Count - 3)
            .Where(f => Math.Sign(cutoffs[f] - cutoffs[f - 1]) != Math.Sign(cutoffs[f + 1] - cutoffs[f]))
            .ToList();

        Assert.Equal([0x3E, 0xFE, 0x3E, 0xFE], turns.Take(4).Select(f => cutoffs[f]));
        Assert.All(turns.Zip(turns.Skip(1), (a, b) => b - a), gap => Assert.Equal(192, gap));
    }

    [Theory]
    [InlineData(0x08, 1, 0x0C, 0x0C)]      // asked for and reachable
    [InlineData(0xA0, -3, 0x11, 0x13)]     // the last one the rate lands on before it
    [InlineData(0x40, 5, null, 0xFE)]      // none asked for: as far as the rate goes
    [InlineData(0x80, 4, 0x10, 0x84)]      // the wrong side of the start: one step
    public void The_end_is_one_the_rate_lands_on(int start, int rate, int? end, int expected)
    {
        Assert.Equal(expected, FilterStep.Setup(0x1f, 0xf1, (byte)start, rate, end).SweepEnd);
    }

    [Fact]
    public void The_end_is_written_and_read_back()
    {
        var song = OneInstrument("FLT ($3f $13 $fe -1)", "\"C-2\", 01");

        string written = PatternsFileWriter.Render(song, "test");

        Assert.Contains("FLT ($3f $13 $fe -1 $00)", written);
        Assert.Equal(written, PatternsFileWriter.Render(PatternsFileReader.Parse(written), "test"));
    }

    [Fact]
    public void Only_a_song_with_a_sweep_pays_for_it()
    {
        var with = OneInstrument("FLT ($1f $f1 $08) $08 ($1f $f1 $08 +1 $40)", "\"C-2\", 01");
        var without = OneInstrument("FLT ($1f $f1 $08) $08 ($1f $f1 $08)", "\"C-2\", 01");

        Assert.True(SongImageBuilder.FeaturesOf(with).FilterSweep);
        Assert.False(SongImageBuilder.FeaturesOf(without).FilterSweep);

        // The code, and a rate and an end in each of the two setups.
        Assert.Equal(SongSizeCalculator.Measure(without).PlayerBytes + 55, SongSizeCalculator.Measure(with).PlayerBytes);
        Assert.Equal(SongSizeCalculator.Measure(without).FilterPoolBytes + 4, SongSizeCalculator.Measure(with).FilterPoolBytes);
        Assert.Equal(new ZeroPageLayout(SongImageBuilder.FeaturesOf(without)).ByteCount + 4,
                     new ZeroPageLayout(SongImageBuilder.FeaturesOf(with)).ByteCount);
        Assert.Equal(SongSizeCalculator.Measure(with).TotalBytes, SongImageBuilder.Build(with).Length);
        Assert.Equal(SongSizeCalculator.Measure(without).TotalBytes, SongImageBuilder.Build(without).Length);
    }

    [Fact]
    public void Whole_image_matches_what_c6510_produced()
    {
        var song = PatternsFileReader.Read(Fixture("filter_sweep.s"));

        Assert.Equal(File.ReadAllBytes(Golden("filter_sweep.bin")), SongImageBuilder.Build(song));
        Assert.Equal(SongSizeCalculator.Measure(song).TotalBytes, SongImageBuilder.Build(song).Length);
    }

    [Fact]
    public void The_emulated_player_writes_what_the_sequencer_writes()
    {
        var song = PatternsFileReader.Read(Fixture("filter_sweep.s"));

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
                $"frame {frame}: the 6502 wrote {Format(fromCpu)}, the sequencer {Format(fromSequencer)}");
        }
    }

    private static string Format(Dictionary<int, int> writes) =>
        string.Join(" ", writes.OrderBy(w => w.Key).Select(w => $"D4{w.Key:X2}=${w.Value:X2}"));
}
