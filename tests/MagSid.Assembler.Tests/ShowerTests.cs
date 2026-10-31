using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Editor.App.Export;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The Musicshower run as a C64 runs it: from the BASIC line, to the idle loop, then one raster
/// interrupt a frame. Each frame the song must sound exactly as the plain player does, and every
/// value on screen must be what the player really holds.
/// </summary>
public class ShowerTests
{
    private const int Frames = 3000;
    private const int FirstSidRegister = 0xD400;
    private const int LastSidRegister = 0xD41C;

    private const string AllFeatures = """
        var v_Song_Speed = 1

        INSTRUMENTS:
        01, $41, $09, $a0, WAVE $41 $41, ARP "C-4" "E-4" "G-4", VIB $00 $04
        02, $21, $09, $a0, VIB $32 $10
        03, $11, $09, $a0
        END:

        PAT01:
        "C-4", 01
        "103", 00
        "...", 00
        "---", 00
        "E-4", 02
        "220", 00
        "G-3", 03
        "17F", 00
        "...", 00
        END:

        PAT02:
        "C-3", 03
        "280", 00
        "...", 00
        END:

        ORDER_CH1:
        1
        END:

        ORDER_CH2:
        2
        END:

        ORDER_CH3:
        1, 2
        END:
        """;

    private static string SongPath(string file) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData", file));

    private static Song Load(string file) => PatternsFileReader.Read(SongPath(file)).WithUnusedPatternsRemoved();

    private static Song WithEveryFeature()
    {
        var song = PatternsFileReader.Parse(AllFeatures);
        song.FunkSpeed = 3;
        song.Instruments[0].SoftHardRestart = true;
        song.Instruments[2].NoHardRestart = true;
        return song;
    }

    public static TheoryData<string> Songs => new()
    {
        "Airstrike.s", "BellaCiao.s", "Breakdance.s", "CityBomber4k(Megastyle).s", "Lightforce.s", "Magnificent.s", "SOS.s",
    };

    /// <summary>A C64 as far as the shower can tell: RAM, the raster register, and an interrupt each
    /// frame. The raster line creeps as instructions run, so a reading of it means something.</summary>
    private sealed class Machine
    {
        public readonly Mos6502 Cpu;
        public readonly List<(int Register, int Value)> SidWrites = [];
        public readonly int[] LastSid = new int[0x1D];
        public int IdleAddress { get; }

        public Machine(byte[] prg)
        {
            var memory = new byte[0x10000];
            int load = prg[0] | (prg[1] << 8);
            Array.Copy(prg, 2, memory, load, prg.Length - 2);

            Cpu = new Mos6502(memory, (address, value) =>
            {
                if (address is >= FirstSidRegister and <= LastSidRegister)
                {
                    SidWrites.Add((address - FirstSidRegister, value));
                    LastSid[address - FirstSidRegister] = value;
                }
            })
            {
                PC = SysAddress(memory),
            };

            // Until the program sits still, where a jump goes to itself.
            for (int i = 0; ; i++)
            {
                Assert.True(i < 1_000_000, "the program never reached its idle loop");
                int before = Cpu.PC;
                Cpu.Step();
                if (Cpu.PC == before)
                    break;
            }

            IdleAddress = Cpu.PC;
            Assert.False(Cpu.InterruptDisable, "the idle loop runs with interrupts off, so nothing would ever play");
        }

        /// <summary>The number on the line BASIC has at $0801: a SYS token, then digits.</summary>
        private static int SysAddress(byte[] memory)
        {
            Assert.Equal(0x9E, memory[0x0805]);
            int address = 0;
            for (int i = 0x0806; memory[i] != 0; i++)
                address = address * 10 + (memory[i] - '0');
            return address;
        }

