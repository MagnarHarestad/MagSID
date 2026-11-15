namespace MagSid.Core;

/// <summary>
/// How the hard restart on a note's trigger frame leaves the control register - the one switch
/// for it. The C# playback engine, the 6502 player the .sid export builds and the size figures all
/// read this, and the golden images for each setting sit side by side, so flipping it is the whole
/// of going back.
///
/// <list type="bullet">
/// <item><c>true</c>: gate off with the waveform kept, the GoatTracker way, for every
/// instrument. The old note fades through the frame rather than being cut, and the oscillator is
/// not reset. Two bytes more player.</item>
/// <item><c>false</c>: $08 - gate off, TEST on, no waveform: the waveform stops at once and the
/// oscillator is held at zero. Except after an instrument with
/// <see cref="Model.Instrument.SoftHardRestart"/>, which gets the gentle way on its own.</item>
/// </list>
///
/// AD $00 and SR $07 are written either way.
/// </summary>
public static class HardRestart
{
    public static readonly bool KeepsWaveform = false;

    /// <summary>Some instrument has NOHR, so the player tests for it before the hard restart; in a
    /// song without one the test is left out, 4 bytes.</summary>
    public static bool AnySkipped(IReadOnlyList<Model.Instrument> instruments)
    {
        for (int i = 0; i < instruments.Count; i++)
        {
            if (instruments[i].NoHardRestart)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every hard restart in the song keeps the waveform: some instrument has SOFTHR, and each of
    /// the others has it too or NOHR, which has no hard restart. The player then keeps the waveform
    /// as <see cref="KeepsWaveform"/> does, with no test of the flag and no $08 to fall back on - so
    /// a voice's first note, with nothing played before it, writes $00 where it wrote $08. 8 bytes.
    /// </summary>
    public static bool AllSoft(IReadOnlyList<Model.Instrument> instruments)
    {
        bool soft = false;
        for (int i = 0; i < instruments.Count; i++)
        {
            var instrument = instruments[i];
            if (!instrument.SoftHardRestart && !instrument.NoHardRestart)
                return false;
            soft |= instrument.SoftHardRestart;
        }

        return soft;
    }
}
