using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// The stack pointer's transfers. A tune that saves it and sets it back to return from deep in its
/// code runs off into the stack if TXS takes the wrong register.
/// </summary>
public class Mos6502StackTests
{
    /// <summary>Runs one instruction at a time from $1000, as many as <paramref name="steps"/>.</summary>
    private static Mos6502 Run(int steps, params byte[] code)
    {
        var memory = new byte[0x10000];
        code.CopyTo(memory, 0x1000);
        var cpu = new Mos6502(memory) { PC = 0x1000 };
        for (int step = 0; step < steps; step++)
            cpu.Step();
        return cpu;
    }

    [Fact]
    public void Txs_copies_x_to_the_stack_pointer()
    {
        var cpu = Run(3, 0xA9, 0x11, 0xA2, 0x77, 0x9A);         // LDA #$11, LDX #$77, TXS
        Assert.Equal(0x77, cpu.SP);
        Assert.Equal(0x11, cpu.A);
    }

    [Fact]
    public void Tsx_copies_the_stack_pointer_to_x()
    {
        var cpu = Run(4, 0xA2, 0x77, 0x9A, 0xA2, 0x00, 0xBA);   // LDX #$77, TXS, LDX #$00, TSX
        Assert.Equal(0x77, cpu.X);
    }
}