        public void Interrupt()
        {
            SidWrites.Clear();

            int stack = Cpu.SP;
            Cpu.Write(0x100 + Cpu.SP, Cpu.PC >> 8);
            Cpu.SP = (Cpu.SP - 1) & 0xFF;
            Cpu.Write(0x100 + Cpu.SP, Cpu.PC & 0xFF);
            Cpu.SP = (Cpu.SP - 1) & 0xFF;
            Cpu.Write(0x100 + Cpu.SP, 0x20);
            Cpu.SP = (Cpu.SP - 1) & 0xFF;
            Cpu.PC = Cpu.Read(0xFFFE) | (Cpu.Read(0xFFFF) << 8);

            for (int i = 0; Cpu.PC != IdleAddress || Cpu.SP != stack; i++)
            {
                Assert.True(i < 19_656 / 2, "an interrupt ran more instructions than a frame has room for");
                if (i % 8 == 0)
                    Cpu.Memory[0xD012]++;
                Cpu.Step();
            }

            Cpu.Memory[0xD012] = 0x10;
        }

        private int Screen(int row, int column) => Cpu.Read(0x0400 + row * 40 + column);

        public string Text(int row, int column, int length)
        {
            var text = new char[length];
            for (int i = 0; i < length; i++)
            {
                int code = Screen(row, column + i);
                text[i] = code is >= 1 and <= 26 ? (char)('A' + code - 1) : (char)code;
            }

            return new string(text);
        }

        public int Hex(int row, int column) => Convert.ToInt32(Text(row, column, 2), 16);
    }

    private static Machine Boot(Song song, string title = "T", string author = "A") =>
        new(ShowerProgram.Build(song, title, author).ToPrg());

    [Theory]
    [MemberData(nameof(Songs))]
    public void It_sounds_exactly_like_the_plain_player(string file)
    {
        var song = Load(file);
        var machine = Boot(song);
        var shower = ShowerProgram.Build(song, "T", "A");

        var plainWrites = new List<(int, int)>();
        var plain = new byte[0x10000];
        byte[] image = SongImageBuilder.Build(song, shower.PlayerAddress);
        image.CopyTo(plain, shower.PlayerAddress);
        var cpu = new Mos6502(plain, (address, value) =>
        {
            if (address is >= FirstSidRegister and <= LastSidRegister)
                plainWrites.Add((address - FirstSidRegister, value));
        });
        cpu.Call(shower.PlayerAddress);

        for (int frame = 0; frame < Frames; frame++)
        {
            plainWrites.Clear();
            cpu.Call(shower.PlayerAddress + 3);
            machine.Interrupt();
            Assert.Equal(plainWrites, machine.SidWrites);
        }
    }

    [Theory]
    [MemberData(nameof(Songs))]
    public void The_screen_shows_what_the_player_holds(string file) => CheckScreen(Load(file));

    [Fact]
    public void The_screen_is_right_with_every_optional_part_built_in()
    {
        var song = WithEveryFeature();
        Assert.True(SongImageBuilder.FeaturesOf(song) is { Vibrato: true, Slide: true, SoftHardRestart: true, Funk: true });
        CheckScreen(song);
    }

