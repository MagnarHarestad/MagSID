using MagSid.Core.Model;

namespace MagSid.Assembler;

using static Operand;

/// <summary>A song with the Musicshower in front. It loads at <see cref="ShowerProgram.LoadAddress"/>
/// and RUN starts it.</summary>
/// <param name="PlayerAddress">music.init, directly after the shower; music.play is three bytes on.</param>
public sealed record ShowerImage(byte[] Image, int PlayerAddress)
{
    /// <summary>The first address past the song.</summary>
    public int End => ShowerProgram.LoadAddress + Image.Length;

    /// <summary>The image with its load address in front, as a C64 file has it.</summary>
    public byte[] ToPrg()
    {
        var prg = new byte[Image.Length + 2];
        prg[0] = ShowerProgram.LoadAddress & 0xFF;
        prg[1] = ShowerProgram.LoadAddress >> 8;
        Image.CopyTo(prg, 2);
        return prg;
    }
}

/// <summary>
/// The Musicshower: a C64 program that plays the song and prints the player's own state each frame,
/// after Code\musicshower.s by Magnar Harestad.
///
/// One contiguous block, so nothing can overlap and the song always fits: the BASIC line at $0801,
/// the shower, then the player and song. The shower's size varies with the song's name and notes,
/// so the player's address does too. The KERNAL and BASIC are switched out for good, so the player's
/// zero page is safe, and the raster IRQ comes in through $FFFE.
///
/// Almost everything on screen is read straight from the player's zero page. What it keeps nowhere
/// goes through <see cref="PlayerShadows"/>, which only this build of the player has: the row's note
/// or command, and the filter registers, which are write-only.
/// </summary>
public static class ShowerProgram
{
    public const int LoadAddress = 0x0801;

    private const int Screen = 0x0400;
    private const int ColourRam = 0xD800;
    private const int ScreenWidth = 40;
    private const int ScreenRows = 25;

    private const int CpuPort = 0x01;
    private const int RasterLine = 0xD012;
    private const int IrqAck = 0xD019;
    private const int Border = 0xD020;
    private const int IrqVector = 0xFFFE;
    private const int NmiVector = 0xFFFA;

    private const int PlayRasterLine = 0x10;
    private const int PlayBorderColour = 0x0B;
    private const int TextColour = 0x03;

    // Everything of the shower's in zero page sits under the player's, which starts at $20.
    private const int ScreenPointer = 0x02;     // and $03
    private const int TextPointer = 0x04;       // and $05
    private const int RasterFirst = 0x06;
    private const int RasterNow = 0x07;
    private const int RasterMax = 0x08;

    // Seven apart, so the player's voice index reaches them too.
    private const int ShownNote = 0x09;
    private const int ShownInstrument = 0x0A;
    private const int ShownAttackDecay = 0x0B;
    private const int ShownSustainRelease = 0x0C;

    private const int ShownCutoff = 0x1B;
    private const int ShownResonance = 0x1C;
    private const int ShownModeVolume = 0x1D;

    private const int FirstZeroPage = ScreenPointer;
    private const int LastZeroPage = ShownModeVolume;

    public static readonly PlayerShadows Shadows = new(ShownNote, ShownCutoff, ShownResonance, ShownModeVolume);

    // Screen rows. Each voice has a three-wide column, values right-aligned in it.
    private const int TitleRow = 0;
    private const int AuthorRow = 1;
    private const int RasterRow = 2;
    private const int VoiceRow = 4;
    private const int SongPosRow = 5;
    private const int PatternRow = 6;
    private const int PatternPosRow = 7;
    private const int DurationRow = 8;
    private const int RowDurationRow = 9;
    private const int NoteRow = 11;
    private const int InstrumentRow = 13;
    private const int AttackDecayRow = 14;
    private const int SustainReleaseRow = 15;
    private const int WaveformRow = 16;
    private const int PulseRow = 17;
    private const int HardRestartRow = 18;
    private const int ModeVolumeRow = 20;
    private const int ResonanceRow = 21;
    private const int CutoffRow = 22;
    private const int FilterAddRow = 23;
    private const int FooterRow = 24;

    private const int RasterMaxColumn = 17;
    private const int RasterNowColumn = 27;

    private const int NoteOffCommand = 0xD0;

    private static int Column(int voice) => 16 + voice * 4;

    private static int ScreenAt(int row, int column) => Screen + row * ScreenWidth + column;

