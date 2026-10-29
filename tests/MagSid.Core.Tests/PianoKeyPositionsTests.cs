using MagSid.Core;

namespace MagSid.Core.Tests;

/// <summary>
/// The tracker piano by place: three layouts' key codes for the same keys, and the same notes from
/// each. The layouts are written out here as Windows codes them - a US or Swedish QWERTY, a German
/// QWERTZ with Y and Z swapped, a French AZERTY with A, Q, W, Z and M elsewhere.
/// </summary>
public class PianoKeyPositionsTests
{
    private const int Comma = 0xBC, Period = 0xBE, Slash = 0xBF, Semicolon = 0xBA, OpenBracket = 0xDB,
                      CloseBracket = 0xDD, Plus = 0xBB, Backslash = 0xDC, Quote = 0xDE, Ugrave = 0xC0,
                      Minus = 0xBD, Exclamation = 0xDF;

    /// <summary>A QWERTY keyboard's key codes by scan code: the letters and digits, and the
    /// punctuation keys the piano and the octave use.</summary>
    private static readonly Dictionary<int, int> Qwerty = Build();

    private static Dictionary<int, int> Build()
    {
        var keys = new Dictionary<int, int>();
        void Row(int scan, string letters)
        {
            foreach (char letter in letters)
                keys[scan++] = letter;
        }
        Row(0x02, "1234567890");
        Row(0x10, "QWERTYUIOP");
        Row(0x1E, "ASDFGHJKL");
        Row(0x2C, "ZXCVBNM");
        keys[0x33] = Comma; keys[0x34] = Period; keys[0x35] = Slash; keys[0x27] = Semicolon;
        keys[0x1A] = OpenBracket; keys[0x1B] = CloseBracket; keys[0x0D] = Plus; keys[0x2B] = Backslash; keys[0x28] = Quote;
        return keys;
    }

    private static Func<int, int> Layout(Dictionary<int, int> changes) =>
        scan => changes.TryGetValue(scan, out int code) ? code : Qwerty.GetValueOrDefault(scan);

    /// <summary>German QWERTZ: Y and Z swapped, and ü + ö ä # ß ´ - on its own codes.</summary>
    private static readonly Func<int, int> German = Layout(new()
    {
        [0x15] = 'Z', [0x2C] = 'Y', [0x1A] = Semicolon, [0x1B] = Plus, [0x27] = Ugrave, [0x28] = Quote,
        [0x2B] = Slash, [0x0C] = OpenBracket, [0x0D] = CloseBracket, [0x35] = Minus,
    });

    /// <summary>French AZERTY: A, Q, W, Z and M elsewhere, ^ and $ after P, ; : ! after the comma.</summary>
    private static readonly Func<int, int> French = Layout(new()
    {
        [0x10] = 'A', [0x11] = 'Z', [0x1E] = 'Q', [0x2C] = 'W', [0x27] = 'M', [0x32] = Comma,
        [0x33] = Period, [0x34] = Slash, [0x35] = Exclamation, [0x1A] = CloseBracket, [0x1B] = Semicolon,
        [0x28] = Ugrave, [0x0C] = OpenBracket,
    });

    /// <summary>The notes a layout plays from the keys of a QWERTY row, by place.</summary>
    private static int[] Played(Func<int, int> layout, params int[] scans)
    {
        var map = PianoKeyPositions.ForLayout(layout);
        return [.. scans.Select(scan => map.TryGetValue(layout(scan), out int semitones) ? semitones : -1)];
    }

    private static readonly int[] LowerRow = [0x2C, 0x1F, 0x2D, 0x20, 0x2E, 0x2F, 0x22, 0x30, 0x23, 0x31, 0x24, 0x32, 0x33, 0x26, 0x34, 0x27, 0x35];

    private static readonly int[] UpperRow = [0x10, 0x03, 0x11, 0x04, 0x12, 0x13, 0x06, 0x14, 0x07, 0x15, 0x08, 0x16, 0x17, 0x0A, 0x18, 0x0B, 0x19, 0x1A, 0x0D, 0x1B];

    [Fact]
    public void On_qwerty_the_rows_play_as_the_tracker_has_always_played_them_and_on_into_the_next_octave()
    {
        var map = PianoKeyPositions.ForLayout(Layout([]));

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], "ZSXDCVGBHNJM".Select(key => map[key]));
        Assert.Equal([12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23], "Q2W3ER5T6Y7U".Select(key => map[key]));
        Assert.Equal([12, 13, 14, 15, 16], new[] { Comma, 'L', Period, Semicolon, Slash }.Select(key => map[key]));
        Assert.Equal([24, 25, 26, 27, 28, 29, 30, 31], new[] { 'I', '9', 'O', '0', 'P', OpenBracket, Plus, CloseBracket }.Select(key => map[key]));
    }

    [Fact]
    public void German_and_french_keyboards_play_the_same_notes_from_the_same_places()
    {
        int[] lower = Played(Layout([]), LowerRow), upper = Played(Layout([]), UpperRow);

        Assert.Equal(Enumerable.Range(0, 17), lower);
        Assert.Equal(Enumerable.Range(12, 20), upper);
        Assert.Equal(lower, Played(German, LowerRow));
        Assert.Equal(upper, Played(German, UpperRow));
        Assert.Equal(lower, Played(French, LowerRow));
        Assert.Equal(upper, Played(French, UpperRow));
    }

    [Fact]
    public void A_key_plays_by_its_place_whatever_the_layout_names_it()
    {
        var german = PianoKeyPositions.ForLayout(German);
        var french = PianoKeyPositions.ForLayout(French);

        // German: the bottom-left letter is Y and plays C; Z sits in the upper row and plays A.
        Assert.Equal((0, 21), (german['Y'], german['Z']));

        // French: A Z E R T Y U I O P plays the upper row and on, as on QWERTY's Q to P; W plays C.
        Assert.Equal([12, 14, 16, 17, 19, 21, 23, 24, 26, 28], "AZERTYUIOP".Select(key => french[key]));
        Assert.Equal(0, french['W']);
        Assert.Equal(11, french[Comma]);
        Assert.False(french.ContainsKey('Q'));

        // ^ and $ - after P - play on into the octave above: F and G.
        Assert.Equal((29, 31), (french[CloseBracket], french[Semicolon]));
    }
}