    private static void CheckScreen(Song song)
    {
        var machine = Boot(song);
        var cpu = machine.Cpu;
        var zp = new ZeroPageLayout(SongImageBuilder.FeaturesOf(song));
        var notes = NoteDictionary.Build(song);
        var order = InstrumentTables.PlayerOrder(song);
        var instruments = order.ToDictionary(entry => entry.Value, entry => entry.Key);
        var rowBytes = song.Patterns.ToDictionary(p => p.Number, p => RowsOf(PatternEncoder.Encode(p, notes, order)));

        int peak = 0;
        var seen = new HashSet<string>();
        for (int frame = 0; frame < Frames; frame++)
        {
            machine.Interrupt();

            int now = machine.Hex(2, 27);
            peak = Math.Max(peak, now);
            Assert.Equal(peak, machine.Hex(2, 17));
            Assert.True(now > 0, "a reading before and after play found no time passed");

            for (int voice = 0; voice < Song.ChannelCount; voice++)
            {
                int Held(int array) => cpu.Read(array + voice * ZeroPageLayout.VoiceStride);
                int column = 16 + voice * 4;

                Assert.Equal(Held(zp.VoiceOrderPos), machine.Hex(5, column + 1));
                int pattern = Held(zp.VoicePatternIdx);
                Assert.Equal(pattern.ToString("00").PadLeft(3), machine.Text(6, column, 3));
                Assert.Equal(Held(zp.VoiceEvent), machine.Hex(7, column + 1));
                Assert.Equal(Held(zp.VoiceDuration), machine.Hex(8, column + 1));
                Assert.Equal(Held(zp.VoiceRowDur), machine.Hex(9, column + 1));

                string note = NoteText(rowBytes[pattern], Held(zp.VoiceEvent), notes);
                Assert.Equal(note, machine.Text(11, column, 3));
                seen.Add(note);

                // By the song's own number, which is not the player's once vibrato reorders them.
                var instrument = song.Instruments.Single(i => i.Number == instruments[Held(zp.VoiceInst)]);
                Assert.Equal(instrument.Number.ToString("00"), machine.Text(13, column + 1, 2));
                Assert.Equal(instrument.AttackDecay, machine.Hex(14, column + 1));
                Assert.Equal(instrument.SustainRelease, machine.Hex(15, column + 1));

                Assert.Equal(Held(zp.VoiceLastWaveform), machine.Hex(16, column + 1));
                int pulse = (Held(zp.VoicePwHi) & 0x0F) << 8 | Held(zp.VoicePwLo);
                Assert.Equal(pulse.ToString("X3"), machine.Text(17, column, 3));

                int previous = Held(zp.VoicePrevPw);
                string restart = (previous & 0x20) != 0 ? "OFF" : (previous & 0x80) != 0 ? "SFT" : "ON ";
                Assert.Equal(restart, machine.Text(18, column, 3));
                seen.Add(restart);
            }

            // The registers themselves, which the program cannot read but a test can.
            Assert.Equal(machine.LastSid[0x18], machine.Hex(20, 17));
            Assert.Equal(machine.LastSid[0x17], machine.Hex(21, 17));
            Assert.Equal(machine.LastSid[0x16], machine.Hex(22, 17));
            Assert.Equal(cpu.Read(zp.FilterDelta), machine.Hex(23, 17));
        }

        Assert.Contains(seen, text => text.Length == 3 && char.IsAsciiLetter(text[0]) && char.IsAsciiDigit(text[2]));
    }

    /// <summary>A pattern's stream walked as the player walks it: for each row, the offset past it
    /// and the note or command it plays.</summary>
    private static Dictionary<int, int> RowsOf(byte[] stream)
    {
        const int SlideCommand = 0xD3, LoopEnd = 0xD2, RowWait = 0xD1;

        var rows = new Dictionary<int, int>();
        for (int i = 0; i < stream.Length && stream[i] != 0;)
        {
            int b = stream[i];
            if (b is >= 0x80 and < 0xD0)
            {
                i++;
            }
            else if (b == LoopEnd)
            {
                i += 3;
            }
            else if (b == SlideCommand)
            {
                i += 2;
                rows[i] = RowWait;
            }
            else
            {
                i++;
                rows[i] = b;
            }
        }

        return rows;
    }

    private static string NoteText(Dictionary<int, int> rows, int position, NoteDictionary notes) =>
        !rows.TryGetValue(position, out int row) ? "..." : row switch
        {
            0xD0 => "---",
            0xD1 => "...",
            _ => notes.Notes[row - 1].ToString(),
        };

    [Fact]
    public void A_note_off_and_a_wait_each_show_as_such()
    {
        var song = WithEveryFeature();
        var machine = Boot(song);
        var shown = new HashSet<string>();
        for (int frame = 0; frame < 200; frame++)
        {
            machine.Interrupt();
            shown.Add(machine.Text(11, 16, 3));
        }

        Assert.Contains("---", shown);
        Assert.Contains("...", shown);
        Assert.Contains("C-4", shown);
    }

