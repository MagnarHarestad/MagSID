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
