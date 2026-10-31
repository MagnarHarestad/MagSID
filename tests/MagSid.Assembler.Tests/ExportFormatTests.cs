using MagSid.Assembler;
using MagSid.Core.Serialization;
using MagSid.Editor.App.Export;
using MagSid.Sid;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The .prg and raw .bin exports against what c6510 produced: the same goldens that pin the image, so
/// these say the file is that image and nothing else - with the load address in front for a .prg.
/// </summary>
public class ExportFormatTests
{
    private static string SongPath(string file) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData", file));

    private static string GoldenPath(string golden) => Path.Combine(AppContext.BaseDirectory, "Goldens",
        Core.HardRestart.KeepsWaveform ? "GateOff" : "", golden + ".bin");

    private static Core.Model.Song BellaCiao() => PatternsFileReader.Read(SongPath("BellaCiao.s"));

    [Theory]
    [InlineData(0x1000, "bellaciao")]
    [InlineData(0x2000, "bellaciao_2000")]
    [InlineData(0x4400, "bellaciao_4400")]
    public void A_raw_binary_is_the_image_c6510_built(int address, string golden)
    {
        Assert.Equal(File.ReadAllBytes(GoldenPath(golden)), SidExporter.ExportBinary(BellaCiao(), address));
    }

    [Theory]
    [InlineData(0x1000, "bellaciao")]
    [InlineData(0x2000, "bellaciao_2000")]
    [InlineData(0x4400, "bellaciao_4400")]
    public void A_prg_is_that_image_with_its_address_in_front_low_byte_first(int address, string golden)
    {
        byte[] prg = SidExporter.ExportPrg(BellaCiao(), address);

        Assert.Equal(address & 0xFF, prg[0]);
        Assert.Equal(address >> 8, prg[1]);
        Assert.Equal(File.ReadAllBytes(GoldenPath(golden)), prg[2..]);
    }

    [Fact]
    public void A_sid_still_carries_the_same_image()
    {
        var song = BellaCiao();
        byte[] sid = SidExporter.Export(song, new SidExporter.Metadata("n", "a", "r"), ChipModel.Mos8580, 0x1000);

        Assert.Equal(SidExporter.ExportBinary(song, 0x1000), sid[0x7C..]);
    }

    [Theory]
    [InlineData(0x0200)]    // the KERNAL's workspace
    [InlineData(0xD000)]    // I/O
    [InlineData(0xFF00)]    // runs off the top of memory
    public void An_address_that_cannot_play_is_refused_in_every_format(int address)
    {
        var song = BellaCiao();

        Assert.Throws<SidExportException>(() => SidExporter.ExportPrg(song, address));
        Assert.Throws<SidExportException>(() => SidExporter.ExportBinary(song, address));
    }

    [Fact]
    public void Patterns_no_channel_plays_are_left_out_as_they_are_from_a_sid()
    {
        var song = BellaCiao();
        int before = SidExporter.ExportBinary(song, 0x1000).Length;

        var unused = new Core.Model.Pattern { Number = song.Patterns.Max(p => p.Number) + 1 };
        unused.Rows.Add(new Core.Model.HoldRow());
        song.Patterns.Add(unused);

        Assert.Equal(before, SidExporter.ExportBinary(song, 0x1000).Length);
    }

    private static List<(int Register, int Value)> SidWrites(byte[] image, int frames, Action<int>? everyWrite = null)
    {
        var writes = new List<(int, int)>();
        var memory = new byte[0x10000];
        image.CopyTo(memory, 0x1000);
        var cpu = new Playback.Mos6502(memory, (address, value) =>
        {
            everyWrite?.Invoke(address);
            if (address is >= 0xD400 and <= 0xD41C)
                writes.Add((address, value));
        });

        cpu.Call(0x1000);
        for (int frame = 0; frame < frames; frame++)
            cpu.Call(0x1003);
        return writes;
    }

