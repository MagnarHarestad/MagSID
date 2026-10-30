using MagSid.Core.Model;

namespace MagSid.Core;

/// <summary>Where a song's exported bytes go. Totals are the real assembled ones - see
/// <see cref="SongSizeCalculator"/> for how that is kept true.</summary>
public sealed record SongSize(
    int PlayerBytes,
    int PatternBytes,
    int PatternPointerBytes,
    int InstrumentTableBytes,
    int WavetablePoolBytes,
    int ArpeggioPoolBytes,
    int FilterPoolBytes,
    int OrderBytes,
    int NoteTableBytes)
{
    public int SongDataBytes =>
        PatternBytes + PatternPointerBytes + InstrumentTableBytes + WavetablePoolBytes +
        ArpeggioPoolBytes + FilterPoolBytes + OrderBytes + NoteTableBytes;

    public int TotalBytes => PlayerBytes + SongDataBytes;

    /// <summary>The last address the export occupies, given the player's own $1000 base.</summary>
    public int EndAddress => 0x1000 + TotalBytes - 1;
}

/// <summary>
/// How many bytes this song becomes once exported - the same number the .sid export produces,
/// without running the assembler. A hand-written mirror of Startup\Tables.s's second pass and
/// its four pooling loops, so the two can drift; SongSizeTests pins every figure against a real
/// c6510 build of each song in SongData, which is what stops that happening quietly.
/// </summary>
public static class SongSizeCalculator
{
    // Measured, not guessed: assemble export_sid.s against a one-instrument song with and
    // without each effect and read "player, song-free" out of tools\measure.py. The effects are
    // gated out of the player entirely when no instrument uses them, and they share the
    // frequency-modulation block, which is why both-at-once is less than the two deltas summed.
    // 2026-10-23: 4 bytes less throughout, 8 with vibrato - the row tables indexed by Y, the arp
    // start stored with STY, ALR for and+lsr and a redundant ldy when the vibrato turns round.
    // 2026-10-30: slide and vibrato 30, 32 and 40 bytes less - Voice_SetFreq joined to the
    // frequency write, and the tests for a rate or a depth of 0 gone. Then 7 less bare and 1 less
    // with either: Voice_SetFreq falls out of the note program, whose loop moved ahead of the entry.
    // Then 33 less, 31 with slides only: X runs 0, 7, 14, as in GoatTracker and SID Duzz It, so there
    // is no SID offset to load and Y is free for the instrument; voice_note went, and Voice_Tick
    // falls into Voice_PlayCurrent.
    private static readonly int PlayerBare = 418 + HardRestartBytes;
    private static readonly int PlayerVibrato = 500 + HardRestartBytes;
    private static readonly int PlayerSlide = 467 + HardRestartBytes;
    private static readonly int PlayerVibratoAndSlide = 526 + HardRestartBytes;

    /// <summary>Keeping the waveform through the hard restart loads and masks it rather than
    /// loading $08 - two bytes. The four figures above are the $08 player's.</summary>
    private static int HardRestartBytes => HardRestart.KeepsWaveform ? 2 : 0;

    /// <summary>The SOFTHR test (lda/bpl/lda/and/bne), assembled only when an instrument has the
    /// flag and the switch has not already made every hard restart soft.</summary>
    private const int SoftHardRestartBytes = 10;

    /// <summary>Funk speed's reload (lda/eor/sta/eor/sta) in place of move8's lda/sta.</summary>
    private const int FunkBytes = 6;

    /// <summary>The cpy/bcs that keeps instruments without vibrato out of the vibrato tables.</summary>
    private const int VibratoCompareBytes = 4;

    /// <summary>
    /// Instruments with vibrato come first in the player's numbering, so the vibrato tables can stop
    /// after them - if the player then compares, to keep the rest out. Two table bytes an instrument
    /// against the compare's four: from two instruments without vibrato it pays. One gets an entry of
    /// 0 instead, whose depth adds nothing.
    /// </summary>
    public static bool ComparesVibrato(Song song) =>
        song.Instruments.Any(i => i.HasVibrato)
        && song.Instruments.Count(i => !i.HasVibrato) >= 2;

    /// <summary>How long vib_ds and vib_depth each are.</summary>
    public static int VibratoTableEntries(Song song) =>
        !song.Instruments.Any(i => i.HasVibrato) ? 0
        : ComparesVibrato(song) ? song.Instruments.Count(i => i.HasVibrato)
        : song.Instruments.Count;

