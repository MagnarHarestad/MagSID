using System.Runtime.InteropServices;
using MagSid.Core;
using MagSid.Core.Model;

namespace MagSid.Editor.App;

/// <summary>
/// The tracker piano, by where the keys are: ZSXDCVGBHNJM one octave and Q2W3ER5T6Y7U the next one
/// up, each row going on into the octave above - , L . ; / and I9O0P[=] - as in FastTracker 2.
/// Found for the Windows keyboard layout in use, from <see cref="PianoKeyPositions"/>, so a German
/// or French keyboard plays the same notes from the same places as a Swedish one; and found again
/// when the layout changes. Off Windows, from each key's place as the views see it pressed.
///
/// Shared rather than owned by the tracker grid, because the DAW's piano roll plays from the same
/// keys and a second copy of this map would drift from the first the day either changed. Both views
/// also read the octave key from here.
/// </summary>
internal static class PianoKeyMap
{
    private const uint ScanCodeToKeyCode = 1; // MAPVK_VSC_TO_VK

    private static readonly (char Letter, bool IsSharp)[] PitchClasses =
    [
        ('C', false), ('C', true), ('D', false), ('D', true), ('E', false), ('F', false),
        ('F', true), ('G', false), ('G', true), ('A', false), ('A', true), ('B', false),
    ];

    private static IntPtr _layout = new(-1);
    private static Dictionary<int, int> _semitones = [];
    private static Keys _octaveKey;
    private static Keys _apostropheKey;

    // Off Windows: the semitones by place, and the place each key code was last pressed at.
    private static readonly Dictionary<int, int> SemitonesByScan = PianoKeyPositions.ForLayout(scan => scan);
    private static readonly Dictionary<Keys, int> ScanOf = [];

    /// <summary>Lowest and highest octave the base octave may be set to. The keys above it play
    /// up to two octaves higher, which <see cref="Note"/> clamps rather than refuses - the
    /// alternative is keys that go silent at the top of the range.</summary>
    public const int MinOctave = 0;
    public const int MaxOctave = 7;

    /// <summary>Whether a key changes the octave - Shift with it for up: the key where a Swedish
    /// keyboard has its apostrophe, and on any layout the key that types one, so a US keyboard
    /// has it on ' as well as on \.</summary>
    public static bool IsOctaveKey(Keys key)
    {
        if (!OperatingSystem.IsWindows())
            return (ScanOf.TryGetValue(key, out int scan) && scan == PianoKeyPositions.OctaveScan) || key == _apostropheKey;

        Refresh();
        return key == _octaveKey || key == _apostropheKey;
    }

    /// <summary>The pitch class and octave a key plays from the given base octave, or null when it
    /// is not a piano key.</summary>
    public static (int PitchClass, int Octave)? Play(Keys key, int octave)
    {
        int semitones;
        if (!OperatingSystem.IsWindows())
        {
            if (!ScanOf.TryGetValue(key, out int scan) || !SemitonesByScan.TryGetValue(scan, out semitones))
                return null;
        }
        else
        {
            Refresh();
            if (!_semitones.TryGetValue((int)key, out semitones))
                return null;
        }
        return (semitones % 12, octave + semitones / 12);
    }

    /// <summary>The note a key plays with the given base octave, or null when it is not a piano key.</summary>
    public static NoteName? TryNote(Keys key, int octave) =>
        Play(key, octave) is { } played ? Note(played.PitchClass, played.Octave) : null;

    public static NoteName Note(int pitchClass, int octave)
    {
        var (letter, isSharp) = PitchClasses[pitchClass];
        return new NoteName(letter, isSharp, Math.Clamp(octave, MinOctave, MaxOctave));
    }

    /// <summary>Steps the octave within range - the same move both views make on the octave key.
    /// </summary>
    public static int StepOctave(int octave, bool up) =>
        Math.Clamp(octave + (up ? 1 : -1), MinOctave, MaxOctave);

    /// <summary>Off Windows there is no layout to ask, so the views tell the map, as each key is
    /// pressed, where it is - its scan code, 0 for none - and what it types.</summary>
    public static void Seen(Keys key, int scan, string? typed)
    {
        if (key == Keys.None || scan == 0)
            return;
        ScanOf[key] = scan;
        if (typed == "'" && !SemitonesByScan.ContainsKey(scan))
            _apostropheKey = key;
    }

    /// <summary>The keys found again when the layout in use is not the one they were found for.</summary>
    private static void Refresh()
    {
        IntPtr layout = GetKeyboardLayout(0);
        if (layout == _layout)
            return;
        _layout = layout;

        _semitones = PianoKeyPositions.ForLayout(scan => (int)MapVirtualKeyEx((uint)scan, ScanCodeToKeyCode, layout));
        _octaveKey = (Keys)MapVirtualKeyEx(PianoKeyPositions.OctaveScan, ScanCodeToKeyCode, layout);

        // Where ' is typed - unless that key plays a note on this layout.
        short apostrophe = VkKeyScanEx('\'', layout);
        _apostropheKey = apostrophe != -1 && !_semitones.ContainsKey(apostrophe & 0xFF) ? (Keys)(apostrophe & 0xFF) : Keys.None;
    }

    [DllImport("user32.dll")]
    private static extern short VkKeyScanEx(char ch, IntPtr hkl);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
}
