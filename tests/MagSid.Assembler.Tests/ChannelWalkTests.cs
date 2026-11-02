using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The player walks its voices 3, 2, 1, so where two channels reach the same shared state on one
/// tick, channel 1 has the last word. The sequencer has to walk them the same way round: no song
/// did this yet, so a sequencer walking 1, 2, 3 stayed unnoticed by every song comparison.
/// </summary>
public class ChannelWalkTests
{
    [Fact]
    public void Two_filter_programs_claimed_on_one_tick_go_to_channel_1_on_both()
    {
        var song = new Song { Speed = 3 };
        song.Instruments.Add(new Instrument
        {
            Number = 1, Waveform = 0x41, AttackDecay = 0x09, SustainRelease = 0xA9,
            Filter = { FilterStep.Setup(0x1F, 0xF1, 0x40) },
        });
        song.Instruments.Add(new Instrument
        {
            Number = 2, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xA9,
            Filter = { FilterStep.Setup(0x2F, 0x34, 0xB0) },
        });

        song.Patterns.Add(Held(1, 1));
        song.Patterns.Add(Held(2, 2));
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));

        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

        for (int frame = 0; frame < 200; frame++)
        {
            cpu.Call(0x1003);
            sequencer.Frame();
        }

        Assert.Equal(0x40, fromCpu[0x16]);
        Assert.Equal(fromCpu[0x16], fromSequencer[0x16]);
        Assert.Equal(fromCpu[0x17], fromSequencer[0x17]);
        Assert.Equal(fromCpu[0x18], fromSequencer[0x18]);
    }

    private static Pattern Held(int number, int instrument)
    {
        var pattern = new Pattern { Number = number };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), instrument));
        for (int row = 1; row < 16; row++)
            pattern.Rows.Add(new HoldRow());
        return pattern;
    }
}
