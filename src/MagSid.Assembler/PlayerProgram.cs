namespace MagSid.Assembler;

using static Operand;

/// <summary>The song tables the player reads. They are emitted after the code, so they arrive here
/// as labels the caller has not placed yet - the reason the emitter resolves references late.
/// VibDs/VibDepth are null exactly when vibrato is off.</summary>
public sealed class SongTableLabels
{
    public required Label TblRowsLo { get; init; }
    public required Label TblRowsHi { get; init; }
    public required Label TblSongOrder { get; init; }
    public required Label NoteFreqLow { get; init; }
    public required Label NoteFreqHigh { get; init; }
    public required Label AdTable { get; init; }
    public required Label SrTable { get; init; }
    public required Label WtStart { get; init; }
    public required Label WtData { get; init; }
    public required Label ArpStart { get; init; }
    public required Label ArpData { get; init; }
    public required Label FltStart { get; init; }
    public required Label FltData { get; init; }
    public required Label PwInit { get; init; }
    public required Label PwAdd { get; init; }

    public Label? VibDs { get; init; }
    public Label? VibDepth { get; init; }

    /// <summary>wt_hold - wt_data: the wavetable pool's last byte, always the $00 that ends the
    /// last program. A note off parks voice_wt_pos there.</summary>
    public required int WtHold { get; init; }
}

/// <summary>Where a Musicshower's build of the player also leaves what it otherwise keeps nowhere,
/// all in zero page: the row's note or command per voice, at the player's stride, and the three
/// filter registers, which cannot be read back. Null for anything else, and the code is as it was.</summary>
public sealed record PlayerShadows(int Note, int FilterCutoff, int FilterResonance, int FilterModeVolume);

/// <summary>The per-song constants baked into the player code: v_Song_Speed and the three
/// v_ord_startN values that seed voice_orderpos.</summary>
public sealed record PlayerParameters(int SongSpeed, int OrderStart1, int OrderStart2, int OrderStart3)
{
    /// <summary>v_Song_Funk: the speed every second tick has, with PlayerFeatures.Funk.</summary>
    public int FunkSpeed { get; init; }

    /// <summary>v_vib_count: instruments with vibrato come first and the tables stop after them, so
    /// the player compares against this. Null when the tables go on to the end - see
    /// SongSizeCalculator.ComparesVibrato.</summary>
    public int? VibratoCompare { get; init; }

    /// <summary>v_channels. Three throughout this project; named rather than inlined because the
    /// assembly writes "ldx #v_channels-1" and reading 2 there invites the off-by-one.</summary>
    public int Channels { get; init; } = 3;
}

/// <summary>
/// Code\musicplayer.s, emitted as bytes.
///
/// A line-for-line port, kept in the source's own order because that order is load-bearing: branch
/// distances depend on it, and so does the fall-through from Filter_Play into the sweep and from
/// the command dispatch into .loopend. Where the assembly uses a library macro (move8, m_setptr_x)
/// the expansion is written out, since that is what the macro emits.
///
/// The musicshower's stores are in only when <see cref="PlayerShadows"/> asks for them, which only
/// ShowerProgram does: a .sid has no use for them, and the images c6510 built do not have them.
///
/// X runs 0, 7, 14 through every per-voice routine: the arrays sit at that stride in zero page (see
/// ZeroPageLayout) and so do the SID's voices, so one index reaches both, and Y is free for the
/// instrument. The routines are in the order they fall through: Voice_Tick into Voice_PlayCurrent,
/// Voice_NoteOff in reach of its branch.
///
/// PlayerImageTests compares the result byte for byte against what c6510 assembles from the real
/// source. That comparison is the only reason to trust any of this: a transcription slip here
/// yields a player that runs and sounds subtly wrong, which no amount of reading catches.
/// </summary>
public static class PlayerProgram
{
    // Pattern-stream command bytes. Same values as the vars at the top of musicplayer.s.
    private const int CmdDur = 0x80;
    private const int CmdInst = 0xC0;
    private const int CmdOff = 0xD0;
    private const int LoopEnd = 0xD2;
    private const int CmdSlide = 0xD3;
    private const int RowWait = 0xD1;

    /// <summary>A reaches Voice_PlayCurrent holding a note id or one of these, deliberately the same
    /// bytes the stream uses, so Voice_Tick can hand the command over as it stands.</summary>
    private const int NoteOff = CmdOff;

