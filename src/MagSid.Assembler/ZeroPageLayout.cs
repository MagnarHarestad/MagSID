namespace MagSid.Assembler;

/// <summary>
/// Where each of the player's variables sits in zero page.
///
/// This has to agree with musicplayer.s to the byte, whose Lua hands the addresses out the same way:
/// ZP_lb and ZP_hb first, then the arrays, then the single variables. So the order of the
/// declarations below is not cosmetic - it is the layout, and a name moved one line changes every
/// instruction that touches it.
///
/// X runs 0, 7, 14, so an array's three copies are seven bytes apart: seven arrays interleave across
/// three rows of seven, and one index reaches both a voice's state and its SID registers. The single
/// variables fill what the last row leaves, then go on from its end.
/// </summary>
public sealed class ZeroPageLayout
{
    /// <summary>What X steps by between voices, and the distance between SID voices too.</summary>
    public const int VoiceStride = 7;

    private const int ArraysPerGroup = VoiceStride;
    private const int GroupBytes = 3 * VoiceStride;

    private readonly int _grid;
    private readonly HashSet<int> _taken = [];
    private int _arrays;
    private int _last;

    public ZeroPageLayout(PlayerFeatures features, int baseAddress = DefaultBaseAddress)
    {
        Base = baseAddress;

        ZpLb = baseAddress;
        ZpHb = baseAddress + 1;
        _grid = baseAddress + 2;
        _last = ZpHb;

        VoicePatternIdx = Array();
        VoiceEvent = Array();
        VoiceDuration = Array();
        VoiceRowDur = Array();
        VoiceWtPos = Array();
        VoiceArpPos = Array();
        VoiceRoot = Array();
        VoicePwLo = Array();
        VoicePwHi = Array();
        VoiceInst = Array();
        VoicePrevPw = Array();
        VoiceLoopCounter = Array();
        VoiceLastWaveform = Array();
        VoiceOrderPos = Array();

        // Slide and vibrato state, present only when the song uses them - which is what makes
        // everything after them move between songs.
        if (features.FreqMod)
        {
            VoiceFreqLo = Array();
            VoiceFreqHi = Array();
            VoiceOffLo = Array();
            VoiceOffHi = Array();
        }

        if (features.Vibrato)
        {
            VoiceVibDir = Array();
            VoiceVibCnt = Array();
            VoiceVibDelay = Array();
        }

        // The rate a pattern's slide row set, per voice - only when some pattern has one.
        if (features.Slide)
        {
            VoiceSlide = Array();
        }

        SongSpeed = Scalar();
        FilterPos = Scalar();
        FilterPhase = Scalar();
        FilterDelta = Scalar();

        // Funk speed's shuffle byte, last - so a song without it keeps every address it had.
        if (features.Funk)
            SongShuffle = Scalar();

        End = _last + 1;
    }

    public const int DefaultBaseAddress = 0x20;

    public int Base { get; }

    /// <summary>One past the last byte used, so <c>End - Base</c> is ZP_Player_Amount.</summary>
    public int End { get; }

    public int ByteCount => End - Base;

    public int ZpLb { get; }
    public int ZpHb { get; }
    public int SongSpeed { get; }

    public int VoicePatternIdx { get; }
    public int VoiceEvent { get; }
    public int VoiceDuration { get; }
    public int VoiceRowDur { get; }
    public int VoiceWtPos { get; }
    public int VoiceArpPos { get; }
    public int VoiceRoot { get; }
    public int VoicePwLo { get; }
    public int VoicePwHi { get; }
    public int VoiceInst { get; }
    public int VoicePrevPw { get; }
    public int VoiceLoopCounter { get; }
    public int VoiceLastWaveform { get; }
    public int VoiceOrderPos { get; }

    /// <summary>Zero unless the song uses vibrato or slide - reading one of these when the feature
    /// is off would be a port bug, so they are left obviously invalid rather than defaulted.</summary>
    public int VoiceFreqLo { get; }
    public int VoiceFreqHi { get; }
    public int VoiceOffLo { get; }
    public int VoiceOffHi { get; }

    public int VoiceVibDir { get; }
    public int VoiceVibCnt { get; }
    public int VoiceVibDelay { get; }
    public int VoiceSlide { get; }

    /// <summary>Song_Shuffle: zero unless the song has funk speed. music.init clears it, and every
    /// tick flips it with the two speeds' difference.</summary>
    public int SongShuffle { get; }

    public int FilterPos { get; }
    public int FilterPhase { get; }
    public int FilterDelta { get; }

    private int Scalar()
    {
        int address = _grid;
        while (!_taken.Add(address))
            address++;

        _last = Math.Max(_last, address);
        return address;
    }

    private int Array()
    {
        int address = _grid + _arrays / ArraysPerGroup * GroupBytes + _arrays % ArraysPerGroup;
        _arrays++;
        for (int voice = 0; voice < 3; voice++)
            _taken.Add(address + voice * VoiceStride);

        _last = Math.Max(_last, address + 2 * VoiceStride);
        return address;
    }
}

/// <summary>Which of the optional effects this song needs, and therefore how much of the player is
/// assembled at all. Mirrors v_vibrato / v_slide / v_freqmod in Startup\Tables.s, which are set by
/// scanning the song's INSTRUMENTS block.</summary>
/// <param name="SoftHardRestart">Some instrument has SOFTHR, so the hard restart has to look at
/// the outgoing instrument's flag; without one the test is left out of the player.</param>
public sealed record PlayerFeatures(bool Vibrato, bool Slide, bool SoftHardRestart = false, bool Funk = false)
{
    public static readonly PlayerFeatures None = new(false, false);

    /// <summary>Vibrato and slide share the frequency-modulation block, which is why having both
    /// costs less than the two on their own added together.</summary>
    public bool FreqMod => Vibrato || Slide;
}