    private const int CmdDuration = 0x80;
    private const int CmdInstrument = 0xC0;
    private const int CmdNoteOff = 0xD0;
    private const int CmdWait = 0xD1;
    private const int CmdLoopEnd = 0xD2;
    private const int CmdSlide = 0xD3;

    /// <summary>A "1xx"/"2xx" row anywhere in the song - what assembles the slide code in.</summary>
    public static bool UsesSlide(Song song) =>
        song.Patterns.Any(p => p.Rows.Any(r => r is SlideRow));

    public static SongSize Measure(Song song)
    {
        bool anyVibrato = song.Instruments.Any(i => i.HasVibrato);
        bool anySlide = UsesSlide(song);

        int player = (anyVibrato, anySlide) switch
        {
            (false, false) => PlayerBare,
            (true, false) => PlayerVibrato,
            (false, true) => PlayerSlide,
            (true, true) => PlayerVibratoAndSlide,
        };

        if (!HardRestart.KeepsWaveform && song.Instruments.Any(i => i.SoftHardRestart))
            player += SoftHardRestartBytes;

        if (song.HasFunk)
            player += FunkBytes;

        if (ComparesVibrato(song))
            player += VibratoCompareBytes;

        int patternBytes = song.Patterns.Sum(p => EncodedLength(p.Rows));

        // ad, sr, wt_start, arp_start, flt_start, pw_init, pw_add - one byte each per
        // instrument, plus the two vibrato tables when those are in play.
        int instrumentCount = song.Instruments.Count;

        return new SongSize(
            PlayerBytes: player,
            PatternBytes: patternBytes,
            // tbl_rows_lo and tbl_rows_hi, indexed by 1-based pattern number from a label one byte
            // before each.
            PatternPointerBytes: 2 * song.Patterns.Count,
            InstrumentTableBytes: 7 * instrumentCount + 2 * VibratoTableEntries(song),
            // Offset 0 of a pool means "nothing running" and is never read, so it is not emitted.
            WavetablePoolBytes: WavetablePoolLength(song) - 1,
            ArpeggioPoolBytes: ArpeggioPoolLength(song) - 1,
            FilterPoolBytes: FilterPoolLength(song) - 1,
            // Per channel: the pattern numbers, a $00 terminator and the restart position.
            OrderBytes: song.ChannelOrders.Sum(o => o.Tokens.Count + 2),
            // note_freq_low and note_freq_high, from labels one byte before each: no note is 0.
            NoteTableBytes: 2 * DistinctNoteCount(song));
    }

    /// <summary>
    /// One pattern's byte stream, terminator included. Mirrors the emitter exactly: a note costs
    /// one byte and carries whatever instrument and duration are already current, so only a
    /// change emits a command; a run of "..." rows is folded into the preceding note's duration;
    /// and LE splices the anchor's state back in when the body changed it.
    /// </summary>
    public static int EncodedPatternLength(IReadOnlyList<PatternRow> rows) => EncodedLength(rows);

    private static int EncodedLength(IReadOnlyList<PatternRow> rows)
    {
        var stream = new List<int>();
        int? curInst = null, curDur = null;
        int lsTarget = 0;
        int? lsInst = null, lsDur = null;
        int consumedUntil = -1;

        void SetState(int? inst, int? dur)
        {
            if (inst is not null && inst != curInst)
            {
                stream.Add(CmdInstrument + inst.Value - 1);
                curInst = inst;
            }
            if (dur is not null && dur != curDur)
            {
                stream.Add(CmdDuration + dur.Value - 1);
                curDur = dur;
            }
        }

        // Stops at a loop marker: a note must not swallow holds from the far side of one, since
        // those only play on the pass that actually reaches them.
        int CountHolds(int from)
        {
            int n = 0;
            while (from < rows.Count && rows[from] is HoldRow)
            {
                n++;
                from++;
            }
            return n;
        }

        for (int idx = 0; idx < rows.Count; idx++)
        {
            switch (rows[idx])
            {
                case LoopStartRow:
                    lsTarget = stream.Count;
                    lsInst = curInst;
                    lsDur = curDur;
                    break;

                case LoopEndRow(var repeatCount):
                {
                    var fixup = new List<int>();
                    if (lsInst is not null && curInst != lsInst)
                        fixup.Add(CmdInstrument + lsInst.Value - 1);
                    if (lsDur is not null && curDur != lsDur)
                        fixup.Add(CmdDuration + lsDur.Value - 1);
                    for (int i = 0; i < fixup.Count; i++)
                        stream.Insert(lsTarget + i, fixup[i]);

                    stream.Add(CmdLoopEnd);
                    stream.Add(repeatCount ?? 0);   // 0 = bare LE, loops forever
                    stream.Add(lsTarget);
                    break;
                }

                case HoldRow:
                    // A hold no preceding note could absorb still has to cost time.
                    if (idx > consumedUntil)
                    {
                        int duration = 1 + CountHolds(idx + 1);
                        SetState(null, duration);
                        stream.Add(CmdWait);
                        consumedUntil = idx + duration - 1;
                    }
                    break;

                case NoteOffRow:
                {
                    int duration = 1 + CountHolds(idx + 1);
                    consumedUntil = idx + duration - 1;
                    SetState(null, duration);   // a note-off uses no instrument
                    stream.Add(CmdNoteOff);
                    break;
                }

                case SlideRow slide:
                {
                    int duration = 1 + CountHolds(idx + 1);
                    consumedUntil = idx + duration - 1;
                    SetState(null, duration);
                    stream.Add(CmdSlide);
                    stream.Add(slide.Rate & 0xFF);
                    break;
                }

                case NoteRow note:
                {
                    int duration = 1 + CountHolds(idx + 1);
                    consumedUntil = idx + duration - 1;
                    SetState(note.InstrumentNumber, duration);
                    stream.Add(0x01);           // any note id is one byte
                    break;
                }
            }
        }

        return stream.Count + 1;                // $00 end of pattern
    }