    // SID registers, written through X: a voice's index is its offset in the SID too.
    private const int SidFreqLo = 0xD400;
    private const int SidFreqHi = 0xD401;
    private const int SidPwLo = 0xD402;
    private const int SidPwHi = 0xD403;
    private const int SidControl = 0xD404;
    private const int SidAttackDecay = 0xD405;
    private const int SidSustainRelease = 0xD406;
    private const int SidFilterCutoffHi = 0xD416;
    private const int SidFilterResonance = 0xD417;
    private const int SidFilterModeVolume = 0xD418;

    public static void Emit(
        Emitter6502 asm,
        SongTableLabels tables,
        PlayerParameters parameters,
        PlayerFeatures features,
        ZeroPageLayout zp,
        PlayerShadows? shadows = null)
    {
        RequireTables(tables, features);

        // Every label up front, because the code references most of them before defining them and
        // grouping them here keeps the emit sequence readable as assembly.
        var musicInit = asm.NewLabel("music.init");
        var musicPlay = asm.NewLabel("music.play");
        var voiceNoteOff = asm.NewLabel("Voice_NoteOff");
        var voiceAdvanceOrder = asm.NewLabel("Voice_AdvanceOrder");
        var voiceSetFreq = asm.NewLabel("Voice_SetFreq");
        var voiceAutomation = asm.NewLabel("Voice_Automation");
        var voiceTick = asm.NewLabel("Voice_Tick");
        var onBeatPlaySong = asm.NewLabel("ONbeat.PlaySong");

        // --- JMPs for Init and Play ---
        asm.Jmp(musicInit);
        asm.Jmp(musicPlay);

        EmitVoiceAdvanceOrder(asm, tables, zp, voiceAdvanceOrder);
        EmitVoiceAutomation(asm, tables, parameters, features, zp, voiceAutomation, voiceSetFreq);
        EmitMusicInit(asm, parameters, zp, musicInit, voiceAdvanceOrder, shadows);
        EmitMusicPlay(asm, tables, parameters, features, zp, musicPlay, voiceAutomation, voiceTick, onBeatPlaySong, shadows);
        EmitVoiceTick(asm, tables, features, zp, voiceTick, voiceAdvanceOrder);
        EmitVoicePlayCurrent(asm, tables, features, zp, voiceNoteOff, voiceSetFreq, shadows);
        EmitVoiceNoteOff(asm, tables, zp, voiceNoteOff);
    }

    /// <summary>The first voice's index, for a loop running down to 0.</summary>
    private static Operand LastVoice(PlayerParameters parameters) => Imm((parameters.Channels - 1) * ZeroPageLayout.VoiceStride);

    /// <summary>On to the next voice down, back to <paramref name="loop"/> while there is one.</summary>
    private static void NextVoice(Emitter6502 asm, Label loop)
    {
        asm.Txa();
        asm.Axs(Imm(ZeroPageLayout.VoiceStride));
        asm.Bpl(loop);
    }

    private static void RequireTables(SongTableLabels tables, PlayerFeatures features)
    {
        if (features.Vibrato && (tables.VibDs is null || tables.VibDepth is null))
            throw new ArgumentException("A song with vibrato needs both vib_ds and vib_depth.", nameof(tables));
    }

    /// <summary>Clears the gate bit, keeping the waveform-select bits - the oscillator would go
    /// instantly silent otherwise, masking the envelope's release. The wavetable is parked on a
    /// $00 hold rather than stopped, so it never puts the gate back on while pulse, note program,
    /// vibrato and slide run on through the release.</summary>
    private static void EmitVoiceNoteOff(Emitter6502 asm, SongTableLabels tables, ZeroPageLayout zp, Label entry)
    {
        asm.Define(entry);
        asm.Lda(MemX(zp.VoiceLastWaveform));
        asm.And(Imm(0xFE));
        asm.Sta(MemX(SidControl));
        asm.Move8(Imm(tables.WtHold), MemX(zp.VoiceWtPos));
        asm.Rts();
    }

    /// <summary>Song order: $00 ends a list and the next byte is the absolute restart position. All
    /// three channels' lists share one array.</summary>
    private static void EmitVoiceAdvanceOrder(Emitter6502 asm, SongTableLabels tables, ZeroPageLayout zp, Label entry)
    {
        asm.Define(entry);
        var read = asm.DefineHere(".read");
        asm.Ldy(MemX(zp.VoiceOrderPos));
        asm.Lda(MemY(tables.TblSongOrder));
        var endOfList = asm.NewLabel(".endoflist");
        asm.Beq(endOfList);                         // the common path falls through
        asm.Sta(MemX(zp.VoicePatternIdx));
        // Not iny/tya/sta: Y is dead from here, so incrementing the array in place is cheaper.
        asm.Inc(MemX(zp.VoiceOrderPos));
        asm.Lda(Imm(0));
        asm.Sta(MemX(zp.VoiceEvent));
        asm.Sta(MemX(zp.VoiceLoopCounter));
        asm.Rts();

        asm.Define(endOfList);
        asm.Iny();
        asm.Lda(MemY(tables.TblSongOrder));         // absolute restart position
        asm.Sta(MemX(zp.VoiceOrderPos));
        asm.Jmp(read);
    }

