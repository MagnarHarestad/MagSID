using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MagSid.Core.Model;
using MagSid.Editor.App;
using MagSid.Playback;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The instrument editor under the list, as the Windows editor's InstrumentEditorPanel: four
/// small captioned groups - envelope, pulse, hard restart, vibrato - that wrap a group
/// at a time when the column is narrow; one grid of the programs, a row a frame, with the
/// wavetable (Wave), the note program (Note, and its byte as Hex) and the filter program (Filter,
/// Mode/Vol, Res+Voice, Cutoff, Sweep) side by side, since all three take a step each frame from
/// the moment a note starts; and what happens after the last note.
///
/// A row's Filter can be blank - no filter action that frame - and the filter program is made of
/// the rows where it is not, in order. A blank Wave ends the wavetable there, and a blank or ROOT
/// Note ends the note program. Right-click a row to add a frame, remove it or move it up or down;
/// the whole row moves, every program's step with it.
///
/// Every field applies the moment it is left, through one validation that reads everything first
/// and only then writes, so a mistake never leaves the instrument half changed. While the song
/// plays, each program's column lights the steps it has played and where it is now.
/// </summary>
internal sealed class InstrumentEditorView : DockPanel
{
    private static readonly FontFamily Mono = EditorTheme.MonoFont;
    private static readonly IBrush Label = EditorTheme.Subtitle;
    private static readonly IBrush Body = EditorTheme.Body;
    private static readonly IBrush Heading = EditorTheme.Heading;
    private static readonly IBrush Warning = new SolidColorBrush(AvColor.FromRgb(0xFF, 0xB0, 0x5C));
    private static readonly IBrush FrameBrush = EditorTheme.Border;
    private static readonly IBrush HeaderBackground = EditorTheme.Button;
    private static readonly IBrush MonoInk = EditorTheme.Mono;

    // Over the field background: 10% for a step this playback has been through, brighter for
    // where the program is right now - the dim trail is what shows an unused tail once the song
    // has run, the bright cell is what you watch move.
    private static readonly AvColor FieldColour = AvColor.FromRgb(0x1C, 0x1C, 0x30);
    private static readonly IBrush FieldBackground = new SolidColorBrush(FieldColour);
    private static readonly IBrush ReachedBackground = new SolidColorBrush(Lighten(FieldColour, 0.10));
    private static readonly IBrush PlayheadBackground = new SolidColorBrush(Lighten(FieldColour, 0.34));

    private const string RootLabel = "ROOT";
    private static readonly string[] NoteChoices = [RootLabel, .. NoteName.AllNotes.Select(n => n.ToString()), ""];
    private static readonly string[] FilterTypes = ["", "Sweep", "Setup"];

    // The program grid's columns, as 1.64's sized themselves to their headings; Sweep takes what
    // is left, so the table ends at its last cell.
    private static readonly string[] Headings = ["#", "Wave", "Note", "Hex", "Filter", "Mode/Vol", "Res+Voice", "Cutoff", "Sweep"];
    private const string ColumnWidths = "34,50,70,46,74,72,80,60,*";
    private const double CellHeight = 22;

    /// <summary>What a freshly enabled Vibrator starts from: delay 2 (8 frames), speed 4, depth
    /// $20 - slow and wide enough to hear on a lead without needing to be tuned first.</summary>
    private const byte DefaultVibratoDelaySpeed = 0x24;
    private const byte DefaultVibratoDepth = 0x20;