    /// <summary>Offset 0 is a lone $00 that no instrument points at - it is the "not running"
    /// value the player tests voice_wt_pos against. Identical programs share one entry.</summary>
    private static int WavetablePoolLength(Song song)
    {
        int length = 1;
        var seen = new HashSet<string>();
        foreach (var instrument in song.Instruments)
        {
            var program = instrument.Wavetable.Count > 0
                ? instrument.Wavetable.Select(b => (int)b).ToList()
                : [instrument.Waveform | 0x01];  // the gate bit is what starts the note
            program.Add(0x00);                   // hold on the last waveform

            if (seen.Add(string.Join(",", program)))
                length += program.Count;
        }
        return length;
    }

    /// <summary>Same shape as the wavetable pool. How a program ends is the whole difference
    /// between an arpeggio and a drum: Root is a bare $80 with no terminator, everything else
    /// ends $00 plus the offset to resume at.</summary>
    private static int ArpeggioPoolLength(Song song)
    {
        int length = 1;
        var seen = new HashSet<string>();
        foreach (var instrument in song.Instruments)
        {
            if (!instrument.HasArpeggio)
                continue;

            string key = string.Join(" ", instrument.Arpeggio.Select(n => n.ToString()))
                         + "|" + instrument.ArpeggioEnding + "|" + instrument.ArpeggioLoopPoint;
            if (!seen.Add(key))
                continue;

            length += instrument.Arpeggio.Count;
            length += instrument.ArpeggioEnding == ArpeggioEnding.Root
                ? 1                              // $80, and it stays there
                : 2;                             // $00 plus the resume offset
        }
        return length;
    }

    /// <summary>A plain cutoff step is one byte; a setup record is $FF plus mode+volume,
    /// resonance+routing, sweep rate and cutoff. $00 ends a program.</summary>
    private static int FilterPoolLength(Song song)
    {
        int length = 1;
        var seen = new HashSet<string>();
        foreach (var instrument in song.Instruments)
        {
            if (instrument.Filter.Count == 0)
                continue;

            int bytes = instrument.Filter.Sum(step => step.IsSetup ? 5 : 1) + 1;
            string key = string.Join(",", instrument.Filter.Select(s =>
                s.IsSetup
                    ? $"S{s.ModeVolume}:{s.ResonanceRouting}:{s.SweepRate}:{s.Cutoff}"
                    : $"C{s.Cutoff}"));
            if (seen.Add(key))
                length += bytes;
        }
        return length;
    }

    /// <summary>The note dictionary is global and built in first-occurrence order - pattern rows
    /// first, then whatever else the arpeggio programs need. Only the count matters here, since
    /// every id costs the same two table bytes.</summary>
    private static int DistinctNoteCount(Song song)
    {
        var notes = new HashSet<NoteName>();
        foreach (var pattern in song.Patterns)
            foreach (var row in pattern.Rows)
                if (row is NoteRow note)
                    notes.Add(note.Note);

        foreach (var instrument in song.Instruments)
            foreach (var step in instrument.Arpeggio)
                notes.Add(step);

        return notes.Count;
    }
}