    [Theory]
    [InlineData("BellaCiao.s", 0x02)]
    [InlineData("BellaCiao.s", 0x80)]
    [InlineData("BellaCiao.s", 0xBB)]           // the last that fits: $BB-$FF
    [InlineData("SOS.s", 0xC0)]
    [InlineData("Magnificent.s", 0x90)]
    public void A_moved_zero_page_plays_the_same_and_stays_where_it_was_put(string file, int zeroPage)
    {
        var song = PatternsFileReader.Read(SongPath(file));
        byte[] moved = SidExporter.ExportBinary(song, 0x1000, zeroPage);
        byte[] standard = SidExporter.ExportBinary(song, 0x1000);
        var (first, last) = SidExporter.ZeroPageRange(song, zeroPage);

        Assert.Equal(standard.Length, moved.Length);
        Assert.Equal(zeroPage, first);

        var outside = new HashSet<int>();
        var writes = SidWrites(moved, 3000, address =>
        {
            // The stack and return addresses are the CPU's; only zero page is the player's to keep to.
            if (address < 0x100 && (address < first || address > last))
                outside.Add(address);
        });

        Assert.Empty(outside);
        Assert.Equal(SidWrites(standard, 3000), writes);
    }

    [Theory]
    [InlineData("SOS.s", 0xD0)]                 // 48 bytes, the smallest player: no effects
    [InlineData("BellaCiao.s", 0xBB)]           // 69, with vibrato
    public void The_top_of_zero_page_ends_on_ff_whatever_the_song_needs(string file, int top)
    {
        var song = PatternsFileReader.Read(SongPath(file));

        Assert.Equal(top, SidExporter.TopZeroPage(song));
        Assert.Equal((top, 0xFF), SidExporter.ZeroPageRange(song, top));
        Assert.Equal(SidExporter.ExportBinary(song).Length, SidExporter.ExportBinary(song, 0x1000, top).Length);
    }

    [Theory]
    [InlineData("slide_and_vib.s")]
    [InlineData("funk_slide.s")]
    public void The_top_fits_with_the_optional_parts_built_in(string fixture)
    {
        var song = PatternsFileReader.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));

        Assert.Equal(0xFF, SidExporter.ZeroPageRange(song, SidExporter.TopZeroPage(song)).Last);
        Assert.NotEmpty(SidExporter.ExportBinary(song, 0x1000, SidExporter.TopZeroPage(song)));
    }

    [Fact]
    public void The_zero_page_it_reports_moves_with_the_start()
    {
        Assert.Equal((0x80, 0xC4), SidExporter.ZeroPageRange(BellaCiao(), 0x80));
    }

    [Fact]
    public void A_moved_zero_page_is_what_c6510_built()
    {
        Assert.Equal(File.ReadAllBytes(GoldenPath("bellaciao_zp80")), SidExporter.ExportBinary(BellaCiao(), 0x1000, 0x80));
    }

    [Theory]
    [InlineData(0x00)]      // the CPU's own port
    [InlineData(0x01)]
    [InlineData(0xBC)]      // one too high for BellaCiao's 69 bytes
    [InlineData(0xFF)]
    [InlineData(0x100)]     // not zero page at all
    public void A_zero_page_the_player_does_not_fit_in_is_refused_in_every_format(int zeroPage)
    {
        var song = BellaCiao();
        var metadata = new SidExporter.Metadata("n", "a", "r");

        Assert.Throws<SidExportException>(() => SidExporter.Export(song, metadata, ChipModel.Mos8580, 0x1000, zeroPage));
        Assert.Throws<SidExportException>(() => SidExporter.ExportPrg(song, 0x1000, zeroPage));
        Assert.Throws<SidExportException>(() => SidExporter.ExportBinary(song, 0x1000, zeroPage));
    }

    [Theory]
    [InlineData("BellaCiao.s", 0x20, 0x64)]     // vibrato
    [InlineData("SOS.s", 0x20, 0x4F)]           // neither effect
    public void The_zero_page_it_reports_is_what_the_player_was_built_with(string file, int first, int last)
    {
        var song = PatternsFileReader.Read(SongPath(file));

        Assert.Equal((first, last), SidExporter.ZeroPageRange(song));

        var layout = new ZeroPageLayout(SongImageBuilder.FeaturesOf(song));
        Assert.Equal(layout.ByteCount, last - first + 1);
    }
}