    /// <summary>A = the row's note or command, X = voice. Voice_Tick falls into this.</summary>
    private static void EmitVoicePlayCurrent(
        Emitter6502 asm, SongTableLabels tables, PlayerFeatures features, ZeroPageLayout zp,
        Label voiceNoteOff, Label voiceSetFreq, PlayerShadows? shadows)
    {
        var leaveSounding = asm.NewLabel(".leavesounding");

        if (shadows is not null)
            asm.Sta(MemX(shadows.Note));

        asm.Cmp(Imm(NoteOff));
        asm.Beq(voiceNoteOff);
        // bcs, not a second cmp: the compare above already left C=1 for everything above $D0, so
        // one branch separates all three cases. A wait row lets whatever is playing keep going.
        asm.Bcs(leaveSounding);

        // The row's own note, kept for a ROOT step in the note program.
        asm.Sta(MemX(zp.VoiceRoot));

        // sty, so A keeps the note. Before the jsr: with slide or vibrato it writes the note plus
        // its offset, and a new note starts from the bare note. Carry is clear from the bcs above,
        // which is what tells Voice_SetFreq to write and not to step the offset.
        if (features.FreqMod)
        {
            asm.Ldy(Imm(0));
            if (features.Vibrato)
                asm.Sty(MemX(zp.VoiceVibDir));
            asm.Sty(MemX(zp.VoiceOffLo));
            asm.Sty(MemX(zp.VoiceOffHi));
            if (features.Slide)
                asm.Sty(MemX(zp.VoiceSlide));       // and a new note starts without a slide
        }

        asm.Jsr(voiceSetFreq);

        // Hard restart unless NOHR, bit 5 of the pw_init byte belonging to the instrument ALREADY
        // sounding here, which voice_prev_pw keeps: zero AD, release 7, gate off (waveform kept, or
        // $08 - see MagSid.Core.HardRestart). Release 7 and not 0 because the envelope rate counter
        // is never reset by a write - a period below where it sits wraps it through 15 bits and the
        // next attack stalls.
        asm.Lda(MemX(zp.VoicePrevPw));
        asm.And(Imm(0x20));
        var skipHardReset = asm.NewLabel(".skiphardreset");
        asm.Bne(skipHardReset);
        // A is already $00 - it is the "and #$20" result the bne just proved to be zero, so the
        // usual "lda #$00" in front of this is dead.
        asm.Sta(MemX(SidAttackDecay));
        asm.Lda(Imm(0x07));
        asm.Sta(MemX(SidSustainRelease));
        // See MagSid.Core.HardRestart: gate off with the waveform kept, or $08.
        if (Core.HardRestart.KeepsWaveform)
        {
            asm.Lda(MemX(zp.VoiceLastWaveform));
            asm.And(Imm(0xFE));
        }
        else
        {
            // SOFTHR is bit 7 of the outgoing instrument's pw_init, which voice_prev_pw holds -
            // the same byte NOHR is read from. Only assembled when some instrument has it.
            var writeControl = asm.NewLabel(".writecontrol");
            var hardControl = asm.NewLabel(".hardcontrol");
            if (features.SoftHardRestart)
            {
                asm.Lda(MemX(zp.VoicePrevPw));
                asm.Bpl(hardControl);
                asm.Lda(MemX(zp.VoiceLastWaveform));
                asm.And(Imm(0xFE));
                asm.Bne(writeControl);              // a last waveform of nothing falls back to $08
            }

            asm.Define(hardControl);
            asm.Lda(Imm(0x08));
            asm.Define(writeControl);
        }

        asm.Sta(MemX(SidControl));
        asm.Define(skipHardReset);

        asm.Ldy(MemX(zp.VoiceInst));
        asm.Lda(MemY(tables.WtStart));
        asm.Sta(MemX(zp.VoiceWtPos));
        asm.Lda(MemY(tables.ArpStart));             // 0 for an instrument with no arpeggio
        asm.Sta(MemX(zp.VoiceArpPos));
        asm.Lda(MemY(tables.FltStart));
        var noFilter = asm.NewLabel(".nofilter");
        asm.Beq(noFilter);                          // instrument leaves the filter alone
        asm.Sta(Mem(zp.FilterPos));
        asm.Define(noFilter);

        asm.Lda(MemY(tables.PwInit));
        asm.Sta(MemX(zp.VoicePrevPw));              // for the next trigger's NOHR test above
        asm.And(Imm(0x40));
        // bit 6 set: continuous - the sweep is per channel, so leave it running across the trigger.
        var arm = asm.NewLabel(".arm");
        asm.Bne(arm);
        asm.Lda(MemX(zp.VoicePrevPw));
        asm.Sta(MemX(zp.VoicePwHi));                // a note restarts the sweep from the
        asm.Lda(Imm(0));                            // instrument's own starting width
        asm.Sta(MemX(zp.VoicePwLo));
        asm.Define(arm);

        if (features.Vibrato)
        {
            // Arm this note's vibrato: delay from vib_ds's top nibble (x4 frames), first run half a
            // speed long (low nibble >> 1), which centres the wave on the note.
            asm.Lda(MemY(tables.VibDs!));
            asm.Tay();                              // Y is free from here on, and cheaper than a reload
            asm.Asr(Imm(0xF0));                     // ALR: and #$f0 + lsr, same flags
            asm.Lsr();
            asm.Sta(MemX(zp.VoiceVibDelay));
            asm.Tya();
            asm.Asr(Imm(0x0F));                     // ALR: and #$0f + lsr
            asm.Sta(MemX(zp.VoiceVibCnt));
        }

        asm.Define(leaveSounding);
        asm.Rts();
    }

