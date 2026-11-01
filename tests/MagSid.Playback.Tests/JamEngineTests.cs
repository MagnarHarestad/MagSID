using MagSid.Core;
using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// Voice allocation and note lifecycle for live jamming - which of the SID's three voices a held
/// note lands on, retriggering, stealing when all three are busy, and release. The actual
/// per-frame automation (wavetable/arpeggio/pulse/filter) is VoiceEngine's and FilterEngine's own
/// logic, already covered via SongSequencerTests since SongSequencer drives the very same code;
/// these tests are about JamEngine's own job on top of that - which voice, and when.
/// </summary>
public class JamEngineTests
{
    private static Instrument MakeInstrument(int number, byte waveform = 0x21, byte ad = 0x09, byte sr = 0xaa) =>
        new() { Number = number, Waveform = waveform, AttackDecay = ad, SustainRelease = sr };

    private static (JamEngine Engine, List<(int Offset, int Value)> Writes) Build()
    {
        var writes = new List<(int Offset, int Value)>();
        var engine = new JamEngine(writeRegister: (o, v) => writes.Add((o, v)));
        writes.Clear(); // constructor priming ($d418 = $0f) isn't part of what a test asserts on
        return (engine, writes);
    }

    private static NoteName Note(string text) => NoteName.Parse(text);

