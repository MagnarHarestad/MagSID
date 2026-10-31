using System.Text;

namespace MagSid.Editor.App.Export;

/// <summary>
/// A 35-track 1541 disk image with one program on it, laid out the way the drive's own DOS would:
/// the BAM and directory on track 18, the file from track 17 outwards at its usual interleave of 10.
/// </summary>
public static class D64Image
{
    public const int ByteCount = 174848;

    private const int SectorBytes = 256;
    private const int Tracks = 35;
    private const int DirectoryTrack = 18;
    private const int BamSector = 0;
    private const int DirectorySector = 1;
    private const int Interleave = 10;
    private const byte Padding = 0xA0;

    /// <summary>A closed PRG, as the first byte of a directory entry says it.</summary>
    private const byte ClosedProgram = 0x82;

    /// <summary>What each sector carries after its two-byte link to the next.</summary>
    private const int DataPerSector = SectorBytes - 2;

    private static int SectorsOn(int track) => track switch
    {
        <= 17 => 21,
        <= 24 => 19,
        <= 30 => 18,
        _ => 17,
    };

    private static int Offset(int track, int sector)
    {
        int sectors = sector;
        for (int t = 1; t < track; t++)
            sectors += SectorsOn(t);
        return sectors * SectorBytes;
    }

    /// <param name="program">The file as a C64 has it, with its load address in front.</param>
    /// <exception cref="ArgumentException">It does not fit on one disk.</exception>
    public static byte[] Create(string name, byte[] program)
    {
        if (program.Length == 0)
            throw new ArgumentException("There is nothing to put on the disk.", nameof(program));

        var image = new byte[ByteCount];
        var used = new bool[Tracks + 1][];
        for (int track = 1; track <= Tracks; track++)
            used[track] = new bool[SectorsOn(track)];

        used[DirectoryTrack][BamSector] = true;
        used[DirectoryTrack][DirectorySector] = true;

        var (firstTrack, firstSector, sectors) = WriteFile(image, used, program);
        WriteDirectory(image, name, firstTrack, firstSector, sectors);
        WriteBam(image, name, used);
        return image;
    }

    /// <summary>Upper case, and nothing a LOAD command reads as anything but the name. Never empty,
    /// which a directory entry cannot be.</summary>
    public static string FileNameFor(string name)
    {
        var fitted = new StringBuilder();
        foreach (char c in name.Trim().ToUpperInvariant())
            fitted.Append(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' ? c : '-');

        string result = fitted.ToString().TrimEnd();
        if (result.Length > 16)
            result = result[..16].TrimEnd();
        return result.Length > 0 ? result : "MUSIC";
    }

    private static (int Track, int Sector, int Count) WriteFile(byte[] image, bool[][] used, byte[] program)
    {
        int firstTrack = 0, firstSector = 0, count = 0;
        int track = DirectoryTrack - 1, sector = 0;
        int previous = -1;

        for (int position = 0; position < program.Length; position += DataPerSector)
        {
            (track, sector) = NextFree(used, track, sector, position == 0);
            used[track][sector] = true;
            int offset = Offset(track, sector);

            if (previous >= 0)
            {
                image[previous] = (byte)track;
                image[previous + 1] = (byte)sector;
            }
            else
            {
                firstTrack = track;
                firstSector = sector;
            }

            int length = Math.Min(DataPerSector, program.Length - position);
            Array.Copy(program, position, image, offset + 2, length);

            // Track 0 in the link says this is the last, and the next byte where its data stops.
            image[offset] = 0;
            image[offset + 1] = (byte)(length + 1);

            previous = offset;
            count++;
        }

        return (firstTrack, firstSector, count);
    }

    /// <summary>Out from the directory: track 17 down to 1, then 19 up to 35.</summary>
    private static (int Track, int Sector) NextFree(bool[][] used, int track, int sector, bool first)
    {
        int start = first ? sector : sector + Interleave;
        while (true)
        {
            int count = SectorsOn(track);
            for (int i = 0; i < count; i++)
            {
                int candidate = (start + i) % count;
                if (!used[track][candidate])
                    return (track, candidate);
            }

            start = 0;
            track = track > DirectoryTrack ? track + 1 : track - 1;
            if (track == 0)
                track = DirectoryTrack + 1;
            if (track > Tracks)
                throw new ArgumentException("The program is larger than one disk.");
        }
    }

    private static void WriteDirectory(byte[] image, string name, int firstTrack, int firstSector, int sectors)
    {
        int offset = Offset(DirectoryTrack, DirectorySector);

        image[offset] = 0;                          // no further directory sector
        image[offset + 1] = 0xFF;

        image[offset + 2] = ClosedProgram;
        image[offset + 3] = (byte)firstTrack;
        image[offset + 4] = (byte)firstSector;
        PutName(image, offset + 5, 16, FileNameFor(name));
        image[offset + 0x1E] = (byte)sectors;
        image[offset + 0x1F] = (byte)(sectors >> 8);
    }

    private static void WriteBam(byte[] image, string name, bool[][] used)
    {
        int offset = Offset(DirectoryTrack, BamSector);

        image[offset] = DirectoryTrack;
        image[offset + 1] = DirectorySector;
        image[offset + 2] = (byte)'A';              // the format of a 1541 disk

        for (int track = 1; track <= Tracks; track++)
        {
            int entry = offset + 4 * track;
            int free = 0;
            for (int sector = 0; sector < SectorsOn(track); sector++)
            {
                if (used[track][sector])
                    continue;

                free++;
                image[entry + 1 + sector / 8] |= (byte)(1 << (sector % 8));
            }

            image[entry] = (byte)free;
        }

        PutName(image, offset + 0x90, 16, FileNameFor(name));
        image[offset + 0xA0] = Padding;
        image[offset + 0xA1] = Padding;
        image[offset + 0xA2] = (byte)'M';           // the disk id
        image[offset + 0xA3] = (byte)'S';
        image[offset + 0xA4] = Padding;
        image[offset + 0xA5] = (byte)'2';           // the DOS type
        image[offset + 0xA6] = (byte)'A';
        for (int i = 0xA7; i <= 0xAA; i++)
            image[offset + i] = Padding;
    }

    private static void PutName(byte[] image, int offset, int length, string name)
    {
        for (int i = 0; i < length; i++)
            image[offset + i] = i < name.Length ? (byte)name[i] : Padding;
    }
}