    /// <summary>One wavetable step plus ADSR for channel X, every frame rather than every tick - an
    /// attack transient needs frame resolution. Pool offset 0 is a dummy $00, so voice_wt_pos = 0
    /// doubles as "nothing played yet". A released note sits on wt_hold and runs on.</summary>
    private static void EmitVoiceAutomation(
        Emitter6502 asm, SongTableLabels tables, PlayerParameters parameters, PlayerFeatures features, ZeroPageLayout zp,
        Label entry, Label voiceSetFreq)
    {
        var idle = asm.NewLabel(".idle");
        var freqMod = asm.NewLabel(".freqmod");
        var arpStep = asm.NewLabel(".arpstep");
        var arpLoop = asm.NewLabel(".arploop");
        // Where the note program's "no program" branch goes.
        Label afterArp = features.FreqMod ? freqMod : idle;

        // The note program's $00: the next byte says where to resume, which is never 0 - the song
        // tables refuse it - so this always branches. Here, ahead of the entry, because what it
        // would fall into is harmless, and the rts below is in reach of the entry.
        asm.Define(arpLoop);
        asm.Iny();
        asm.Lda(MemY(tables.ArpData));
        asm.Sta(MemX(zp.VoiceArpPos));
        asm.Tay();
        asm.Bne(arpStep);
        if (features.FreqMod)
        {
            asm.Define(idle);
            asm.Rts();
        }

        asm.Define(entry);
        asm.Ldy(MemX(zp.VoiceWtPos));
        asm.Beq(idle);

        asm.Lda(MemY(tables.WtData));
        var adsr = asm.NewLabel(".adsr");
        asm.Beq(adsr);                              // $00 = hold on the current waveform
        asm.Sta(MemX(SidControl));
        asm.Sta(MemX(zp.VoiceLastWaveform));        // remembered for Voice_NoteOff
        asm.Inc(MemX(zp.VoiceWtPos));
        asm.Define(adsr);

        asm.Ldy(MemX(zp.VoiceInst));
        asm.Lda(MemY(tables.AdTable));
        asm.Sta(MemX(SidAttackDecay));
        asm.Lda(MemY(tables.SrTable));
        asm.Sta(MemX(SidSustainRelease));

        // Pulse width. $D402/$D403 are write-only, so the width lives in a per-channel shadow and
        // is rewritten every frame. The delta is sign-extended into all 12 bits, so the sweep
        // travels the whole register and wraps.
        asm.Lda(MemY(tables.PwAdd));
        asm.Pha();
        asm.Clc();
        asm.Adc(MemX(zp.VoicePwLo));
        asm.Sta(MemX(zp.VoicePwLo));
        asm.Sta(MemX(SidPwLo));
        asm.Pla();                                  // the delta again. None of pla/and/beq/lda
        asm.And(Imm(0x80));                         // touches the carry flag either
        var pwHigh = asm.NewLabel(".pwhigh");
        asm.Beq(pwHigh);
        asm.Lda(Imm(0xFF));                         // sign extension - a downward sweep
        asm.Define(pwHigh);
        asm.Adc(MemX(zp.VoicePwHi));
        asm.And(Imm(0x0F));
        asm.Sta(MemX(zp.VoicePwHi));
        asm.Sta(MemX(SidPwHi));

        // Note program: one note per frame alongside the wavetable. Absolute note ids, except ROOT
        // which plays the row's own note - that pair is what makes a drum or bass transient
        // expressible.
        asm.Ldy(MemX(zp.VoiceArpPos));
        asm.Beq(afterArp);                          // instrument has no note program

        asm.Define(arpStep);
        asm.Lda(MemY(tables.ArpData));
        asm.Beq(arpLoop);                           // $00 = end marker, next byte says where to resume
        var arpNote = asm.NewLabel(".arpnote");
        asm.Bpl(arpNote);                           // $01..$7F = a note dictionary id
                                                    // else bit 7 set = exactly $80 = ROOT
        asm.Lda(MemX(zp.VoiceRoot));
        asm.Bytes(0x2C);                            // bit abs: steps over the inc, a byte under a branch
        asm.Define(arpNote);
        asm.Inc(MemX(zp.VoiceArpPos));

        if (features.FreqMod)
        {
            asm.Sec();                              // a step: on through the offset, unlike a new note
            EmitFreqMod(asm, tables, parameters, features, zp, voiceSetFreq, freqMod);
        }
        else
        {
            // A = note dictionary id, X = voice. Writes the frequency.
            asm.Define(voiceSetFreq);
            asm.Tay();
            asm.Lda(MemY(tables.NoteFreqLow));
            asm.Sta(MemX(SidFreqLo));
            asm.Lda(MemY(tables.NoteFreqHigh));
            asm.Sta(MemX(SidFreqHi));
            asm.Define(idle);
            asm.Rts();
        }
    }