    [Theory]
    [MemberData(nameof(Songs))]
    public void The_shower_then_the_player_and_song_lie_end_to_end(string file)
    {
        var song = Load(file);
        var shower = ShowerProgram.Build(song, "Title", "Author");

        Assert.Equal(0x0801, ShowerProgram.LoadAddress);
        Assert.True(shower.PlayerAddress > ShowerProgram.LoadAddress);

        // Directly after: the two jumps at the player's head, and music.init is the first's target.
        Assert.Equal(0x4C, shower.Image[shower.PlayerAddress - ShowerProgram.LoadAddress]);
        Assert.Equal(0x4C, shower.Image[shower.PlayerAddress - ShowerProgram.LoadAddress + 3]);

        // Song data is the same bytes from the end backwards, patterns' pointers aside.
        byte[] plain = SongImageBuilder.Build(song, shower.PlayerAddress);
        Assert.Equal(plain[^20..], shower.Image[^20..]);
        Assert.InRange(shower.End, shower.PlayerAddress + plain.Length, 0xD000);
    }

    [Theory]
    [InlineData("Airstrike.s")]
    [InlineData("BellaCiao.s")]
    public void Its_zero_page_stays_clear_of_the_players(string file)
    {
        var song = Load(file);
        var zp = new ZeroPageLayout(SongImageBuilder.FeaturesOf(song));

        Assert.All(new[] { ShowerProgram.Shadows.Note + 2 * ZeroPageLayout.VoiceStride, ShowerProgram.Shadows.FilterCutoff, ShowerProgram.Shadows.FilterResonance, ShowerProgram.Shadows.FilterModeVolume },
            address => Assert.InRange(address, 2, zp.Base - 1));
        Assert.Equal((2, zp.End - 1), ShowerProgram.ZeroPageRange(song));
    }

    [Fact]
    public void Its_name_and_author_are_on_the_screen_and_a_long_name_is_cut()
    {
        var machine = Boot(WithEveryFeature(), "Bella ciao", "Magnar");
        Assert.Equal("BELLA CIAO", machine.Text(0, 15, 10));
        Assert.Equal("MAGNAR", machine.Text(1, 17, 6));

        var longName = Boot(WithEveryFeature(), new string('x', 60), "");
        Assert.Equal(new string('X', 40), longName.Text(0, 0, 40));
        Assert.Equal(new string(' ', 40), longName.Text(1, 0, 40));
    }

    [Fact]
    public void A_song_too_large_to_fit_below_the_io_area_is_refused()
    {
        var song = WithEveryFeature();
        song.Patterns.Clear();
        foreach (var channel in song.ChannelOrders)
            channel.Tokens.Clear();

        var note = new NoteRow(new NoteName('C', false, 4), 1);
        for (int number = 1; number <= 240; number++)
        {
            var pattern = new Pattern { Number = number };
            for (int row = 0; row < 240; row++)
                pattern.Rows.Add(note);
            song.Patterns.Add(pattern);
            song.ChannelOrders[0].InsertEntry(number - 1, number);
        }

        song.ChannelOrders[1].InsertEntry(0, 1);
        song.ChannelOrders[2].InsertEntry(0, 1);

        var problem = Assert.Throws<SidExportException>(() => SidExporter.BuildShower(song, "T", "A"));
        Assert.Contains("I/O", problem.Message);
    }

    [Fact]
    public void A_prg_and_a_disk_hold_the_same_program()
    {
        var song = Load("BellaCiao.s");
        byte[] prg = SidExporter.ExportShowerPrg(song, "Bella Ciao", "M");
        byte[] disk = SidExporter.ExportShowerD64(song, "Bella Ciao", "M");

        Assert.Equal(1, prg[0]);
        Assert.Equal(8, prg[1]);
        Assert.Equal(prg, D64ImageTests.ReadOnlyFile(disk));
    }
}
