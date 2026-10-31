using MagSid.Editor.App.Export;

namespace MagSid.Assembler.Tests;

/// <summary>The disk read back the way a 1541 reads it: the directory, then the file's sector chain,
/// and the BAM against which sectors that chain really took.</summary>
public class D64ImageTests
{
    private const int SectorBytes = 256;
    private const int DirectoryTrack = 18;
    private const int TotalFreeOnEmptyDisk = 664;

    private static int SectorsOn(int track) => track <= 17 ? 21 : track <= 24 ? 19 : track <= 30 ? 18 : 17;

    private static int Offset(int track, int sector)
    {
        int sectors = sector;
        for (int t = 1; t < track; t++)
            sectors += SectorsOn(t);
        return sectors * SectorBytes;
    }

    private static List<(int Track, int Sector)> Chain(byte[] disk)
    {
        int entry = Offset(DirectoryTrack, 1) + 2;
        var chain = new List<(int, int)>();
        for (var (track, sector) = (disk[entry + 1], disk[entry + 2]); ;)
        {
            chain.Add((track, sector));
            int offset = Offset(track, sector);
            if (disk[offset] == 0)
                return chain;
            (track, sector) = (disk[offset], disk[offset + 1]);
        }
    }

    public static byte[] ReadOnlyFile(byte[] disk)
    {
        var file = new List<byte>();
        foreach (var (track, sector) in Chain(disk))
        {
            int offset = Offset(track, sector);
            // The last sector's link says the index of its last byte.
            int end = disk[offset] == 0 ? disk[offset + 1] + 1 : SectorBytes;
            file.AddRange(disk[(offset + 2)..(offset + end)]);
        }

        return file.ToArray();
    }

    private static byte[] Program(int length)
    {
        var program = new byte[length];
        for (int i = 0; i < length; i++)
            program[i] = (byte)(i * 7 + (i >> 8));
        return program;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(253)]
    [InlineData(254)]       // exactly one sector's worth
    [InlineData(255)]
    [InlineData(508)]
    [InlineData(2500)]
    [InlineData(51000)]     // about all a C64 could hold, which crosses into other tracks
    public void The_file_reads_back_as_it_went_in(int length)
    {
        byte[] program = Program(length);
        byte[] disk = D64Image.Create("Song", program);

        Assert.Equal(D64Image.ByteCount, disk.Length);
        Assert.Equal(program, ReadOnlyFile(disk));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2500)]
    [InlineData(51000)]
    public void The_bam_marks_exactly_what_the_file_and_directory_took(int length)
    {
        byte[] disk = D64Image.Create("Song", Program(length));
        var taken = Chain(disk).ToHashSet();
        int bam = Offset(DirectoryTrack, 0);

        int free = 0;
        for (int track = 1; track <= 35; track++)
        {
            int count = 0;
            for (int sector = 0; sector < SectorsOn(track); sector++)
            {
                bool markedFree = (disk[bam + 4 * track + 1 + sector / 8] & (1 << (sector % 8))) != 0;
                bool used = taken.Contains((track, sector)) || track == DirectoryTrack && sector <= 1;
                Assert.Equal(!used, markedFree);
                if (markedFree)
                    count++;
            }

            Assert.Equal(count, disk[bam + 4 * track]);
            if (track != DirectoryTrack)
                free += count;
        }

        Assert.Equal(TotalFreeOnEmptyDisk - taken.Count, free);
    }

    [Fact]
    public void The_directory_lists_one_closed_program_with_its_size()
    {
        byte[] disk = D64Image.Create("Bella ciao", Program(2500));
        int directory = Offset(DirectoryTrack, 1);
        int sectors = Chain(disk).Count;

        Assert.Equal(0x00, disk[directory]);
        Assert.Equal(0xFF, disk[directory + 1]);
        Assert.Equal(0x82, disk[directory + 2]);
        Assert.Equal("BELLA CIAO", System.Text.Encoding.ASCII.GetString(disk, directory + 5, 10));
        Assert.Equal(0xA0, disk[directory + 15]);
        Assert.Equal(sectors, disk[directory + 0x1E]);
        Assert.Equal(0, disk[directory + 0x1F]);
        Assert.Equal(0, disk[directory + 0x22]);    // no second entry
    }

    [Fact]
    public void The_bam_points_at_the_directory_and_names_the_disk()
    {
        byte[] disk = D64Image.Create("Bella ciao", Program(10));
        int bam = Offset(DirectoryTrack, 0);

        Assert.Equal(DirectoryTrack, disk[bam]);
        Assert.Equal(1, disk[bam + 1]);
        Assert.Equal((byte)'A', disk[bam + 2]);
        Assert.Equal("BELLA CIAO", System.Text.Encoding.ASCII.GetString(disk, bam + 0x90, 10));
        Assert.Equal(0xA0, disk[bam + 0x9A]);
        Assert.Equal("2A", System.Text.Encoding.ASCII.GetString(disk, bam + 0xA5, 2));
    }

    [Fact]
    public void The_file_starts_beside_the_directory_at_the_drives_own_interleave()
    {
        var chain = Chain(D64Image.Create("Song", Program(2500)));

        Assert.Equal((17, 0), chain[0]);
        Assert.Equal((17, 10), chain[1]);
        Assert.Equal((17, 20), chain[2]);
    }

    [Theory]
    [InlineData("Bella Ciao", "BELLA CIAO")]
    [InlineData("  spaced  ", "SPACED")]
    [InlineData("a,b*c?d", "A-B-C-D")]
    [InlineData("CityBomber4k(Megastyle)", "CITYBOMBER4K-MEG")]
    [InlineData("", "MUSIC")]
    [InlineData("   ", "MUSIC")]
    public void A_file_name_is_one_a_load_command_can_say(string name, string expected)
    {
        Assert.Equal(expected, D64Image.FileNameFor(name));
    }
}
