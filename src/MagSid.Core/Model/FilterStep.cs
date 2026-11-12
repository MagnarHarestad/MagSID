namespace MagSid.Core.Model;

/// <summary>
/// One frame of an instrument's filter program, mirroring the FLT section in the
/// INSTRUMENTS: block and the flt_data byte stream the assembler builds from it.
///
/// Two shapes, distinguished by <see cref="IsSetup"/>:
/// a bare sweep step, which only moves the cutoff high byte ($D416), and a setup step,
/// which also writes the filter mode plus master volume ($D418) and the resonance plus
/// voice routing ($D417). A program normally opens with a setup step and continues with
/// sweep steps.
///
/// <see cref="Cutoff"/> is restricted to $01..$FE because the assembled stream spends
/// $FF on the setup marker and $00 on the end marker. That is not a loss worth working
/// around: both excluded values are degenerate cutoff settings.
/// </summary>
public readonly record struct FilterStep
{
    public const byte MinCutoff = 0x01;
    public const byte MaxCutoff = 0xFE;

    /// <summary>$D418 - filter mode in the high nibble, master volume in the low. Null on a
    /// sweep step.
    ///
    /// Bit 7 (3OFF, disconnect voice 3 from the output) is always 0 here - see
    /// <see cref="Setup"/>. This player never uses 3OFF, and GoatTracker users habitually write
    /// this byte as 9x/bx/dx/fx anyway (there that bit means something else, a file-format
    /// marker, not the SID's own 3OFF), so a byte arriving with it set means the same mode and
    /// volume as the same byte without it.</summary>
    public byte? ModeVolume { get; private init; }

    /// <summary>$D417 - resonance in the high nibble, which voices pass through the filter in
    /// the low. Null on a sweep step.</summary>
    public byte? ResonanceRouting { get; private init; }

    /// <summary>
    /// How far the cutoff moves a frame from here on, signed, 0 to stop a sweep. Only a setup step
    /// carries one.
    ///
    /// The sweep goes back and forth between this step's cutoff and <see cref="SweepEnd"/> long
    /// after the program has finished, so it is a property of the filter's configuration rather
    /// than a step of its own. A sweep already running when a setup with a rate comes along keeps
    /// going as it is.
    /// </summary>
    public int SweepRate { get; private init; }

    /// <summary>Where a sweep turns back towards the cutoff it started from: always a whole number
    /// of rates away, so the sweep lands on it. 0 without a sweep.</summary>
    public int SweepEnd { get; private init; }

    /// <summary>$D416 - the cutoff frequency's high byte, written by every step.</summary>
    public byte Cutoff { get; private init; }

    public bool IsSetup => ModeVolume.HasValue;

    /// <summary>The filter modes a setup step switches on, as the editor's buttons name them: "LP",
    /// "LP+HP", or "off" with none. Null for a sweep step, which leaves the mode alone.</summary>
    public string? ModeText
    {
        get
        {
            if (ModeVolume is not { } mode)
                return null;

            var names = new List<string>(3);
            if ((mode & 0x10) != 0)
                names.Add("LP");
            if ((mode & 0x20) != 0)
                names.Add("BP");
            if ((mode & 0x40) != 0)
                names.Add("HP");
            return names.Count == 0 ? "off" : string.Join('+', names);
        }
    }

    public static FilterStep Sweep(byte cutoff) => new() { Cutoff = cutoff };

    /// <summary>Masks off bit 7 of <paramref name="modeVolume"/> (3OFF) unconditionally, so
    /// every step this factory produces already carries the invariant <see cref="ModeVolume"/>
    /// documents - regardless of whether the byte came from a hand-typed GoatTracker-habit
    /// value, an existing file authored the same way, or anywhere else. One place, so nothing
    /// downstream (playback, export, the editor's grid) has to re-derive or repeat it.</summary>
    public static FilterStep Setup(byte modeVolume, byte resonanceRouting, byte cutoff,
                                  int sweepRate = 0, int? sweepEnd = null) => new()
    {
        ModeVolume = (byte)(modeVolume & 0x7f),
        ResonanceRouting = resonanceRouting,
        Cutoff = cutoff,
        SweepRate = sweepRate,
        SweepEnd = sweepRate == 0 ? 0 : ReachableEnd(cutoff, sweepRate, sweepEnd),
    };

    /// <summary>
    /// The end a sweep from <paramref name="start"/> can land on: the one asked for, or the last
    /// before it, at least one step out and inside a byte. With none asked for, as far as the rate
    /// goes. Startup\Tables.s does the same for a file it reads.
    /// </summary>
    public static int ReachableEnd(int start, int rate, int? end)
    {
        int target = end ?? (rate > 0 ? 0xFF : 0x00);
        int steps = (target - start) / rate;
        int room = rate > 0 ? (0xFF - start) / rate : start / -rate;
        return start + (Math.Clamp(steps, 1, Math.Max(1, room)) * rate);
    }

    /// <summary>The same step with another cutoff, kept inside $01..$FE - $00 ends a program and
    /// $FF opens a setup record, so neither is a cutoff. A sweep's end moves to stay reachable.</summary>
    public FilterStep WithCutoff(int cutoff)
    {
        byte kept = (byte)Math.Clamp(cutoff, MinCutoff, MaxCutoff);
        return IsSetup ? Setup(ModeVolume!.Value, ResonanceRouting!.Value, kept, SweepRate, SweepEnd) : Sweep(kept);
    }
}