    private readonly TextBox _attackDecay = Hex();
    private readonly TextBox _sustainRelease = Hex();
    // Hard restart is one choice of three, so it is one control of three segments: On, Soft, Off.
    private readonly ToggleButton _fullHardRestart = Segment("On", "Before the next note: the envelope is reset and the oscillator stopped - a clean, hard attack");
    private readonly ToggleButton _softHardRestart = Segment("Soft", "Before the next note: the envelope is reset but the waveform kept - no click on a cut note");
    private readonly ToggleButton _noHardRestart = Segment("Off", "No hard restart: the next note follows on, legato");
    private readonly NumericUpDown _pulseWidth = new()
    {
        Minimum = 0, Maximum = 15, Width = 46, FormatString = "0", FontFamily = Mono, Foreground = MonoInk,
        Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBox _pulseSweep = Hex();
    private readonly ToggleButton _pulseContinuous = Switch("Cont.", "Continuous: the pulse sweep carries on from note to note instead of starting again");
    private readonly ToggleButton _vibrator = Switch("Vibrator", "Vibrato on or off");
    private readonly TextBox _vibDelaySpeed = Hex();
    private readonly TextBox _vibDepth = Hex();

    private readonly StackPanel _rows = new();
    private readonly ContextMenu _rowMenu = new();
    private readonly List<ProgramRow> _program = [];
    private ProgramRow? _currentRow;

    private readonly ComboBox _ending = new()
    {
        ItemsSource = new[]
        {
            "Loop - cycle the notes for as long as the note is held",
            "Hold - stay on the last note",
            "Root - settle on the row's own note",
        },
        Width = 236,
    };
    private readonly TextBlock _loopLabel = Caption("from step");
    private readonly NumericUpDown _loopFrom = new() { Minimum = 0, Maximum = 63, Width = 60, FormatString = "0", FontFamily = Mono, Foreground = MonoInk };
    private readonly TextBlock _problem = new() { Foreground = Warning, TextWrapping = TextWrapping.Wrap };

    private Instrument? _instrument;

    /// <summary>The fields being filled from an instrument: the changes that makes are not edits.</summary>
    private bool _loading;
    private bool _syncing;

    private ProgramCoverage? _coverage;
    private int _playheadWave = -1;
    private int _playheadArp = -1;
    private int _playheadFilter = -1;
    private int _coverageVersion = -1;

    public InstrumentEditorView()
    {
        // 1.64's panel: the groups along the top, "after the last note" pinned to the bottom, and
        // the program table taking all the height between them.
        Margin = new Thickness(12);

        var groups = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 4, 4, 2),
            Children =
            {
                Group("ENVELOPE", Caption("A/D"), _attackDecay, Caption("S/R"), _sustainRelease),
                Group("PULSE", Caption("Width"), _pulseWidth, Caption("Sweep"), _pulseSweep, _pulseContinuous),
                Group("HARD RESTART", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(3, 0, 0, 0),
                    Children = { _fullHardRestart, _softHardRestart, _noHardRestart },
                }),
                Group("VIBRATO", _vibrator, Caption("D/S"), _vibDelaySpeed, Caption("Depth"), _vibDepth),
            },
        };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(ColumnWidths), Background = HeaderBackground, Height = CellHeight };
        for (int column = 0; column < Headings.Length; column++)
        {
            var cell = new Border
            {
                BorderBrush = FrameBrush,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock { Text = Headings[column], Foreground = Heading, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) },
            };
            Grid.SetColumn(cell, column);
            header.Children.Add(cell);
        }

        var menu = _rowMenu;
        menu.Opening += (_, _) => menu.ItemsSource = new Control[]
        {
            Entry("Add frame", () =>
            {
                AddRow("41", "", "", null);
                Commit();
            }, true),
            Entry("Remove", () =>
            {
                if (_currentRow is { } row)
                    RemoveRow(row);
                Commit();
            }, _currentRow is not null),
            Entry("Up", () => { MoveCurrentRow(-1); Commit(); }, _currentRow is not null),
            Entry("Down", () => { MoveCurrentRow(+1); Commit(); }, _currentRow is not null),
        };

        var table = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _rows,
        });
        var grid = new Border
        {
            BorderBrush = FrameBrush,
            BorderThickness = new Thickness(1),
            Background = FieldBackground,
            ContextMenu = menu,
            Child = table,
        };

        var ending = Row(Caption("After the last note:"), _ending, _loopLabel, _loopFrom);
        ending.Margin = new Thickness(0, 6, 0, 0);

        SetDock(groups, Dock.Top);
        SetDock(_problem, Dock.Bottom);
        SetDock(ending, Dock.Bottom);
        Children.Add(groups);
        Children.Add(_problem);
        Children.Add(ending);
        Children.Add(grid);

        foreach (var box in new[] { _attackDecay, _sustainRelease, _pulseSweep, _vibDelaySpeed, _vibDepth })
            box.LostFocus += (_, _) => Commit();
        _pulseContinuous.IsCheckedChanged += (_, _) => Commit();
        foreach (var segment in new[] { _fullHardRestart, _softHardRestart, _noHardRestart })
        {
            // A segment stays down when clicked again: one of the three is always the choice.
            segment.Click += (_, _) =>
            {
                ShowHardRestart(segment == _noHardRestart, segment == _softHardRestart);
                Commit();
            };
        }
        _vibrator.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
                return;

            // Switching it on over a blank or zero depth would commit "no vibrato" straight
            // back off, so the fields are seeded first - the button would otherwise seem dead.
            if (_vibrator.IsChecked == true && (!TryHex(_vibDepth.Text, out byte depth) || depth == 0))
            {
                _vibDelaySpeed.Text = DefaultVibratoDelaySpeed.ToString("x2");
                _vibDepth.Text = DefaultVibratoDepth.ToString("x2");
            }

            UpdateVibratoEnabled();
            Commit();
        };
        _pulseWidth.ValueChanged += (_, _) => Commit();
        _loopFrom.ValueChanged += (_, _) => Commit();
        _ending.SelectionChanged += (_, _) =>
        {
            UpdateLoopFromVisibility();
            Commit();
        };

        Load(null);
    }

    /// <summary>The instrument changed - the song is dirty and the list's names may differ.</summary>
    public event Action? Edited;

    public void Load(Instrument? instrument)
    {
        _instrument = instrument;
        _loading = true;
        try
        {
            IsEnabled = instrument is not null;
            _problem.Text = "";
            ClearRows();
            if (instrument is null)
                return;

            _attackDecay.Text = instrument.AttackDecay.ToString("x2");
            _sustainRelease.Text = instrument.SustainRelease.ToString("x2");
            ShowHardRestart(instrument.NoHardRestart, instrument.SoftHardRestart);
            _pulseWidth.Value = Math.Clamp(instrument.PulseWidth, 0, 15);
            // The raw byte, GoatTracker style, as A/D and S/R are: PulseSweep is a signed byte.
            _pulseSweep.Text = unchecked((byte)Math.Clamp(instrument.PulseSweep, -128, 127)).ToString("x2");
            _pulseContinuous.IsChecked = instrument.PulseContinuous;
            _vibrator.IsChecked = instrument.HasVibrato;
            _vibDelaySpeed.Text = (instrument.HasVibrato ? instrument.VibratoDelaySpeed : DefaultVibratoDelaySpeed).ToString("x2");
            _vibDepth.Text = (instrument.HasVibrato ? instrument.VibratoDepth : DefaultVibratoDepth).ToString("x2");
            UpdateVibratoEnabled();

            _ending.SelectedIndex = (int)instrument.ArpeggioEnding;
            _loopFrom.Value = Math.Clamp(instrument.ArpeggioLoopPoint, 0, 63);
            UpdateLoopFromVisibility();

            // Row 0's Wave is where the waveform is typed; shown as stored, not gate-forced.
            var waves = instrument.Wavetable.Count > 0
                ? instrument.Wavetable.Select(b => b.ToString("x2")).ToList()
                : [instrument.Waveform.ToString("x2")];
            var notes = instrument.Arpeggio.Select(n => n.ToString()).ToList();
            var hexes = instrument.Arpeggio.Select(HexText).ToList();

            // As far as whichever program is longest, so every step has a row to show it in.
            int rows = Math.Max(Math.Max(waves.Count, notes.Count), instrument.Filter.Count);
            for (int i = 0; i < rows; i++)
            {
                AddRow(
                    i < waves.Count ? waves[i] : "",
                    i < notes.Count ? notes[i] : "",
                    i < hexes.Count ? hexes[i] : "",
                    i < instrument.Filter.Count ? instrument.Filter[i] : null);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Show High Hex-Values was switched: the Hex and Mode/Vol columns are redrawn in
    /// the form now chosen. Both forms can be typed.</summary>
    public void RefreshHighHexDisplay()
    {
        _syncing = true;
        try
        {
            foreach (var row in _program)
            {
                row.Hex.Text = Shown(row.Hex.Text);
                row.Mode.Text = Shown(row.Mode.Text);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// The trail of steps already played, and where the programs are at the instant coming out of
    /// the speakers. The steps that stay dark are the point: a wavetable can carry eight steps and
    /// the song only play three, and the other five are bytes in the .sid doing nothing. Null when
    /// playback stops.
    /// </summary>
    public void SetPlaybackCoverage(ProgramCoverage? coverage, int waveStep = -1, int arpStep = -1, int filterStep = -1)
    {
        // This runs on every poll, while the tints change at most once a frame: nothing is
        // repainted unless the playhead, the trail or the instrument really moved.
        int version = coverage?.Version ?? -1;
        if (ReferenceEquals(_coverage, coverage) && waveStep == _playheadWave && arpStep == _playheadArp
            && filterStep == _playheadFilter && version == _coverageVersion)
            return;

        _coverage = coverage;
        _playheadWave = waveStep;
        _playheadArp = arpStep;
        _playheadFilter = filterStep;
        _coverageVersion = version;

        for (int i = 0; i < _program.Count; i++)
        {
            var row = _program[i];
            row.Wave.Background = Tint(i, _playheadWave, coverage?.Wavetable);
            row.Note.Background = Tint(i, _playheadArp, coverage?.Arpeggio);
            row.Type.Background = Tint(i, _playheadFilter, coverage?.Filter);
        }
    }

    private static IBrush Tint(int step, int playhead, bool[]? reached) =>
        reached is null ? FieldBackground
        : step == playhead ? PlayheadBackground
        : step < ProgramCoverage.MaxSteps && reached[step] ? ReachedBackground
        : FieldBackground;

    // ---------------------------------------------------------------- the program grid

    private sealed class ProgramRow
    {
        public required Grid Panel { get; init; }
        public required TextBlock Step { get; init; }
        public required TextBox Wave { get; init; }
        public required ComboBox Note { get; init; }
        public required TextBox Hex { get; init; }
        public required ComboBox Type { get; init; }
        public required TextBox Mode { get; init; }
        public required TextBox Res { get; init; }
        public required TextBox Cutoff { get; init; }
        public required TextBox Sweep { get; init; }

        public string NoteText => Note.SelectedItem as string ?? "";
        public string TypeText => Type.SelectedItem as string ?? "";
    }

    private void AddRow(string wave, string note, string hex, FilterStep? filter, int at = -1)
    {
        var row = new ProgramRow
        {
            Panel = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(ColumnWidths), Height = CellHeight },
            Step = new TextBlock { Foreground = MonoInk, FontFamily = Mono, FontSize = EditorTheme.MonoSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) },
            Wave = Cell(),
            Note = Choice(NoteChoices),
            Hex = Cell(),
            Type = Choice(FilterTypes),
            Mode = Cell(),
            Res = Cell(),
            Cutoff = Cell(),
            Sweep = Cell(),
        };

        // Each cell in a frame of grid lines, as a DataGridView draws them.
        Control[] cells = [row.Step, row.Wave, row.Note, row.Hex, row.Type, row.Mode, row.Res, row.Cutoff, row.Sweep];
        for (int column = 0; column < cells.Length; column++)
        {
            var frame = new Border { BorderBrush = FrameBrush, BorderThickness = new Thickness(0, 0, 1, 1), Child = cells[column] };
            Grid.SetColumn(frame, column);
            row.Panel.Children.Add(frame);
        }

        bool loading = _loading;
        _loading = true;
        try
        {
            row.Wave.Text = wave;
            row.Note.SelectedItem = note;
            row.Hex.Text = Shown(hex);
            row.Type.SelectedItem = filter is { IsSetup: true } ? "Setup" : filter is not null ? "Sweep" : "";
            row.Mode.Text = Shown(filter?.ModeVolume?.ToString("x2") ?? "");
            row.Res.Text = filter?.ResonanceRouting?.ToString("x2") ?? "";
            row.Cutoff.Text = filter is { } step ? step.Cutoff.ToString("x2") : "";
            row.Sweep.Text = filter is { IsSetup: true } setup ? setup.SweepRate.ToString(CultureInfo.InvariantCulture) : "";
            ApplyFilterType(row);
        }
        finally
        {
            _loading = loading;
        }

        foreach (var control in new Control[] { row.Wave, row.Note, row.Hex, row.Type, row.Mode, row.Res, row.Cutoff, row.Sweep })
            control.GotFocus += (_, _) => _currentRow = row;

        // The row's own menu on every cell, in place of a text box's cut and paste: a frame is
        // what a right-click here acts on.
        foreach (var control in new Control[] { row.Step, row.Wave, row.Note, row.Hex, row.Type, row.Mode, row.Res, row.Cutoff, row.Sweep })
        {
            control.ContextFlyout = null;
            control.ContextMenu = _rowMenu;
        }

        // A right-click picks the row under it, so Remove and the moves act on what was clicked.
        row.Panel.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(row.Panel).Properties.IsRightButtonPressed)
                _currentRow = row;
        }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        foreach (var box in new[] { row.Wave, row.Res, row.Cutoff, row.Sweep })
            box.LostFocus += (_, _) => Commit();
        row.Hex.LostFocus += (_, _) =>
        {
            if (!_syncing)
                SyncNoteFromHex(row);
            Commit();
        };
        row.Mode.LostFocus += (_, _) => Commit();
        row.Note.SelectionChanged += (_, _) =>
        {
            if (_loading || _syncing)
                return;
            SyncHexFromNote(row);
            Commit();
        };
        row.Type.SelectionChanged += (_, _) =>
        {
            if (_loading)
                return;
            ApplyFilterType(row);
            Commit();
        };

        if (at < 0 || at > _program.Count)
            at = _program.Count;
        _program.Insert(at, row);
        _rows.Children.Insert(at, row.Panel);
        Renumber();
    }

    private void RemoveRow(ProgramRow row)
    {
        _program.Remove(row);
        _rows.Children.Remove(row.Panel);
        if (_currentRow == row)
            _currentRow = null;
        Renumber();
    }

    private void ClearRows()
    {
        _program.Clear();
        _rows.Children.Clear();
        _currentRow = null;
    }

    private void MoveCurrentRow(int delta)
    {
        if (_currentRow is not { } row)
            return;

        int from = _program.IndexOf(row), to = from + delta;
        if (from < 0 || to < 0 || to >= _program.Count)
            return;

        _program.RemoveAt(from);
        _rows.Children.RemoveAt(from);
        _program.Insert(to, row);
        _rows.Children.Insert(to, row.Panel);
        Renumber();
        row.Wave.Focus();
    }

    private void Renumber()
    {
        for (int i = 0; i < _program.Count; i++)
            _program[i].Step.Text = i.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Cutoff needs a filter step of either kind; Mode/Vol, Res+Voice and Sweep need a
    /// setup. A blank Filter - no filter action this frame - blanks and locks all four.</summary>
    private static void ApplyFilterType(ProgramRow row)
    {
        bool setup = row.TypeText == "Setup";
        bool step = row.TypeText.Length > 0;

        row.Cutoff.IsEnabled = step;
        if (!step)
            row.Cutoff.Text = "";
        else if (string.IsNullOrWhiteSpace(row.Cutoff.Text))
            row.Cutoff.Text = "08";

        foreach (var (cell, fresh) in new[] { (row.Mode, Shown("1f")), (row.Res, "f1"), (row.Sweep, "0") })
        {
            cell.IsEnabled = setup;
            if (!setup)
                cell.Text = "";
            else if (string.IsNullOrWhiteSpace(cell.Text))
                cell.Text = fresh;
        }
    }

    private void SyncHexFromNote(ProgramRow row)
    {
        _syncing = true;
        row.Hex.Text = NoteName.TryParse(row.NoteText, out var note) ? Shown(HexText(note)) : "";
        _syncing = false;
    }

    private void SyncNoteFromHex(ProgramRow row)
    {
        string text = (row.Hex.Text ?? "").Trim().TrimStart('$');
        if (text.Length == 0)
            return;

        if (!TryHex(text, out byte raw))
        {
            _problem.Text = $"'{text}' is not a hex value.";
            return;
        }

        int index = raw & 0x7F;
        if (index > 0x5F)
        {
            _problem.Text = $"${raw:x2} does not correspond to a note (valid: $00..$5f or $80..$df).";
            return;
        }

        var note = NoteName.AllNotes[index];
        _syncing = true;
        row.Note.SelectedItem = note.ToString();
        row.Hex.Text = Shown(HexText(note));
        _syncing = false;
    }

    private static string HexText(NoteName note) => note.TableIndex.ToString("x2");

    /// <summary>A byte of the Hex or Mode/Vol column as it is shown: with its high bit set when
    /// Show High Hex-Values is on - GoatTracker's form, $80+ notes and 9x filter modes.</summary>
    private static string Shown(string? text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || !TryHex(trimmed, out byte raw))
            return text ?? "";
        return (EditorSettings.ShowHighHex ? raw | 0x80 : raw & 0x7F).ToString("x2");
    }

    // ---------------------------------------------------------------- commit

    private void Commit()
    {
        if (_loading || _syncing || _instrument is not { } instrument)
            return;

        _problem.Text = "";
        try
        {
            byte attackDecay = ReadHex(_attackDecay.Text, "Attack/Decay");
            byte sustainRelease = ReadHex(_sustainRelease.Text, "Sustain/Release");
            byte sweep = ReadHex(_pulseSweep.Text, "Pulse Sweep");
            bool vibrato = _vibrator.IsChecked == true;
            byte delaySpeed = vibrato ? ReadHex(_vibDelaySpeed.Text, "Vibrato delay/speed") : instrument.VibratoDelaySpeed;
            byte depth = vibrato ? ReadHex(_vibDepth.Text, "Vibrato depth") : (byte)0;
            if (vibrato && depth == 0)
                throw new FormatException("Vibrato depth must be 01-ff - 00 would mean no vibrato; switch Vibrator off instead.");

            var (waveform, waves, notes, ending) = ReadWavetable(instrument);
            var filter = ReadFilter();

            instrument.Waveform = waveform;
            instrument.AttackDecay = attackDecay;
            instrument.SustainRelease = sustainRelease;
            // Off keeps the instrument's Soft as it was: with no restart it changes nothing, and
            // the file is left as written.
            instrument.NoHardRestart = _noHardRestart.IsChecked == true;
            if (instrument.NoHardRestart == false)
                instrument.SoftHardRestart = _softHardRestart.IsChecked == true;
            instrument.PulseWidth = (int)(_pulseWidth.Value ?? 0);
            instrument.PulseSweep = unchecked((sbyte)sweep);
            instrument.PulseContinuous = _pulseContinuous.IsChecked == true;

            // Switched off keeps the delay/speed the instrument last had, so switching Vibrator
            // back on restores it; only the depth - the on/off in the file and the player - goes.
            if (vibrato)
                instrument.VibratoDelaySpeed = delaySpeed;
            instrument.VibratoDepth = depth;

            instrument.Wavetable.Clear();
            instrument.Wavetable.AddRange(waves);
            instrument.Arpeggio.Clear();
            instrument.Arpeggio.AddRange(notes);
            instrument.ArpeggioEnding = ending;
            instrument.ArpeggioLoopPoint = ending == ArpeggioEnding.Loop ? (int)(_loopFrom.Value ?? 0) : 0;
            instrument.Filter.Clear();
            instrument.Filter.AddRange(filter);

            Edited?.Invoke();
        }
        catch (FormatException ex)
        {
            _problem.Text = ex.Message;
        }
    }

    private (byte Waveform, List<byte> Waves, List<NoteName> Notes, ArpeggioEnding Ending) ReadWavetable(Instrument instrument)
    {
        // Unchanged unless row 0 gives a new one: an entirely blank grid means "as it was".
        byte waveform = instrument.Waveform;
        var waves = new List<byte>();
        var notes = new List<NoteName>();
        var ending = (ArpeggioEnding)Math.Max(0, _ending.SelectedIndex);
        bool wavesEnded = false, notesEnded = false;

        for (int i = 0; i < _program.Count; i++)
        {
            var row = _program[i];
            int frame = i + 1;

            string wave = (row.Wave.Text ?? "").Trim();
            if (wave.Length == 0)
                wavesEnded = true;
            else if (wavesEnded)
                throw new FormatException($"Frame {frame}: the waveform column already ended on an earlier frame, so this value would never be reached.");
            else
            {
                byte value = ReadHex(wave, $"Frame {frame} waveform");
                if (value == 0)
                    throw new FormatException($"Frame {frame}: $00 is what ends a wavetable, so it cannot also be a step. Leave the cell blank instead.");
                waves.Add(value);
            }

            string note = row.NoteText.Trim();
            if (note.Length == 0 || note == RootLabel)
            {
                if (note == RootLabel)
                    ending = ArpeggioEnding.Root;
                notesEnded = true;
            }
            else if (notesEnded)
                throw new FormatException($"Frame {frame}: the note column already ended on an earlier frame, so this note would never be reached.");
            else if (!NoteName.TryParse(note, out var parsed))
                throw new FormatException($"Frame {frame}: '{note}' is not a note.");
            else
                notes.Add(parsed);
        }

        if (ending == ArpeggioEnding.Hold && notes.Count == 0)
            throw new FormatException("\"Hold\" stays on the last note, so the note column needs at least one.");

        int loopFrom = (int)(_loopFrom.Value ?? 0);
        if (ending == ArpeggioEnding.Loop && loopFrom >= Math.Max(1, notes.Count))
            throw new FormatException($"The loop goes back to step {loopFrom}, so there has to be a note there. Step numbering starts at 0.");

        // One step is the plain waveform: it compiles to the same pooled program as no WAVE
        // section at all, so it is never stored as a one-entry wavetable.
        if (waves.Count == 1)
        {
            waveform = waves[0];
            waves.Clear();
        }

        return (waveform, waves, notes, ending);
    }

    /// <summary>The filter program: the rows whose Filter is not blank, in order.</summary>
    private List<FilterStep> ReadFilter()
    {
        var filter = new List<FilterStep>();
        for (int i = 0; i < _program.Count; i++)
        {
            var row = _program[i];
            if (row.TypeText.Length == 0)
                continue;

            int frame = i + 1;
            byte cutoff = ReadHex(row.Cutoff.Text, $"Frame {frame} filter cutoff");
            if (cutoff is < FilterStep.MinCutoff or > FilterStep.MaxCutoff)
                throw new FormatException($"Frame {frame} filter: cutoff must be ${FilterStep.MinCutoff:x2}-${FilterStep.MaxCutoff:x2}. " +
                                          "$00 ends a program and $ff marks a setup step, so neither can also mean a cutoff.");

            if (row.TypeText != "Setup")
            {
                filter.Add(FilterStep.Sweep(cutoff));
                continue;
            }

            // Mode/Vol may be typed in either form; the player's byte has the high bit clear.
            byte mode = (byte)(ReadHex(row.Mode.Text, $"Frame {frame} filter mode+volume") & 0x7F);
            byte resonance = ReadHex(row.Res.Text, $"Frame {frame} filter resonance+routing");
            string rateText = (row.Sweep.Text ?? "").Trim();
            if (rateText.Length == 0)
                rateText = "0";
            if (!int.TryParse(rateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate) || rate is < -128 or > 127)
                throw new FormatException($"Frame {frame} filter: the sweep rate is a signed number, -128 to 127. 0 stops a sweep that is already running.");

            filter.Add(FilterStep.Setup(mode, resonance, cutoff, rate));
        }

        return filter;
    }

    // ---------------------------------------------------------------- small helpers

    private void UpdateVibratoEnabled()
    {
        bool on = _vibrator.IsChecked == true;
        _vibDelaySpeed.IsEnabled = on;
        _vibDepth.IsEnabled = on;
    }

    private void UpdateLoopFromVisibility()
    {
        bool loops = _ending.SelectedIndex == (int)ArpeggioEnding.Loop;
        _loopLabel.IsVisible = _loopFrom.IsVisible = loops;
    }

    private static bool TryHex(string? text, out byte value) =>
        byte.TryParse((text ?? "").Trim().TrimStart('$'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static byte ReadHex(string? text, string what) =>
        TryHex(text, out byte value) ? value : throw new FormatException($"{what} must be a hex byte, 00-ff.");

    private static AvColor Lighten(AvColor colour, double amount) => AvColor.FromRgb(
        (byte)(colour.R + (255 - colour.R) * amount),
        (byte)(colour.G + (255 - colour.G) * amount),
        (byte)(colour.B + (255 - colour.B) * amount));

    private static MenuItem Entry(string header, Action action, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static TextBox Hex() => new()
    {
        Width = 28,
        Height = 20,
        Padding = new Thickness(0),
        FontFamily = Mono,
        FontSize = EditorTheme.MonoSize,
        Foreground = MonoInk,
        MaxLength = 2,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(2, 0),
    };

    /// <summary>A text cell of the program table: the grid line is its frame's, not its own.</summary>
    private static TextBox Cell() => new()
    {
        MinHeight = 0,
        Padding = new Thickness(4, 0),
        FontFamily = Mono,
        FontSize = EditorTheme.MonoSize,
        Foreground = MonoInk,
        Background = FieldBackground,
        BorderThickness = new Thickness(0),
        Classes = { "cell" },
    };

    private static ComboBox Choice(string[] items) => new()
    {
        ItemsSource = items,
        MinHeight = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Padding = new Thickness(4, 0, 0, 0),
        FontFamily = Mono,
        FontSize = EditorTheme.MonoSize,
        Foreground = MonoInk,
        Background = FieldBackground,
        BorderThickness = new Thickness(0),
        Classes = { "cell" },
    };

    /// <summary>An on/off setting: a light and its name, lit and bright when on, a dim ring and
    /// grey when off - so off reads as off, not as a button waiting to be pressed.</summary>
    private static ToggleButton Switch(string text, string tip)
    {
        var toggle = new ToggleButton
        {
            Margin = new Thickness(4, 0, 0, 0),
            Classes = { "switch" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new global::Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(toggle, tip);
        return toggle;
    }

    /// <summary>One of the hard restart's three segments, drawn joined to its neighbours.</summary>
    private static ToggleButton Segment(string text, string tip)
    {
        var segment = new ToggleButton
        {
            Content = text,
            Padding = new Thickness(8, 1),
            Margin = new Thickness(0, 0, -1, 0),
            Classes = { "segment" },
        };
        ToolTip.SetTip(segment, tip);
        return segment;
    }

    /// <summary>Puts the hard restart's choice on its segments: Off wins, as it does in the player.</summary>
    private void ShowHardRestart(bool off, bool soft)
    {
        _noHardRestart.IsChecked = off;
        _softHardRestart.IsChecked = !off && soft;
        _fullHardRestart.IsChecked = !off && !soft;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = Body,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(3, 0, 0, 0),
    };

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }

    /// <summary>A caption over a row of fields, in a thin frame - a group without a heavy border.</summary>
    private static Control Group(string caption, params Control[] fields) => new Border
    {
        BorderBrush = FrameBrush,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(4, 3, 6, 4),
        Margin = new Thickness(0, 0, 8, 4),
        Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = caption, Foreground = Label, FontSize = 9, FontWeight = FontWeight.Bold, Margin = new Thickness(3, 0, 0, 1) },
                Row(fields),
            },
        },
    };
}