    /// <summary>
    /// A = note dictionary id, X = channel. Keeps the note, which the SID's registers cannot be read
    /// back for; then a note program (carry set) steps the offset, and a new note (carry clear) does
    /// not.
    ///
    /// Then slide and vibrato, last in Voice_Automation because a note program sets a new bare note
    /// every frame. Both move one signed 16-bit offset, which is added back on top of the note and
    /// written every frame - that is what keeps a slide that "100" stopped where it got to. Slide
    /// adds the rate a pattern's slide row left in voice_slide a frame, vibrato traces a triangle
    /// from vib_ds (delay/speed) and vib_depth. A rate of 0 adds nothing, so it is not tested for:
    /// that would only save cycles, and this player spends those for bytes.
    /// </summary>
    private static void EmitFreqMod(
        Emitter6502 asm, SongTableLabels tables, PlayerParameters parameters, PlayerFeatures features, ZeroPageLayout zp,
        Label voiceSetFreq, Label freqMod)
    {
        var fmWrite = asm.NewLabel(".fmwrite");

        asm.Define(voiceSetFreq);
        asm.Tay();
        asm.Lda(MemY(tables.NoteFreqLow));
        asm.Sta(MemX(zp.VoiceFreqLo));
        asm.Lda(MemY(tables.NoteFreqHigh));
        asm.Sta(MemX(zp.VoiceFreqHi));
        asm.Bcc(fmWrite);

        asm.Define(freqMod);
        if (features.Slide)
        {
            // offset += rate, sign-extended.
            var slideUp = asm.NewLabel(".slideup");
            var slideDone = asm.NewLabel(".slidedone");
            asm.Lda(MemX(zp.VoiceSlide));
            asm.Bpl(slideUp);
            asm.Dec(MemX(zp.VoiceOffHi));
            asm.Define(slideUp);
            asm.Clc();
            asm.Adc(MemX(zp.VoiceOffLo));
            asm.Sta(MemX(zp.VoiceOffLo));
            asm.Bcc(slideDone);
            asm.Inc(MemX(zp.VoiceOffHi));
            asm.Define(slideDone);
        }

        if (features.Vibrato)
        {
            asm.Ldy(MemX(zp.VoiceInst));

            // Instruments with vibrato come first: the tables stop there, so the rest stay out.
            if (parameters.VibratoCompare is { } count)
            {
                asm.Cpy(Imm(count));
                asm.Bcs(fmWrite);
            }

            // Counts down to 0 and stays there: the inc puts back what going past 0 took off.
            asm.Dec(MemX(zp.VoiceVibDelay));
            asm.Bpl(fmWrite);
            asm.Inc(MemX(zp.VoiceVibDelay));

            // offset += depth (dir $00) or -= depth (dir $ff). depth EOR dir is depth or ~depth, and
            // the carry - bit 7 of dir, out of the asl - supplies the +1 that turns ~depth into
            // -depth; dir itself is the sign extension for the high byte.
            asm.Lda(MemX(zp.VoiceVibDir));
            asm.Asl();
            asm.Lda(MemY(tables.VibDepth!));
            asm.Eor(MemX(zp.VoiceVibDir));
            asm.Adc(MemX(zp.VoiceOffLo));
            asm.Sta(MemX(zp.VoiceOffLo));
            asm.Lda(MemX(zp.VoiceVibDir));
            asm.Adc(MemX(zp.VoiceOffHi));
            asm.Sta(MemX(zp.VoiceOffHi));
            asm.Dec(MemX(zp.VoiceVibCnt));
            asm.Bpl(fmWrite);
            asm.Lda(MemX(zp.VoiceVibDir));          // run finished: turn round and reload the speed
            asm.Eor(Imm(0xFF));
            asm.Sta(MemX(zp.VoiceVibDir));
            asm.Lda(MemY(tables.VibDs!));
            asm.And(Imm(0x0F));
            asm.Sta(MemX(zp.VoiceVibCnt));
        }

        asm.Define(fmWrite);
        asm.Lda(MemX(zp.VoiceFreqLo));
        asm.Clc();
        asm.Adc(MemX(zp.VoiceOffLo));
        asm.Sta(MemX(SidFreqLo));
        asm.Lda(MemX(zp.VoiceFreqHi));
        asm.Adc(MemX(zp.VoiceOffHi));
        asm.Sta(MemX(SidFreqHi));
        asm.Rts();
    }

