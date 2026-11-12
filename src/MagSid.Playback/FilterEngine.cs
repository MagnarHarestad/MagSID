using MagSid.Core.Model;

namespace MagSid.Playback;

/// <summary>
/// The SID's one shared filter program - whichever instrument last claimed it, across every
/// voice, owns it until another instrument with a filter program claims it in turn, exactly as
/// real hardware has one filter, not one per voice. Extracted out of <see cref="SongSequencer"/>
/// for the same reason <see cref="VoiceEngine"/> was: <see cref="JamEngine"/>'s chords claim the
/// filter through live NoteOn calls instead of pattern rows, but the stepping and sweep math
/// must be identical either way.
/// </summary>
public sealed class FilterEngine
{
    private IReadOnlyList<FilterStep>? _program;
    private int _stepIndex;

    /// <summary>The 0-based step last played, and the instrument whose program it belongs to -
    /// the SID has one filter, so whichever instrument claimed it last owns these.</summary>
    public int LastStep { get; private set; } = -1;
    public int OwnerInstrumentNumber { get; private set; } = -1;

    /// <summary>Frames stepped since the owner claimed the filter - 1 on the frame its first step
    /// plays - and the cutoff last written to $D416, by a step or by the sweep. For showing where
    /// the filter is, not used by the stepping itself.</summary>
    public int FramesSinceClaim { get; private set; }
    public int Cutoff { get; private set; } = -1;

    // The sweep outlives the program that started it, so it lives here rather than being reset
    // by Claim: the cutoff it is at, its signed rate, and the two cutoffs it turns back at.
    private int _sweepCut;
    private int _sweepRate;
    private int _sweepStart;
    private int _sweepEnd;

    /// <summary>Lets the filter go: no program, no sweep. Writes nothing - the caller sets the
    /// registers it wants the chip left with.</summary>
    public void Reset()
    {
        _program = null;
        _stepIndex = 0;
        _sweepCut = 0;
        _sweepRate = 0;
        _sweepStart = 0;
        _sweepEnd = 0;
        LastStep = -1;
        OwnerInstrumentNumber = -1;
        FramesSinceClaim = 0;
        Cutoff = -1;
    }

    /// <summary>Hands this instrument the shared filter, restarting its program from the top.
    /// Call only when the instrument actually has one (Filter.Count &gt; 0) - a note with no
    /// filter program of its own should leave whichever voice already owns the filter
    /// alone.</summary>
    public void Claim(IReadOnlyList<FilterStep> program, int instrumentNumber = -1)
    {
        _program = program;
        _stepIndex = 0;
        LastStep = -1;
        OwnerInstrumentNumber = instrumentNumber;
        FramesSinceClaim = 0;
    }

    /// <summary>One frame: the program step, then the sweep - the same order SongSequencer.Frame
    /// itself calls them in, so on a frame where both write $D416, the sweep is what
    /// stands.</summary>
    public void StepFrame(Action<int, int> write)
    {
        if (_program is not null)
            FramesSinceClaim++;

        StepProgram(write);
        StepSweep(write);
    }

    /// <summary>One filter step, once per frame. One-shot: when the program runs out the cutoff
    /// simply stays where it landed, which is what the assembly's $00 end marker does.</summary>
    private void StepProgram(Action<int, int> write)
    {
        if (_program is null || _stepIndex >= _program.Count)
        {
            return;
        }

        LastStep = _stepIndex;
        var step = _program[_stepIndex++];
        int before = _sweepRate;

        if (step.IsSetup)
        {
            write(0x18, step.ModeVolume!.Value);       // filter mode + master volume
            write(0x17, step.ResonanceRouting!.Value); // resonance + which voices are routed

            // 0 stops a sweep; any other rate starts one, unless one is already running - that one
            // goes on as it is, so a drum hitting again leaves it alone.
            if (step.SweepRate == 0 || before == 0)
            {
                _sweepRate = step.SweepRate;
                _sweepEnd = step.SweepEnd;
            }
        }

        write(0x16, step.Cutoff);
        Cutoff = step.Cutoff;

        // With no sweep running before it, a step is where the next one starts, and turns back to.
        if (before == 0)
        {
            _sweepCut = step.Cutoff;
            _sweepStart = step.Cutoff;
        }
    }

    /// <summary>
    /// The cutoff moves by the signed rate every frame and turns back at either end. The ends are a
    /// whole number of rates apart (see <see cref="FilterStep.ReachableEnd"/>), so it lands on
    /// them, and turning is the rate's sign flipped - 0 - rate, as a byte, as the player does it.
    /// </summary>
    private void StepSweep(Action<int, int> write)
    {
        if (_sweepRate == 0)
        {
            return;
        }

        _sweepCut = (_sweepCut + _sweepRate) & 0xFF;
        write(0x16, _sweepCut);
        Cutoff = _sweepCut;

        if (_sweepCut == _sweepEnd || _sweepCut == _sweepStart)
            _sweepRate = unchecked((sbyte)-_sweepRate);
    }
}
