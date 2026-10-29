namespace MagSid.Core;

/// <summary>
/// The tracker piano by where its keys are on the keyboard rather than what is printed on them:
/// the key at the bottom left of the letters plays C whether the layout calls it Z, Y or W. Keys
/// are named by their scan codes - their places on a PC keyboard - and a layout's own key codes
/// are found for them when the editor starts and whenever the layout changes, so a German QWERTZ
/// or a French AZERTY keyboard plays the same notes from the same places as a Swedish or US one.
///
/// FastTracker 2's layout: ZSXDCVGBHNJM an octave and , L . ; / on into the next; Q2W3ER5T6Y7U the
/// octave above, and I9O0P[=] on into the one above that.
/// </summary>
public static class PianoKeyPositions
{
    /// <summary>Each key's scan code and the semitones it plays above the base octave's C - the
    /// upper row starting an octave up.</summary>
    public static readonly IReadOnlyList<(int Scan, int Semitones)> Keys =
    [
        // Z S X D C V G B H N J M , L . ; /
        (0x2C, 0), (0x1F, 1), (0x2D, 2), (0x20, 3), (0x2E, 4), (0x2F, 5), (0x22, 6), (0x30, 7),
        (0x23, 8), (0x31, 9), (0x24, 10), (0x32, 11), (0x33, 12), (0x26, 13), (0x34, 14), (0x27, 15),
        (0x35, 16),

        // Q 2 W 3 E R 5 T 6 Y 7 U I 9 O 0 P [ = ]
        (0x10, 12), (0x03, 13), (0x11, 14), (0x04, 15), (0x12, 16), (0x13, 17), (0x06, 18), (0x14, 19),
        (0x07, 20), (0x15, 21), (0x08, 22), (0x16, 23), (0x17, 24), (0x0A, 25), (0x18, 26), (0x0B, 27),
        (0x19, 28), (0x1A, 29), (0x0D, 30), (0x1B, 31),
    ];

    /// <summary>The key that changes the octave - down, and up with Shift: where a Swedish keyboard
    /// has its apostrophe, beside Enter; # on a German one, * on a French one, \ on a US one.</summary>
    public const int OctaveScan = 0x2B;

    /// <summary>The semitones each of a layout's key codes plays, given how the layout codes each
    /// scan code - 0 for one it does not have. A key code two places share plays the first's.</summary>
    public static Dictionary<int, int> ForLayout(Func<int, int> keyCodeOf)
    {
        var map = new Dictionary<int, int>();
        foreach (var (scan, semitones) in Keys)
        {
            int code = keyCodeOf(scan);
            if (code != 0)
                map.TryAdd(code, semitones);
        }
        return map;
    }
}