    private static void EmitMusicInit(
        Emitter6502 asm, PlayerParameters parameters, ZeroPageLayout zp, Label entry, Label voiceAdvanceOrder,
        PlayerShadows? shadows)
    {
        asm.Define(entry);

        // ZP is not part of the loaded image, so state that used to be initialised by its own
        // ".byte 0,0,0" is cleared here - before the Voice_AdvanceOrder loop, which writes
        // voice_event and voice_loop_counter.
        asm.Lda(Imm(0));
        asm.Ldx(Imm(zp.ByteCount - 1));
        var zeroState = asm.DefineHere(".zerostate");
        asm.Sta(MemX(zp.Base));
        asm.Dex();
        asm.Bpl(zeroState);

        asm.Move8(Imm(0x0F), Mem(SidFilterModeVolume));
        if (shadows is not null)
            asm.Sta(Mem(shadows.FilterModeVolume));

        // The first voice's list starts at 0, as cleared; the others' are baked in here.
        asm.Move8(Imm(parameters.OrderStart2), Mem(zp.VoiceOrderPos + ZeroPageLayout.VoiceStride));
        asm.Move8(Imm(parameters.OrderStart3), Mem(zp.VoiceOrderPos + 2 * ZeroPageLayout.VoiceStride));

        asm.Ldx(LastVoice(parameters));
        var initChannel = asm.DefineHere(".initchannel");
        asm.Jsr(voiceAdvanceOrder);                 // preserves X
        NextVoice(asm, initChannel);
        asm.Rts();
    }

    private static void EmitMusicPlay(
        Emitter6502 asm, SongTableLabels tables, PlayerParameters parameters, PlayerFeatures features, ZeroPageLayout zp,
        Label entry, Label voiceAutomation, Label voiceTick, Label onBeatPlaySong, PlayerShadows? shadows)
    {
        asm.Define(entry);

        // Wavetables first and on every frame - that is what gives them frame resolution. Counting
        // down saves the cpx; it reverses the write order within a frame, but the writes and the
        // per-frame register state are identical and the voices are independent, so it is inaudible.
        asm.Ldx(LastVoice(parameters));
        var nextVoice = asm.DefineHere(".nextvoice");
        asm.Jsr(voiceAutomation);
        NextVoice(asm, nextVoice);

        EmitFilterPlay(asm, tables, zp, onBeatPlaySong, shadows);

        // --- ONbeat.PlaySong ---
        asm.Define(onBeatPlaySong);
        if (features.Funk)
        {
            // Funk speed: Song_Shuffle starts at 0 and flips with the speeds' difference every
            // tick, so the tick lengths run v_Song_Speed, v_Song_Funk, ... - no init code needed.
            asm.Lda(Mem(zp.SongShuffle));
            asm.Eor(Imm(parameters.SongSpeed ^ parameters.FunkSpeed));
            asm.Sta(Mem(zp.SongShuffle));
            asm.Eor(Imm(parameters.FunkSpeed));
            asm.Sta(Mem(zp.SongSpeed));
        }
        else
        {
            asm.Move8(Imm(parameters.SongSpeed), Mem(zp.SongSpeed));
        }

        // Tick all channels. Filter_Play above ran by fallthrough, not a jsr, so the trailing rts
        // below is this routine's own.
        asm.Ldx(LastVoice(parameters));             // descending, same reasoning as above
        var nextChannel = asm.DefineHere(".nextchannel");
        asm.Jsr(voiceTick);                         // preserves X
        NextVoice(asm, nextChannel);
        asm.Rts();
    }

