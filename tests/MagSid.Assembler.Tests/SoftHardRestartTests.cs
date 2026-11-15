using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// SOFTHR: when an instrument's note is replaced, the hard restart clears only the gate and keeps
/// its waveform rather than writing $08. Gated like vibrato and slide - a song without it carries
/// none of its code - so these pin what it costs, that the 6502 player and the C# engine do the
/// same thing with it, and that the instruments without it still get $08. With every instrument
/// soft (or NOHR) the test and the $08 both go; NOHR's own test is only there with NOHR.
/// </summary>
public class SoftHardRestartTests
{
    /// <summary>Channel 1 alternates instrument 1 (triangle) and instrument 2 (saw), each note
    /// replacing the other. Channels 2 and 3 hold one note each.</summary>
    private static Song Build(bool softOnFirst, bool softOnSecond = false, bool noneOnSecond = false)
    {
        var song = new Song { Speed = 2 };
        song.Instruments.Add(new Instrument
        {
            Number = 1, Waveform = 0x11, AttackDecay = 0x09, SustainRelease = 0xA9, SoftHardRestart = softOnFirst,
        });
        song.Instruments.Add(new Instrument
        {
            Number = 2, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xA9,
            SoftHardRestart = softOnSecond, NoHardRestart = noneOnSecond,
        });

        var melody = new Pattern { Number = 1 };
        foreach (var (note, instrument) in new[] { ("C-4", 1), ("E-4", 2), ("G-4", 1), ("C-5", 1), ("A-4", 2), ("F-4", 1) })
        {
            melody.Rows.Add(new NoteRow(NoteName.Parse(note), instrument));
            melody.Rows.Add(new HoldRow());
        }

        var held = new Pattern { Number = 2 };
        held.Rows.Add(new NoteRow(NoteName.Parse("C-3"), 2));
        for (int i = 0; i < 11; i++)
            held.Rows.Add(new HoldRow());

        song.Patterns.Add(melody);
        song.Patterns.Add(held);
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));
        return song;
    }

    [Fact]
    public void The_player_grows_only_when_an_instrument_uses_it()
    {
        // The rest of the suite runs with the switch that makes every hard restart soft turned
        // off; with it on there is nothing for the flag to add.
        Assert.False(HardRestart.KeepsWaveform);

        var plain = Build(softOnFirst: false);
        var soft = Build(softOnFirst: true);

        int plainBytes = SongImageBuilder.Build(plain).Length;
        int softBytes = SongImageBuilder.Build(soft).Length;

        Assert.Equal(plainBytes + 10, softBytes);
        Assert.Equal(plainBytes, SongSizeCalculator.Measure(plain).TotalBytes);
        Assert.Equal(softBytes, SongSizeCalculator.Measure(soft).TotalBytes);
    }

    [Fact]
    public void Every_instrument_soft_costs_2_and_NOHR_4()
    {
        Assert.False(HardRestart.KeepsWaveform);

        int plain = SongImageBuilder.Build(Build(softOnFirst: false)).Length;

        foreach (var (song, extra) in new[]
                 {
                     (Build(softOnFirst: true, softOnSecond: true), 2),
                     (Build(softOnFirst: true, noneOnSecond: true), 2 + 4),
                     (Build(softOnFirst: false, noneOnSecond: true), 4),
                 })
        {
            Assert.Equal(plain + extra, SongImageBuilder.Build(song).Length);
            Assert.Equal(plain + extra, SongSizeCalculator.Measure(song).TotalBytes);
        }
    }

    [Fact]
    public void With_every_instrument_soft_no_hard_restart_writes_08_not_even_a_voices_first()
    {
        var song = Build(softOnFirst: true, softOnSecond: true);
        var writes = new List<int>();
        var first = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) =>
        {
            if (register is 4 or 11 or 18)
            {
                writes.Add(value & 0xFF);
                first.TryAdd(register, value & 0xFF);
            }
        });

        for (int frame = 0; frame < 40; frame++)
            sequencer.Frame();

        // Each voice's first note has nothing before it to keep: $00, gate and waveform off.
        Assert.Equal([0x00, 0x00, 0x00], first.Values);
        Assert.DoesNotContain(0x08, writes);
        Assert.Contains(0x10, writes);
        Assert.Contains(0x20, writes);
    }

    [Fact]
    public void The_hard_restart_after_a_soft_instrument_keeps_its_waveform_and_the_rest_still_write_08()
    {
        var song = Build(softOnFirst: true);
        var controlByFrame = new List<List<int>>();
        var current = new List<int>();
        var sequencer = new SongSequencer(song, (register, value) =>
        {
            if (register == 4)
                current.Add(value & 0xFF);
        });

        for (int frame = 0; frame < 40; frame++)
        {
            current = new List<int>();
            sequencer.Frame();
            controlByFrame.Add(current);
        }

        var all = controlByFrame.SelectMany(c => c).ToList();

        // Instrument 1's triangle, gate off: what replacing it writes. Instrument 2 has no flag,
        // so replacing a saw note is the plain $08.
        Assert.Contains(0x10, all);
        Assert.Contains(0x08, all);
        Assert.DoesNotContain(0x20, all);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]     // every instrument soft
    [InlineData(true, false, true)]     // soft, and NOHR on the other
    [InlineData(false, false, true)]    // NOHR beside a plain hard restart
    public void The_6502_player_writes_what_the_sequencer_writes(bool soft, bool softOnSecond, bool noneOnSecond)
    {
        var song = Build(soft, softOnSecond, noneOnSecond);

        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

        for (int frame = 0; frame < 300; frame++)
        {
            fromCpu.Clear();
            fromSequencer.Clear();
            cpu.Call(0x1003);
            sequencer.Frame();

            Assert.True(fromCpu.Count == fromSequencer.Count
                        && fromCpu.All(w => fromSequencer.TryGetValue(w.Key, out int v) && v == w.Value),
                $"frame {frame}: 6502 {Format(fromCpu)} / sequencer {Format(fromSequencer)}");
        }
    }

    private static string Format(Dictionary<int, int> writes) =>
        string.Join(" ", writes.OrderBy(w => w.Key).Select(w => $"D4{w.Key:X2}=${w.Value:X2}"));
}
