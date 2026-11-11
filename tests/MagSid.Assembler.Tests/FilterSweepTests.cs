using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The filter sweep starts from the cutoff of the last step taken while no sweep ran, rather than
/// from wherever an earlier sweep left off. A sweep already running keeps its place. Only a song
/// with a sweep pays for it.
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

    [Fact]
    public void A_sweep_starts_from_the_setup_before_it_on_every_note()
    {
        // Rows of 3 frames: a note on frame 0 and again on frame 12, its program starting a frame on.
        var song = OneInstrument("FLT ($1f $f1 $08) $08 ($1f $f1 $08 +1)",
            "\"C-2\", 01", "\"...\", 00", "\"...\", 00", "\"...\", 00", "\"C-2\", 01", "\"...\", 00", "\"...\", 00", "\"...\", 00");

        var cutoffs = Cutoffs(song, 18);

        Assert.Equal([0x08, 0x08, 0x0A, 0x0C, 0x0E], cutoffs.GetRange(1, 5));
        Assert.Equal([0x08, 0x08, 0x0A, 0x0C, 0x0E], cutoffs.GetRange(13, 5));
    }

    [Fact]
    public void A_sweep_already_running_keeps_its_place()
    {
        var song = OneInstrument("FLT ($3f $f3 $80 +1)",
            "\"C-2\", 01", "\"...\", 00", "\"C-2\", 01", "\"...\", 00");

        var cutoffs = Cutoffs(song, 12);

        // The song starts the sweep from the bottom; the second note's setup does not move it.
        Assert.Equal([0x02, 0x04, 0x06, 0x08, 0x0A, 0x0C, 0x0E, 0x10], cutoffs.GetRange(1, 8));
    }

    [Fact]
    public void Only_a_song_with_a_sweep_pays_for_it()
    {
        var with = OneInstrument("FLT ($1f $f1 $08) $08 ($1f $f1 $08 +1)", "\"C-2\", 01");
        var without = OneInstrument("FLT ($1f $f1 $08) $08 ($1f $f1 $08)", "\"C-2\", 01");

        Assert.True(SongImageBuilder.FeaturesOf(with).FilterSweep);
        Assert.False(SongImageBuilder.FeaturesOf(without).FilterSweep);
        Assert.Equal(SongSizeCalculator.Measure(without).PlayerBytes + 7, SongSizeCalculator.Measure(with).PlayerBytes);
        Assert.Equal(SongSizeCalculator.Measure(with).TotalBytes, SongImageBuilder.Build(with).Length);
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