    /// <summary>Filter, once per frame and before the row fetch, so a program claimed this frame
    /// takes its first step next frame. Byte stream: $01..$FE steps the cutoff, $FF introduces a
    /// four-byte setup (mode+volume, resonance+routing, sweep rate, cutoff), $00 ends. Falls
    /// through from music.play, no jsr.</summary>
    private static void EmitFilterPlay(
        Emitter6502 asm, SongTableLabels tables, ZeroPageLayout zp, Label onBeatPlaySong, PlayerShadows? shadows)
    {
        var filterDone = asm.NewLabel(".filterdone");
        var filterCut = asm.NewLabel(".filtercut");

        asm.Ldx(Mem(zp.FilterPos));
        asm.Beq(filterDone);                        // nothing has claimed the filter yet
        asm.Lda(MemX(tables.FltData));
        asm.Beq(filterDone);                        // program finished - hold the last cutoff
        asm.Cmp(Imm(0xFF));
        asm.Bne(filterCut);
        asm.Inx();
        asm.Lda(MemX(tables.FltData));
        asm.Sta(Mem(SidFilterModeVolume));          // filter mode + master volume
        if (shadows is not null)
            asm.Sta(Mem(shadows.FilterModeVolume));
        asm.Inx();
        asm.Lda(MemX(tables.FltData));
        asm.Sta(Mem(SidFilterResonance));           // resonance + which voices are routed through
        if (shadows is not null)
            asm.Sta(Mem(shadows.FilterResonance));
        asm.Inx();
        asm.Lda(MemX(tables.FltData));
        asm.Sta(Mem(zp.FilterDelta));               // sweep rate, 0 to stop a running sweep
        asm.Inx();
        asm.Lda(MemX(tables.FltData));
        asm.Define(filterCut);
        asm.Sta(Mem(SidFilterCutoffHi));
        if (shadows is not null)
            asm.Sta(Mem(shadows.FilterCutoff));
        asm.Inx();
        asm.Stx(Mem(zp.FilterPos));
        asm.Define(filterDone);

        // Filter sweep, every frame. A free-running triangle: filter_phase advances by a signed
        // rate and wraps, the cutoff folds the top half back down and doubles. That fold is what
        // makes it ping-pong without bounds checks, at the cost of a fixed $00..$FE range.
        asm.Lda(Mem(zp.FilterDelta));
        var sweepDone = asm.NewLabel(".sweepdone");
        asm.Beq(sweepDone);                         // 0 = no sweep running
        asm.Clc();
        asm.Adc(Mem(zp.FilterPhase));
        asm.Sta(Mem(zp.FilterPhase));
        var sweepUp = asm.NewLabel(".sweepup");
        asm.Bpl(sweepUp);
        asm.Eor(Imm(0xFF));                         // past the top: fold back down
        asm.Define(sweepUp);
        asm.Asl();
        asm.Sta(Mem(SidFilterCutoffHi));
        if (shadows is not null)
            asm.Sta(Mem(shadows.FilterCutoff));
        asm.Define(sweepDone);

        asm.Dec(Mem(zp.SongSpeed));
        asm.Bmi(onBeatPlaySong);
        asm.Rts();                                  // between ticks: the filter already stepped
    }