    private static int Voice(int array, int voice) => array + voice * ZeroPageLayout.VoiceStride;

    /// <summary>A byte the screen shows as two hex digits.</summary>
    private readonly record struct HexCell(int Source, int Row, int Column);

    /// <summary>The first and last zero-page bytes the program uses, the player's included.</summary>
    public static (int First, int Last) ZeroPageRange(Song song) =>
        (FirstZeroPage, new ZeroPageLayout(SongImageBuilder.FeaturesOf(song)).End - 1);

    /// <exception cref="SongTooLargeException">The song is outside the player's limits.</exception>
    public static ShowerImage Build(Song song, string title, string author)
    {
        var asm = new Emitter6502(LoadAddress);
        var player = asm.NewLabel("player");
        var play = asm.NewLabel("player.play");

        SongImageBuilder.Emit(asm, song, Shadows, tables =>
        {
            EmitBasicLine(asm);
            EmitShower(asm, song, title, author, tables, player, play);

            asm.Define(player);
            asm.DefineAt(play, asm.Pc + 3);
        });

        return new ShowerImage(asm.Assemble(), player.Address!.Value);
    }

    /// <summary>10 SYS2061, which is the address right after this line.</summary>
    private static void EmitBasicLine(Emitter6502 asm)
    {
        const int nextLine = LoadAddress + 10;
        asm.Word(nextLine);
        asm.Word(10);
        asm.Byte(0x9E);                             // the token for SYS
        asm.Bytes(System.Text.Encoding.ASCII.GetBytes((nextLine + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        asm.Byte(0);                                // end of the line
        asm.Word(0);                                // end of the program
    }

    private static void EmitShower(
        Emitter6502 asm, Song song, string title, string author, SongTableLabels tables, Label player, Label play)
    {
        var zp = new ZeroPageLayout(SongImageBuilder.FeaturesOf(song));
        var notes = NoteDictionary.Build(song);

        var text = asm.NewLabel("text");
        var irq = asm.NewLabel("irq");
        var nmi = asm.NewLabel("nmi");
        var digits = asm.NewLabel("digits");
        var instrumentNumbers = asm.NewLabel("instrument_numbers");
        var shownText = new[] { asm.NewLabel("shown_text1"), asm.NewLabel("shown_text2"), asm.NewLabel("shown_text3") };
        var cellSource = asm.NewLabel("cell_source");
        var cellLow = asm.NewLabel("cell_lo");
        var cellHigh = asm.NewLabel("cell_hi");
        var noteIndex = asm.NewLabel("note_index");
        var restartIndex = asm.NewLabel("restart_index");
        var decimalDigits = asm.NewLabel("decimal_digits");

        var cells = HexCells(zp);

        EmitSetup(asm, text, irq, nmi, player);

        asm.Define(nmi);
        asm.Rti();

        EmitIrq(asm, zp, tables, play, irq, digits, instrumentNumbers, shownText, cells.Count, cellSource, cellLow, cellHigh, noteIndex, restartIndex, decimalDigits);
        EmitNoteIndex(asm, notes, noteIndex);
        EmitRestartIndex(asm, notes, restartIndex);
        EmitDecimalDigits(asm, decimalDigits);

        // --- data ---

        asm.Define(digits);
        asm.Bytes("0123456789ABCDEF".Select(ScreenCode));

        // voice_inst counts in the player's order, and the screen speaks the song's. Packed so the
        // two hex digits print the number the way the editor writes it: instrument 16 is $16.
        asm.Define(instrumentNumbers);
        var order = InstrumentTables.PlayerOrder(song);
        asm.Bytes(order.OrderBy(entry => entry.Value).Select(entry => (byte)(entry.Key / 10 * 16 + entry.Key % 10)));

        var shown = ShownTexts(notes);
        for (int i = 0; i < 3; i++)
        {
            asm.Define(shownText[i]);
            asm.Bytes(shown.Select(entry => ScreenCode(entry[i])));
        }

        asm.Define(cellSource);
        asm.Bytes(cells.Select(c => (byte)c.Source));
        asm.Define(cellLow);
        asm.Bytes(cells.Select(c => (byte)(ScreenAt(c.Row, c.Column) & 0xFF)));
        asm.Define(cellHigh);
        asm.Bytes(cells.Select(c => (byte)(ScreenAt(c.Row, c.Column) >> 8)));

        asm.Define(text);
        asm.Bytes(ScreenText(title, author));
    }

    private static List<HexCell> HexCells(ZeroPageLayout zp)
    {
        var cells = new List<HexCell>
        {
            new(RasterMax, RasterRow, RasterMaxColumn),
            new(RasterNow, RasterRow, RasterNowColumn),
            new(ShownModeVolume, ModeVolumeRow, Column(0) + 1),
            new(ShownResonance, ResonanceRow, Column(0) + 1),
            new(ShownCutoff, CutoffRow, Column(0) + 1),
            new(zp.FilterDelta, FilterAddRow, Column(0) + 1),
        };

        for (int voice = 0; voice < Song.ChannelCount; voice++)
        {
            int right = Column(voice) + 1;
            cells.Add(new(Voice(zp.VoiceOrderPos, voice), SongPosRow, right));
            cells.Add(new(Voice(zp.VoiceEvent, voice), PatternPosRow, right));
            cells.Add(new(Voice(zp.VoiceDuration, voice), DurationRow, right));
            cells.Add(new(Voice(zp.VoiceRowDur, voice), RowDurationRow, right));
            cells.Add(new(Voice(ShownInstrument, voice), InstrumentRow, right));
            cells.Add(new(Voice(ShownAttackDecay, voice), AttackDecayRow, right));
            cells.Add(new(Voice(ShownSustainRelease, voice), SustainReleaseRow, right));
            cells.Add(new(Voice(zp.VoiceLastWaveform, voice), WaveformRow, right));
            cells.Add(new(Voice(zp.VoicePwLo, voice), PulseRow, right));
        }

        return cells;
    }

    /// <summary>Three-letter entries: index 0 is a wait row or nothing played yet, a note's dictionary
    /// id is its own index, and the rest follow.</summary>
    private static List<string> ShownTexts(NoteDictionary notes)
    {
        var texts = new List<string> { "..." };
        texts.AddRange(notes.Notes.Select(note => note.ToString()));
        texts.AddRange(["---", "ON ", "SFT", "OFF"]);
        return texts;
    }

    private static int NoteOffIndex(NoteDictionary notes) => notes.Count + 1;
    private static int RestartOnIndex(NoteDictionary notes) => notes.Count + 2;
    private static int RestartSoftIndex(NoteDictionary notes) => notes.Count + 3;
    private static int RestartOffIndex(NoteDictionary notes) => notes.Count + 4;

    private static void EmitSetup(Emitter6502 asm, Label text, Label irq, Label nmi, Label player)
    {
        asm.Sei();
        asm.Move8(Imm(0x35), Mem(CpuPort));         // I/O stays; BASIC and the KERNAL go

        // No timer interrupts from either CIA, and whatever they had pending goes.
        asm.Lda(Imm(0x7F));
        asm.Sta(Mem(0xDC0D));
        asm.Sta(Mem(0xDD0D));
        asm.Lda(Mem(0xDC0D));
        asm.Lda(Mem(0xDD0D));

        // Text mode in the first VIC bank, with the ROM character set.
        asm.Move8(Imm(0x3F), Mem(0xDD02));
        asm.Move8(Imm(0x03), Mem(0xDD00));
        asm.Move8(Imm(0x1B), Mem(0xD011));
        asm.Move8(Imm(0xC8), Mem(0xD016));
        asm.Move8(Imm(0x15), Mem(0xD018));

        asm.Lda(Imm(0));
        asm.Sta(Mem(0xD015));                       // no sprites
        asm.Sta(Mem(Border));
        asm.Sta(Mem(0xD021));

        // The SID, as a program before this may have left it.
        asm.Ldx(Imm(0x18));
        var clearSid = asm.DefineHere(".clearsid");
        asm.Sta(MemX(0xD400));
        asm.Dex();
        asm.Bpl(clearSid);

        asm.Ldx(Imm(LastZeroPage - FirstZeroPage));
        var clearZeroPage = asm.DefineHere(".clearzp");
        asm.Sta(MemX(FirstZeroPage));
        asm.Dex();
        asm.Bpl(clearZeroPage);

        // Whole pages, which also clears the sprite pointers past the screen's end. X is 0 here.
        asm.Inx();
        var clearScreen = asm.DefineHere(".clearscreen");
        asm.Lda(Imm(0x20));
        for (int page = 0; page < 4; page++)
            asm.Sta(MemX(Screen + page * 0x100));
        asm.Lda(Imm(TextColour));
        for (int page = 0; page < 4; page++)
            asm.Sta(MemX(ColourRam + page * 0x100));
        asm.Inx();
        asm.Bne(clearScreen);

        EmitPrintText(asm, text);

        asm.Jsr(player);                            // music.init

        asm.Move8(ImmLow(irq), Mem(IrqVector));
        asm.Move8(ImmHigh(irq), Mem(IrqVector + 1));
        asm.Move8(ImmLow(nmi), Mem(NmiVector));     // the RESTORE key does nothing
        asm.Move8(ImmHigh(nmi), Mem(NmiVector + 1));

        asm.Move8(Imm(PlayRasterLine), Mem(RasterLine));
        asm.Lda(Imm(1));
        asm.Sta(Mem(0xD01A));
        asm.Sta(Mem(IrqAck));
        asm.Cli();

        var idle = asm.DefineHere(".idle");
        asm.Jmp(idle);
    }

    /// <summary>The fixed text: screen codes row by row from the top, $FE ending a row and $FF the
    /// lot.</summary>
    private static void EmitPrintText(Emitter6502 asm, Label text)
    {
        asm.Move8(ImmLow(text), Mem(TextPointer));
        asm.Move8(ImmHigh(text), Mem(TextPointer + 1));
        asm.Move8(Imm(Screen & 0xFF), Mem(ScreenPointer));
        asm.Move8(Imm(Screen >> 8), Mem(ScreenPointer + 1));

        var row = asm.DefineHere(".row");
        asm.Ldy(Imm(0));
        var character = asm.DefineHere(".character");
        asm.Lda(IndY(TextPointer));
        asm.Cmp(Imm(0xFE));
        var endOfRow = asm.NewLabel(".endofrow");
        asm.Bcs(endOfRow);
        asm.Sta(IndY(ScreenPointer));
        asm.Iny();
        asm.Bne(character);                         // always: a row is at most 40 long

        asm.Define(endOfRow);
        asm.Tax();                                  // C is set, so this adds Y + 1: past the marker
        asm.Tya();
        asm.Adc(Mem(TextPointer));
        asm.Sta(Mem(TextPointer));
        var sameTextPage = asm.NewLabel(".sametextpage");
        asm.Bcc(sameTextPage);
        asm.Inc(Mem(TextPointer + 1));
        asm.Define(sameTextPage);

        asm.Lda(Mem(ScreenPointer));
        asm.Clc();
        asm.Adc(Imm(ScreenWidth));
        asm.Sta(Mem(ScreenPointer));
        var sameScreenPage = asm.NewLabel(".samescreenpage");
        asm.Bcc(sameScreenPage);
        asm.Inc(Mem(ScreenPointer + 1));
        asm.Define(sameScreenPage);

        asm.Inx();                                  // only the $FF reaches 0
        asm.Bne(row);
    }

    private static void EmitIrq(
        Emitter6502 asm, ZeroPageLayout zp, SongTableLabels tables, Label play, Label irq, Label digits,
        Label instrumentNumbers, Label[] shownText, int cellCount, Label cellSource, Label cellLow, Label cellHigh,
        Label noteIndex, Label restartIndex, Label decimalDigits)
    {
        asm.Define(irq);
        asm.Pha();
        asm.Txa();
        asm.Pha();
        asm.Tya();
        asm.Pha();

        // music.play alone, in raster lines, with the border showing the same.
        asm.Move8(Imm(PlayBorderColour), Mem(Border));
        asm.Move8(Mem(RasterLine), Mem(RasterFirst));
        asm.Jsr(play);
        asm.Lda(Mem(RasterLine));
        asm.Sec();
        asm.Sbc(Mem(RasterFirst));
        asm.Sta(Mem(RasterNow));
        asm.Move8(Imm(0), Mem(Border));

        // The peak stays.
        asm.Lda(Mem(RasterNow));
        asm.Cmp(Mem(RasterMax));
        var notNewPeak = asm.NewLabel(".notnewpeak");
        asm.Bcc(notNewPeak);
        asm.Sta(Mem(RasterMax));
        asm.Define(notNewPeak);

        // What the current instrument is, for the cells below.
        asm.Ldx(Imm((Song.ChannelCount - 1) * ZeroPageLayout.VoiceStride));
        var nextInstrument = asm.DefineHere(".nextinstrument");
        asm.Ldy(MemX(zp.VoiceInst));
        asm.Lda(MemY(instrumentNumbers));
        asm.Sta(MemX(ShownInstrument));
        asm.Lda(MemY(tables.AdTable));
        asm.Sta(MemX(ShownAttackDecay));
        asm.Lda(MemY(tables.SrTable));
        asm.Sta(MemX(ShownSustainRelease));
        asm.Txa();
        asm.Axs(Imm(ZeroPageLayout.VoiceStride));
        asm.Bpl(nextInstrument);

        EmitHexCells(asm, digits, cellCount, cellSource, cellLow, cellHigh);

        for (int voice = 0; voice < Song.ChannelCount; voice++)
        {
            int column = Column(voice);

            asm.Lda(Mem(Voice(ShownNote, voice)));
            asm.Jsr(noteIndex);
            EmitShownText(asm, shownText, NoteRow, column);

            asm.Lda(Mem(Voice(zp.VoicePrevPw, voice)));
            asm.Jsr(restartIndex);
            EmitShownText(asm, shownText, HardRestartRow, column);

            asm.Lda(Mem(Voice(zp.VoicePatternIdx, voice)));
            asm.Jsr(decimalDigits);
            asm.Sty(Mem(ScreenAt(PatternRow, column)));
            asm.Stx(Mem(ScreenAt(PatternRow, column + 1)));
            asm.Sta(Mem(ScreenAt(PatternRow, column + 2)));

            // The width's high nibble. Right after a trigger this byte still has the flags on top.
            asm.Lda(Mem(Voice(zp.VoicePwHi, voice)));
            asm.And(Imm(0x0F));
            asm.Tay();
            asm.Lda(MemY(digits));
            asm.Sta(Mem(ScreenAt(PulseRow, column)));
        }

        asm.Move8(Imm(1), Mem(IrqAck));
        asm.Pla();
        asm.Tay();
        asm.Pla();
        asm.Tax();
        asm.Pla();
        asm.Rti();
    }

    /// <summary>Every byte shown as two hex digits, from a table, so it costs three bytes a value
    /// and not twenty.</summary>
    private static void EmitHexCells(
        Emitter6502 asm, Label digits, int cellCount, Label cellSource, Label cellLow, Label cellHigh)
    {
        asm.Ldx(Imm(cellCount - 1));
        var nextCell = asm.DefineHere(".nextcell");
        asm.Lda(MemX(cellLow));
        asm.Sta(Mem(ScreenPointer));
        asm.Lda(MemX(cellHigh));
        asm.Sta(Mem(ScreenPointer + 1));
        asm.Ldy(MemX(cellSource));
        asm.Lda(MemY(0));
        asm.Pha();
        asm.Lsr();
        asm.Lsr();
        asm.Lsr();
        asm.Lsr();
        asm.Tay();
        asm.Lda(MemY(digits));
        asm.Ldy(Imm(0));
        asm.Sta(IndY(ScreenPointer));
        asm.Pla();
        asm.And(Imm(0x0F));
        asm.Tay();
        asm.Lda(MemY(digits));
        asm.Ldy(Imm(1));
        asm.Sta(IndY(ScreenPointer));
        asm.Dex();
        asm.Bpl(nextCell);
    }

    private static void EmitShownText(Emitter6502 asm, Label[] shownText, int row, int column)
    {
        for (int i = 0; i < 3; i++)
        {
            asm.Lda(MemY(shownText[i]));
            asm.Sta(Mem(ScreenAt(row, column + i)));
        }
    }

    /// <summary>A = the row's note or command, Y = out its index in the shown texts. A note's id is
    /// its index, and so is 0 for nothing played yet; anything else above that is a wait.</summary>
    private static void EmitNoteIndex(Emitter6502 asm, NoteDictionary notes, Label entry)
    {
        asm.Define(entry);
        var done = asm.NewLabel(".done");
        asm.Tay();
        asm.Bpl(done);
        asm.Ldy(Imm(NoteOffIndex(notes)));
        asm.Cmp(Imm(NoteOffCommand));
        asm.Beq(done);
        asm.Ldy(Imm(0));
        asm.Define(done);
        asm.Rts();
    }

    /// <summary>A = voice_prev_pw, Y = out its index in the shown texts: what the next note on this
    /// voice does. The same two bits Voice_PlayCurrent tests, NOHR first.</summary>
    private static void EmitRestartIndex(Emitter6502 asm, NoteDictionary notes, Label entry)
    {
        asm.Define(entry);
        var done = asm.NewLabel(".done");
        asm.Tax();
        asm.Ldy(Imm(RestartOffIndex(notes)));
        asm.And(Imm(0x20));
        asm.Bne(done);
        asm.Ldy(Imm(RestartOnIndex(notes)));
        asm.Txa();
        asm.Bpl(done);
        asm.Ldy(Imm(RestartSoftIndex(notes)));
        asm.Define(done);
        asm.Rts();
    }

    /// <summary>A = a pattern number, out as screen codes for the way the editor writes it: Y
    /// hundreds (a space when none), X tens, A ones.</summary>
    private static void EmitDecimalDigits(Emitter6502 asm, Label entry)
    {
        const int beforeZero = 0x2F;

        asm.Define(entry);

        // Each loop goes one past and the adc takes that back: the carry is clear on the way out.
        asm.Ldy(Imm(beforeZero));
        asm.Sec();
        var hundreds = asm.DefineHere(".hundreds");
        asm.Iny();
        asm.Sbc(Imm(100));
        asm.Bcs(hundreds);
        asm.Adc(Imm(100));

        asm.Cpy(Imm(beforeZero + 1));
        var hasHundreds = asm.NewLabel(".hashundreds");
        asm.Bne(hasHundreds);
        asm.Ldy(Imm(0x20));
        asm.Define(hasHundreds);

        asm.Ldx(Imm(beforeZero));
        asm.Sec();
        var tens = asm.DefineHere(".tens");
        asm.Inx();
        asm.Sbc(Imm(10));
        asm.Bcs(tens);
        asm.Adc(Imm(10 + beforeZero + 1));
        asm.Rts();
    }

    private static byte[] ScreenText(string title, string author)
    {
        var rows = new Dictionary<int, string>
        {
            [TitleRow] = Centred(title),
            [AuthorRow] = Centred(author),
            [RasterRow] = "RASTERTIME MAX: $00. NOW: $00",
            [VoiceRow] = "VOICE........:   1   2   3",
            [SongPosRow] = "SONGPOS......:",
            [PatternRow] = "PATTERN NR...:",
            [PatternPosRow] = "PATTERN POS..:",
            [DurationRow] = "NOTE DURATION:",
            [RowDurationRow] = "ROW DURATION.:",
            [NoteRow] = "NOTE.........:",
            [InstrumentRow] = "INSTRUMENT...:",
            [AttackDecayRow] = "ATTACK/DECAY.:",
            [SustainReleaseRow] = "SUSTAIN/REL..:",
            [WaveformRow] = "WAVEFORM/GATE:",
            [PulseRow] = "PULSE........:",
            [HardRestartRow] = "HARD RESTART.:",
            [ModeVolumeRow] = "FILTER/VOLUME:",
            [ResonanceRow] = "FILTER RES+V.:",
            [CutoffRow] = "FILTER CUTOFF:",
            [FilterAddRow] = "FILTER ADD...:",
            [FooterRow] = Centred("MAGSID MUSICSHOWER"),
        };

        var bytes = new List<byte>();
        for (int row = 0; row < ScreenRows; row++)
        {
            if (rows.TryGetValue(row, out string? text))
                bytes.AddRange(text.Select(ScreenCode));
            bytes.Add(row == ScreenRows - 1 ? (byte)0xFF : (byte)0xFE);
        }

        return bytes.ToArray();
    }

    private static string Centred(string text)
    {
        string fitted = text.Trim();
        if (fitted.Length > ScreenWidth)
            fitted = fitted[..ScreenWidth];
        return new string(' ', (ScreenWidth - fitted.Length) / 2) + fitted;
    }

    /// <summary>In the ROM's first character set: capitals, and what it has of punctuation. A
    /// character it does not have becomes a space.</summary>
    private static byte ScreenCode(char c)
    {
        char upper = char.ToUpperInvariant(c);
        return upper switch
        {
            >= 'A' and <= 'Z' => (byte)(upper - 'A' + 1),
            >= ' ' and <= '?' => (byte)upper,
            '@' => 0,
            '[' => 0x1B,
            ']' => 0x1D,
            _ => 0x20,
        };
    }
}
