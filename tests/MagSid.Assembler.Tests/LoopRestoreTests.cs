using MagSid.Core;
using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// A loop that ends on another instrument puts its own back for the next pass, directly ahead of the
/// body's first note. At the top of the body it would change the instrument under the note still
/// sounding, for as long as a first row that is not a note lasts.
/// </summary>
public class LoopRestoreTests
{
    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    private static string Golden(string file) => Path.Combine(
        AppContext.BaseDirectory, "Goldens", HardRestart.KeepsWaveform ? "GateOff" : "", file);

    private static Core.Model.Song Song(params string[] rows)
    {
        string text = $$"""
            var v_Song_Speed = 3

            INSTRUMENTS:
            01, $41, $0a, $50, PW 2 +20
            02, $41, $0a, $a9, PW 8 +0
            03, $21, $00, $f7
            END:

            PAT01:
            {{string.Join("\n", rows)}}
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
        return PatternsFileReader.Parse(text);
    }

    private static string Encoded(Core.Model.Song song) => Convert.ToHexString(
        PatternEncoder.Encode(song.Patterns[0], NoteDictionary.Build(song), InstrumentTables.PlayerOrder(song)));

    [Theory]
    [InlineData("\"...\"", "D1")]
    [InlineData("\"---\"", "D0")]
    [InlineData("\"103\"", "D303")]
    [InlineData("\"F44\"", "D40303")]
    public void It_goes_ahead_of_the_first_note_and_not_the_first_row(string firstRow, string encoded)
    {
        var song = Song("\"C-4\", 01", "LS", $"{firstRow}, 00", "\"E-4\", 01", "\"G-4\", 02", "LE");

        Assert.Equal($"C08001{encoded}C002C103D2000300", Encoded(song));
    }

    [Fact]
    public void A_note_as_the_first_row_has_it_where_it_always_was()
    {
        var song = Song("\"C-4\", 01", "LS", "\"E-4\", 01", "\"G-4\", 02", "LE");

        Assert.Equal("C08001C002C103D2000300", Encoded(song));
    }

    [Fact]
    public void The_instrument_goes_ahead_of_a_duration_at_the_same_place()
    {
        var song = Song("\"C-4\", 01", "LS", "\"E-4\", 01", "\"G-4\", 02", "\"...\", 00", "LE");

        Assert.Equal("C08001C08002C18103D2000300", Encoded(song));
    }

    [Fact]
    public void The_duration_stays_at_the_top_since_the_first_row_lasts_it()
    {
        var song = Song("\"C-4\", 01", "LS", "\"---\", 00", "\"E-4\", 01", "\"G-4\", 02", "\"...\", 00", "LE");

        Assert.Equal("C0800180D0C002C18103D2000300", Encoded(song));
    }

    [Fact]
    public void A_first_note_with_an_instrument_of_its_own_needs_none()
    {
        var song = Song("\"C-4\", 01", "LS", "\"...\", 00", "\"E-4\", 02", "\"G-4\", 03", "LE");

        Assert.Equal("C08001D1C102C203D2000300", Encoded(song));
    }

    [Fact]
    public void A_later_end_on_the_same_anchor_does_not_put_it_back_twice()
    {
        var song = Song("\"C-4\", 01", "LS", "\"E-4\", 01", "\"G-4\", 02", "LE02", "\"A-4\", 03", "LE02");

        Assert.Equal("C08001C002C103D2020" + "3C204D2020300", Encoded(song));
    }

    [Theory]
    [InlineData("\"...\"")]
    [InlineData("\"---\"")]
    [InlineData("\"103\"")]
    [InlineData("\"F44\"")]
    public void The_note_sounding_keeps_its_instrument_on_every_pass(string firstRow)
    {
        var song = Song("\"C-4\", 01", "LS", $"{firstRow}, 00", "\"E-4\", 01", "\"G-4\", 02", "\"...\", 00", "LE");

        AssertPlayerAndSequencerAgree(song, 400);
    }

    [Fact]
    public void Whole_image_matches_what_c6510_produced()
    {
        var song = PatternsFileReader.Read(Fixture("loop_restore.s"));

        Assert.Equal(File.ReadAllBytes(Golden("loop_restore.bin")), SongImageBuilder.Build(song));
        Assert.Equal(SongSizeCalculator.Measure(song).TotalBytes, SongImageBuilder.Build(song).Length);
    }

    [Fact]
    public void The_emulated_player_writes_what_the_sequencer_writes()
    {
        var song = PatternsFileReader.Read(Fixture("loop_restore.s"));

        AssertPlayerAndSequencerAgree(song, SongCoverageAnalyzer.PassFrames(song) + 2000);
    }

    [Fact]
    public void A_loop_from_Optimize_leaves_the_C64_as_it_was()
    {
        var rows = new List<string> { "\"C-4\", 01" };
        for (int i = 0; i < 8; i++)
            rows.AddRange(["\"---\", 00", "\"E-4\", 02"]);
        var song = Song([.. rows]);

        var before = PlayerWrites(song, 400);
        Assert.True(SongOptimizer.Apply(song).BytesSaved > 0);
        Assert.Contains(song.Patterns[0].Rows, row => row is Core.Model.LoopStartRow);

        Assert.Equal(before, PlayerWrites(song, 400));
    }

    private static List<string> PlayerWrites(Core.Model.Song song, int frames)
    {
        var writes = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => writes[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var log = new List<string>();
        for (int frame = 0; frame < frames; frame++)
        {
            writes.Clear();
            cpu.Call(0x1003);
            log.Add($"{frame}: {Format(writes)}");
        }

        return log;
    }

    private static void AssertPlayerAndSequencerAgree(Core.Model.Song song, int frames)
    {
        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

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