    /// <summary>Per-channel tick. X = channel on entry and preserved on every exit. One subroutine
    /// indexed by X rather than three macro copies, which used to cost 592 of the player's 794
    /// bytes. Rows are variable length: voice_event is a byte offset, .nextcmd consumes commands.</summary>
    private static void EmitVoiceTick(
        Emitter6502 asm, SongTableLabels tables, PlayerFeatures features, ZeroPageLayout zp,
        Label entry, Label voiceAdvanceOrder)
    {
        asm.Define(entry);
        var fetch = asm.NewLabel(".fetch");
        var nextCmd = asm.NewLabel(".nextcmd");
        var playRow = asm.NewLabel(".playrow");
        var setDuration = asm.NewLabel(".setduration");
        var setInstrument = asm.NewLabel(".setinstrument");
        var advance = asm.NewLabel(".advance");
        var loopBack = asm.NewLabel(".loopback");
        var endOfPattern = asm.NewLabel(".endofpattern");
        var slide = asm.NewLabel(".slide");

        // Count down first and test the result rather than testing for zero then decrementing: a
        // row with N ticks left spends exactly N of them here. The $ff is never seen - .playrow
        // reloads voice_duration from voice_rowdur first.
        asm.Dec(MemX(zp.VoiceDuration));
        asm.Bmi(fetch);
        asm.Rts();

        asm.Define(fetch);
        asm.Ldy(MemX(zp.VoicePatternIdx));
        asm.Lda(MemY(tables.TblRowsLo));
        asm.Sta(Mem(zp.ZpLb));
        asm.Lda(MemY(tables.TblRowsHi));
        asm.Sta(Mem(zp.ZpHb));

        // .fetchrow in the assembly - a label nothing jumps to, so there is nothing to define.
        asm.Ldy(MemX(zp.VoiceEvent));

        asm.Define(nextCmd);
        asm.Lda(IndY(zp.ZpLb));
        asm.Beq(endOfPattern);
        asm.Bpl(playRow);                           // $01..$7F is a note id - store it as it stands
        asm.Cmp(Imm(CmdInst));
        asm.Bcc(setDuration);                       // $80..$BF
        asm.Cmp(Imm(CmdOff));
        asm.Bcc(setInstrument);                     // $C0..$CF
        asm.Cmp(Imm(LoopEnd));
        // $D0/$D1 - note off and wait are stored as the command byte itself, which is what
        // NOTE_OFF/ROW_WAIT are defined to be. Falls through on $D2 into .loopend, which is why
        // that block has to stay right here.
        if (features.Slide)
        {
            asm.Bcc(playRow);
            asm.Bne(slide);                         // $D3, only in a song with slide rows
        }
        else
        {
            asm.Bne(playRow);
        }

        // --- .loopend ---
        // $D2, then the repeat count (0 = bare LE, loops forever), then the byte offset to jump
        // back to (the most recent LS, or pattern start if none).
        asm.Iny();
        asm.Lda(IndY(zp.ZpLb));
        asm.Beq(loopBack);
        // ISC (illegal $F7 zp,x) is INC then SBC, so this one instruction is the inc + cmp pair it
        // replaces: with carry set, SBC gives A - counter, zero exactly when equal. CARRY MUST BE
        // SET - it is, only because the sole way in is the falling-through cmp #LOOP_END above.
        asm.Isc(MemX(zp.VoiceLoopCounter));
        asm.Bne(loopBack);
        // A is already 0 - the difference the isc just computed, and this path only runs when that
        // difference was zero.
        asm.Sta(MemX(zp.VoiceLoopCounter));
        asm.Iny();                                  // step past the target byte to the next row
        asm.Iny();
        // Y is already the new offset, so there is no need to store it and have it loaded straight
        // back - nothing between here and .playrow reads voice_event, and both write it themselves.
        asm.Jmp(nextCmd);

        asm.Define(loopBack);
        asm.Iny();
        asm.Lda(IndY(zp.ZpLb));
        asm.Tay();                                  // same reasoning - jump straight in with Y set
        asm.Jmp(nextCmd);

        asm.Define(endOfPattern);
        asm.Jsr(voiceAdvanceOrder);                 // reuses ZP_lb/hb, so re-derive the row pointer
        asm.Jmp(fetch);

        // Masking rather than subtracting: the dispatch above already proved which range the byte
        // is in, so the low bits are the value and a single AND isolates them - one byte less than
        // sec/sbc, and it leaves no carry to think about.
        asm.Define(setDuration);
        asm.And(Imm(0x3F));                         // $80..$BF -> 0..63 further ticks, so a row
        asm.Sta(MemX(zp.VoiceRowDur));              // lasts 1..64
        // Always taken - "and #$3f" cannot set N, and the sta between does not touch flags.
        asm.Bpl(advance);

        asm.Define(setInstrument);
        asm.And(Imm(0x0F));                         // $C0..$CF -> 0..15
        asm.Sta(MemX(zp.VoiceInst));
        asm.Define(advance);
        asm.Iny();
        asm.Bne(nextCmd);                           // always - a pattern never reaches offset 0 here

        if (features.Slide)
        {
            // $D3, then the signed rate: a wait row that also sets the voice's slide, so it leaves
            // as ROW_WAIT with Y on the rate byte - .playrow steps past it.
            asm.Define(slide);
            asm.Iny();
            asm.Lda(IndY(zp.ZpLb));
            asm.Sta(MemX(zp.VoiceSlide));
            asm.Lda(Imm(RowWait));
        }

        // All in Y: A keeps the note for Voice_PlayCurrent, which this falls into.
        asm.Define(playRow);
        asm.Iny();
        asm.Sty(MemX(zp.VoiceEvent));
        asm.Ldy(MemX(zp.VoiceRowDur));              // the row takes however long is current
        asm.Sty(MemX(zp.VoiceDuration));
    }
}