    /// <summary>The instrument is the one being edited, so frames can appear under a note that has
    /// finished. Any of them has its gate bit set, which would start the sound again by itself.</summary>
    [Fact]
    public void A_frame_added_to_the_instrument_does_not_wake_a_released_note()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);
        instrument.Wavetable.Add(0x09);

        engine.NoteOn(Note("C-4"), instrument);
        for (int frame = 0; frame < 3; frame++)
            engine.Frame();
        engine.NoteOff(Note("C-4"));
        engine.Frame();
        writes.Clear();

        instrument.Wavetable.Add(0x41);
        for (int frame = 0; frame < 5; frame++)
            engine.Frame();

        Assert.DoesNotContain(writes, write => write.Offset == 4);
    }

    [Fact]
    public void A_frame_added_while_the_note_is_held_still_plays()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);
        instrument.Wavetable.Add(0x09);

        engine.NoteOn(Note("C-4"), instrument);
        for (int frame = 0; frame < 3; frame++)
            engine.Frame();
        writes.Clear();

        instrument.Wavetable.Add(0x41);
        engine.Frame();

        Assert.Contains((4, 0x41), writes);
    }

    [Fact]
    public void The_next_note_plays_the_frames_added_since_the_last()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);
        instrument.Wavetable.Add(0x09);

        engine.NoteOn(Note("C-4"), instrument);
        for (int frame = 0; frame < 3; frame++)
            engine.Frame();
        engine.NoteOff(Note("C-4"));
        engine.Frame();

        instrument.Wavetable.Add(0x41);
        writes.Clear();
        engine.NoteOn(Note("C-4"), instrument);
        for (int frame = 0; frame < 2; frame++)
            engine.Frame();

        Assert.Contains((4, 0x09), writes);
        Assert.Contains((4, 0x41), writes);
    }

    /// <summary>A step past the end used to throw on the audio thread, which stops all sound.</summary>
    [Theory]
    [InlineData(ArpeggioEnding.Loop, 2, 5)]     // the loop past the end, as the From knob could put it
    [InlineData(ArpeggioEnding.Loop, 0, 0)]     // every step deleted while the note sounds
    [InlineData(ArpeggioEnding.Hold, 0, 0)]
    public void A_program_cut_short_under_a_sounding_note_never_fails_a_frame(ArpeggioEnding ending, int stepsLeft, int loopPoint)
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);
        instrument.Arpeggio.AddRange([Note("C-4"), Note("E-4"), Note("G-4")]);
        instrument.ArpeggioEnding = ending;

        engine.NoteOn(Note("C-4"), instrument);
        engine.Frame();

        instrument.Arpeggio.RemoveRange(stepsLeft, instrument.Arpeggio.Count - stepsLeft);
        instrument.ArpeggioLoopPoint = loopPoint;
        for (int frame = 0; frame < 20; frame++)
            engine.Frame();

        Assert.NotEmpty(writes);
    }

    /// <summary>The audio thread reads the instrument the editor is rewriting as it is typed. Without
    /// the lock a frame finds a list empty between its length and its contents.</summary>
    [Fact]
    public void Rewriting_the_programs_while_a_note_plays_never_fails_a_frame()
    {
        var (engine, _) = Build();
        var instrument = MakeInstrument(1);
        instrument.Wavetable.AddRange(Enumerable.Repeat((byte)0x41, 200));
        instrument.Arpeggio.AddRange(Enumerable.Repeat(Note("C-4"), 200));

        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        var audio = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    engine.NoteOn(Note("C-4"), instrument);
                    for (int frame = 0; frame < 150 && !stop.IsCancellationRequested; frame++)
                        engine.Frame();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        audio.Start();

        var waves = Enumerable.Repeat((byte)0x21, 200).ToArray();
        var notes = Enumerable.Repeat(Note("E-4"), 200).ToArray();
        var until = DateTime.UtcNow.AddSeconds(2);
        while (failure is null && DateTime.UtcNow < until)
        {
            lock (Instrument.ProgramLock)
            {
                instrument.Wavetable.Clear();
                instrument.Wavetable.AddRange(waves);
                instrument.Arpeggio.Clear();
                instrument.Arpeggio.AddRange(notes);
            }
        }

        stop.Cancel();
        audio.Join();
        Assert.Null(failure);
    }

    /// <summary>The editor stores these as they are typed, to be heard on the sounding note.</summary>
    [Fact]
    public void A_changed_attack_decay_and_sustain_release_reach_the_sid_on_the_next_frame()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1, ad: 0x09, sr: 0xaa);

        engine.NoteOn(Note("C-4"), instrument);
        engine.Frame();
        writes.Clear();

        instrument.AttackDecay = 0x5c;
        instrument.SustainRelease = 0x3d;
        engine.Frame();

        Assert.Contains((5, 0x5c), writes);
        Assert.Contains((6, 0x3d), writes);
    }

    [Fact]
    public void First_three_notes_take_one_voice_each_in_order()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);

        engine.NoteOn(Note("C-4"), instrument);
        engine.NoteOn(Note("E-4"), instrument);
        engine.NoteOn(Note("G-4"), instrument);

        // TriggerNote writes the frequency lo byte unconditionally, at each voice's own base
        // register (0, 7, 14) - the gate itself only opens on the following Automate(), so the
        // frequency write is the reliable "which voice" signal right after NoteOn.
        var usedBases = writes.Where(w => w.Offset is 0 or 7 or 14).Select(w => w.Offset).Distinct().OrderBy(b => b);
        Assert.Equal([0, 7, 14], usedBases);
    }

    [Fact]
    public void Resetting_the_filter_takes_every_voice_out_of_it_and_stops_its_program()
    {
        var (engine, writes) = Build();
        var filtered = MakeInstrument(1);
        filtered.Filter.Add(FilterStep.Setup(0x1f, 0xf1, 0x20, sweepRate: 4));
        filtered.Filter.Add(FilterStep.Sweep(0x30));

        engine.NoteOn(Note("C-4"), filtered);
        writes.Clear();

        engine.ResetFilter();

        // Routing and resonance off, volume 15 - the sound bank's next card is heard on its own.
        Assert.Contains((0x17, 0x00), writes);
        Assert.Contains((0x18, 0x0f), writes);

        // The old program and its sweep are gone: frames go by without touching the filter.
        writes.Clear();
        for (int i = 0; i < 8; i++)
            engine.Frame();
        Assert.DoesNotContain(writes, w => w.Offset is >= 0x15 and <= 0x18);
    }

    [Fact]
    public void A_fourth_note_steals_the_oldest_held_voice()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);

        engine.NoteOn(Note("C-4"), instrument); // voice 0, oldest
        engine.NoteOn(Note("E-4"), instrument); // voice 1
        engine.NoteOn(Note("G-4"), instrument); // voice 2
        writes.Clear();

        engine.NoteOn(Note("C-5"), instrument); // no free voice - must steal voice 0

        Assert.Contains(writes, w => w.Offset == 0); // voice 0 retriggered (frequency rewritten)
        Assert.DoesNotContain(writes, w => w.Offset is 7 or 14); // voices 1 and 2 left alone
    }

    [Fact]
    public void Retriggering_an_already_held_note_reuses_its_own_voice_rather_than_stealing_another()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);

        engine.NoteOn(Note("C-4"), instrument); // voice 0
        engine.NoteOn(Note("E-4"), instrument); // voice 1
        writes.Clear();

        engine.NoteOn(Note("C-4"), instrument); // held again - same note, same key repeat

        Assert.Contains(writes, w => w.Offset == 0); // voice 0 again
        Assert.DoesNotContain(writes, w => w.Offset is 7 or 14); // voice 1 and the free voice 2 untouched
    }

    [Fact]
    public void NoteOff_releases_only_the_voice_playing_that_note()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);

        engine.NoteOn(Note("C-4"), instrument); // voice 0
        engine.NoteOn(Note("E-4"), instrument); // voice 1
        engine.Frame(); // let each voice's own implicit single-step program complete before release
        writes.Clear();

        engine.NoteOff(Note("C-4"));

        var write = Assert.Single(writes);
        Assert.Equal(4, write.Offset);                 // voice 0's control register
        Assert.Equal(0, write.Value & 0x01);            // gate cleared
    }

    [Fact]
    public void NoteOff_for_a_note_not_currently_held_does_nothing()
    {
        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), MakeInstrument(1));
        writes.Clear();

        engine.NoteOff(Note("G-6")); // never pressed

        Assert.Empty(writes);
    }

    [Fact]
    public void Frame_runs_the_full_wavetable_program_not_just_its_first_step()
    {
        var instrument = MakeInstrument(1);
        instrument.Wavetable.AddRange([0x81, 0x41, 0x11]); // three distinct control bytes

        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), instrument);
        writes.Clear();

        engine.Frame(); // step 1 of the WAVE program
        var first = writes.Where(w => w.Offset == 4).Select(w => w.Value).ToList();
        writes.Clear();

        engine.Frame(); // step 2 - this is exactly what NotePreviewPlayer.Trigger cannot do
        var second = writes.Where(w => w.Offset == 4).Select(w => w.Value).ToList();

        Assert.Equal([0x81], first);
        Assert.Equal([0x41], second);
    }

    [Fact]
    public void Deferred_NoteOff_holds_off_until_the_wavetable_program_completes()
    {
        var instrument = MakeInstrument(1);
        instrument.Wavetable.AddRange([0x81, 0x41, 0x11]); // three steps, gate stays on throughout

        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), instrument);
        writes.Clear();

        engine.NoteOff(Note("C-4")); // key released immediately - before even step 1 has run
        Assert.Empty(writes); // deferred: no release write yet, nothing else either

        engine.Frame(); // step 1 (0x81) - program still not complete
        Assert.DoesNotContain(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
        writes.Clear();

        engine.Frame(); // step 2 (0x41) - still not complete
        Assert.DoesNotContain(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
        writes.Clear();

        engine.Frame(); // step 3 (0x11) - the program completes on exactly this frame
        var gateWrites = writes.Where(w => w.Offset == 4).Select(w => w.Value).ToList();
        Assert.Equal([0x11, 0x10], gateWrites); // the program's own last step, then the deferred release right after
    }

    [Fact]
    public void Deferred_release_never_fires_on_its_own_while_the_key_stays_held()
    {
        var instrument = MakeInstrument(1);
        instrument.Wavetable.AddRange([0x81, 0x41, 0x11]);

        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), instrument);
        // No NoteOff at all - the key is still held once the program completes.
        engine.Frame();
        engine.Frame();
        writes.Clear();

        engine.Frame(); // program completes on this frame, but nothing ever asked to release

        Assert.DoesNotContain(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
    }

    /// <summary>
    /// Magnificent's KickBass shape: three wavetable steps but four arpeggio steps ending on ROOT.
    /// The falling pitch *is* the drum, and waiting only for the wavetable released the note two
    /// frames before the sweep ever reached its root - on exactly the short taps a drum is played
    /// with.
    /// </summary>
    [Fact]
    public void Deferred_NoteOff_also_waits_for_an_arpeggio_longer_than_the_wavetable()
    {
        var instrument = MakeInstrument(1);
        instrument.Wavetable.AddRange([0x81, 0x41, 0x11]); // three steps - complete after frame 3
        instrument.Arpeggio.AddRange([Note("B-7"), Note("B-7"), Note("D-3"), Note("F#2")]);
        instrument.ArpeggioEnding = ArpeggioEnding.Root;

        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), instrument);
        engine.NoteOff(Note("C-4")); // released before a single frame has run

        for (int frame = 1; frame <= 4; frame++)
        {
            writes.Clear();
            engine.Frame();
            Assert.DoesNotContain(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
        }

        // Frame 5 is where the arpeggio runs past its last step and ROOT writes the row's own note.
        // The release belongs on that frame and no earlier.
        writes.Clear();
        engine.Frame();

        var (lo, hi) = NotePalTable.GetFreq(Note("C-4"));
        Assert.Contains(writes, w => w.Offset == 0 && w.Value == lo);
        Assert.Contains(writes, w => w.Offset == 1 && w.Value == hi);
        Assert.Contains(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
    }

    /// <summary>A looping arpeggio has no last step to settle on, so completion has to mean "one
    /// pass" rather than "the end" - otherwise a key-up on a plain arpeggio would never release and
    /// the voice would hang for good.</summary>
    [Fact]
    public void A_looping_arpeggio_releases_after_one_pass_rather_than_hanging()
    {
        var instrument = MakeInstrument(1);
        instrument.Arpeggio.AddRange([Note("C-4"), Note("E-4")]);
        instrument.ArpeggioEnding = ArpeggioEnding.Loop;

        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), instrument);
        engine.NoteOff(Note("C-4"));

        writes.Clear();
        engine.Frame();
        Assert.DoesNotContain(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0); // mid-pass

        for (int frame = 2; frame <= 8; frame++)
            engine.Frame();

        Assert.Contains(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
    }

    [Fact]
    public void SilenceAll_gates_off_every_held_voice_and_clears_its_slot()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);
        engine.NoteOn(Note("C-4"), instrument); // voice 0
        engine.NoteOn(Note("E-4"), instrument); // voice 1
        writes.Clear();

        engine.SilenceAll();

        // All three control registers written (voice 2 was never triggered, but SilenceAll
        // still touches every slot - IsActive inside VoiceEngine.Silence is what actually
        // guards it, not the caller, so this also proves an idle voice is left alone: only 2
        // gate writes, not 3, even though 3 slots exist).
        var gateWrites = writes.Where(w => w.Offset is 4 or 11 or 18).ToList();
        Assert.Equal(2, gateWrites.Count);
        Assert.All(gateWrites, w => Assert.Equal(0, w.Value & 0x01)); // gate cleared on each
    }

    [Fact]
    public void SilenceAll_frees_every_slot_so_the_same_note_does_not_read_as_still_held()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);
        engine.NoteOn(Note("C-4"), instrument); // voice 0
        engine.SilenceAll();
        writes.Clear();

        engine.NoteOn(Note("C-4"), instrument); // must retrigger, not silently no-op

        Assert.Contains(writes, w => w.Offset == 0); // voice 0's frequency written again
    }

    private const int ScaleNoteFrames = 12;
    private static readonly string[] ScaleNotes = ["C-4", "D-4", "E-4", "F-4", "G-4", "A-4", "B-4", "C-5"];

    /// <summary>What the last pair of frequency writes on any voice says, or -1 before there is one.</summary>
    private static int LastFrequency(List<(int Offset, int Value)> writes)
    {
        int lo = -1, hi = -1;
        foreach (var (offset, value) in writes)
        {
            if (offset is 0 or 7 or 14) lo = value;
            if (offset is 1 or 8 or 15) hi = value;
        }
        return lo < 0 || hi < 0 ? -1 : (hi << 8) | lo;
    }

    private static int Frequency(string note)
    {
        var (lo, hi) = NotePalTable.GetFreq(Note(note));
        return (hi << 8) | lo;
    }

    private static bool GateOn(List<(int Offset, int Value)> writes, int control) =>
        writes.LastOrDefault(w => w.Offset == control).Value % 2 == 1;

    [Fact]
    public void A_scale_plays_Do_to_Do_a_note_at_a_time_with_a_gap_before_each_next_note()
    {
        var (engine, writes) = Build();
        engine.PlayScale(Note("C-4"), MakeInstrument(1));

        for (int step = 0; step < ScaleNotes.Length; step++)
        {
            for (int frame = 0; frame < ScaleNoteFrames; frame++)
            {
                engine.Frame();
                if (frame == 0)
                {
                    Assert.Equal(Frequency(ScaleNotes[step]), LastFrequency(writes));
                    Assert.True(GateOn(writes, 4), $"step {step} should sound");
                }
                if (frame == ScaleNoteFrames - 1)
                {
                    Assert.Equal(Frequency(ScaleNotes[step]), LastFrequency(writes));
                    Assert.False(GateOn(writes, 4), $"step {step} should have let go before the next");
                }
            }
        }
    }

    [Fact]
    public void A_scale_ends_by_itself_after_its_eight_notes()
    {
        var (engine, writes) = Build();
        engine.PlayScale(Note("C-4"), MakeInstrument(1));
        for (int frame = 0; frame < ScaleNotes.Length * ScaleNoteFrames; frame++)
            engine.Frame();

        writes.Clear();
        for (int frame = 0; frame < 200; frame++)
            engine.Frame();

        Assert.DoesNotContain(writes, w => w.Offset is 4 or 11 or 18 && w.Value % 2 == 1);
        Assert.DoesNotContain(writes, w => w.Offset is 0 or 7 or 14 or 1 or 8 or 15);
    }

    [Fact]
    public void A_new_scale_takes_over_from_the_one_playing()
    {
        var (engine, writes) = Build();
        engine.PlayScale(Note("C-4"), MakeInstrument(1));
        for (int frame = 0; frame < ScaleNoteFrames + 3; frame++)
            engine.Frame();

        engine.PlayScale(Note("C-2"), MakeInstrument(2));
        engine.Frame();
        Assert.Equal(Frequency("C-2"), LastFrequency(writes));

        for (int frame = 1; frame < ScaleNoteFrames + 1; frame++)
            engine.Frame();
        Assert.Equal(Frequency("D-2"), LastFrequency(writes));
    }

    [Fact]
    public void Stopping_a_scale_lets_go_of_its_note_and_starts_no_more()
    {
        var (engine, writes) = Build();
        engine.PlayScale(Note("C-4"), MakeInstrument(1));
        for (int frame = 0; frame < 3; frame++)
            engine.Frame();
        Assert.True(GateOn(writes, 4));

        engine.StopScale();
        Assert.False(GateOn(writes, 4));

        writes.Clear();
        for (int frame = 0; frame < 3 * ScaleNoteFrames; frame++)
            engine.Frame();
        Assert.DoesNotContain(writes, w => w.Offset is 4 or 11 or 18 && w.Value % 2 == 1);
    }

    [Fact]
    public void A_key_played_during_a_scale_takes_over()
    {
        var (engine, writes) = Build();
        engine.PlayScale(Note("C-4"), MakeInstrument(1));
        for (int frame = 0; frame < 3; frame++)
            engine.Frame();

        engine.NoteOn(Note("A-3"), MakeInstrument(2));
        for (int frame = 0; frame < 3 * ScaleNoteFrames; frame++)
            engine.Frame();

        Assert.Equal(Frequency("A-3"), LastFrequency(writes));
    }

    /// <summary>Notes in a program that ends on LOOP or HOLD are absolute by design, so these
    /// repeat rather than climb; every other sound of the bank walks up the scale.</summary>
    [Fact]
    public void Every_sound_of_the_bank_climbs_unless_its_notes_are_fixed_and_all_let_go()
    {
        string[] fixedNotes = ["ArpMinor", "ArpMinor2", "ArpMinor3", "ArpMajor", "ArpMajor2", "ArpMajor3", "Kick", "Snare", "Snare2", "Snare3"];
        foreach (var sound in MagSid.Core.Midi.GmBank.BuiltIn.All)
        {
            var (engine, writes) = Build();
            engine.PlayScale(Note("C-4"), sound.Instrument);

            var heard = new List<int>();
            for (int frame = 0; frame < ScaleNotes.Length * ScaleNoteFrames; frame++)
            {
                engine.Frame();
                if (frame % ScaleNoteFrames == 8)
                    heard.Add(LastFrequency(writes));
            }

            if (fixedNotes.Contains(sound.Key))
                Assert.True(heard.Distinct().Count() == 1, $"{sound.Key}: {string.Join(' ', heard)}");
            else
                Assert.True(heard.Zip(heard.Skip(1)).All(p => p.Second > p.First), $"{sound.Key}: {string.Join(' ', heard)}");

            for (int frame = 0; frame < 100; frame++)
                engine.Frame();
            foreach (int control in new[] { 4, 11, 18 })
                Assert.False(GateOn(writes, control), $"{sound.Key} still holds voice register {control}");
        }
    }

    /// <summary>The real chip, not the register log: the pitch heard rises step by step.</summary>
    [Fact]
    public void The_scale_rises_in_the_rendered_sound()
    {
        const int samplesPerStep = 44100 * ScaleNoteFrames / 50;
        using var engine = new JamEngine();
        engine.PlayScale(Note("C-4"), MakeInstrument(1, waveform: 0x11, ad: 0x00, sr: 0xf0));

        var crossings = new List<int>();
        var buffer = new byte[samplesPerStep * 2];
        for (int step = 0; step < ScaleNotes.Length; step++)
        {
            Assert.Equal(buffer.Length, engine.Read(buffer, 0, buffer.Length));
            var samples = new short[samplesPerStep];
            Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);

            // The middle of the note: past the frame the step began on, before its release.
            double mean = samples.Skip(samplesPerStep / 4).Take(samplesPerStep / 2).Average(v => (double)v);
            int count = 0;
            for (int i = samplesPerStep / 4 + 1; i < samplesPerStep * 3 / 4; i++)
                if (samples[i - 1] < mean && samples[i] >= mean)
                    count++;
            crossings.Add(count);
        }

        Assert.True(crossings.Zip(crossings.Skip(1)).All(p => p.Second > p.First), string.Join(' ', crossings));
    }
}
