using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Core.SidImport;
using MagSid.Core.Validation;
using MagSid.Editor.App;
using MagSid.Editor.App.Controls;
using MagSid.Editor.App.Daw;
using MagSid.Editor.App.Midi;
using MagSid.Editor.Avalonia.Player;
using MagSid.Midi;
using MagSid.Playback;
using MagSid.Sid;
using AvColor = Avalonia.Media.Color;
using WinKeys = System.Windows.Forms.Keys;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The editor's window on every system: the Retro view - the instrument list and the tracker grid -
/// and the DAW view, with the song's file, history and playback around them. The grid and the DAW
/// are the Windows editor's own TrackerGridControl and DawView, compiled against the WinForms face
/// in MagSid.Editor.Views; what is here is the part MainForm does there, rebuilt in Avalonia:
/// menus, transport, and wiring the views' events to the session and the engines.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly IBrush WindowBackground = new SolidColorBrush(AvColor.FromRgb(0x10, 0x10, 0x20));
    private static readonly IBrush Body = new SolidColorBrush(AvColor.FromRgb(0xD2, 0xD2, 0xDE));
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    private static readonly FontFamily Mono = new("Consolas, Menlo, DejaVu Sans Mono, monospace");

    private readonly EditorSession _session = new();
    private readonly AutosaveManager _autosave;
    private readonly TrackerGridControl _grid = new();
    private readonly DawView _daw = new();
    private readonly InstrumentEditorView _instrumentEditor = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _songLength = new();
    private readonly TextBlock _editMode = new();
    private readonly Button _play = new();
    private readonly Button _chip = new();
    // A fallback: while playback runs, the window drains events and repaints once per displayed
    // frame (see OnPlaybackFrame). This keeps the drain going when no frames are drawn - minimised.
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>Whether a frame callback is already asked for - one chain at a time.</summary>
    private bool _framesRequested;

    // The 8580 to begin with, as in 1.64; a pick of the other is remembered.
    private ChipModel _chipModel = EditorSettings.Chip6581 ? ChipModel.Mos6581 : ChipModel.Mos8580;
    private SongPlaybackEngine? _engine;
    private IAudioOutput? _output;
    private IAudioOutput? _previewOutput;
    private SongPlaybackEngine? _previewEngine;
    private JamEngine? _jam;

    /// <summary>A jam engine is being opened in the background - see PrepareJam.</summary>
    private bool _jamOpening;
    private bool _closed;
    private SongSequencer.SequencerPosition? _resumeFrom;
    private (int Position, int Row)? _resumeCursor;
    private bool _refreshing;
    private Control _retroView = null!;
    private MenuItem _viewToggle = null!;
    private bool _dawShowing;
    private readonly SignalPathView _signalPath = new();
    private readonly DigiToSidOscView _digi;
    private Control _signalPathHost = null!;
    private Control _digiHost = null!;

    /// <summary>Signal Path or Digi to SID-osc when one is in the centre, over the song's view.</summary>
    private Control? _toolShowing;
    private MenuItem _midiMenu = null!;
    private MidiInput? _midi;
    private MidiKnobBindings? _midiBindings;

    /// <summary>The piano keys held down in the DAW, so a key's repeat does not retrigger it and its
    /// release finds the note it started.</summary>
    private readonly Dictionary<WinKeys, NoteName> _dawHeldKeys = [];

    /// <summary>The take's punch-in and punch-out, in song frames: notes are written between them.</summary>
    private (int In, int Out) _recordWindow = (0, int.MaxValue);

    /// <summary>The song frame heard at the last poll - a step back means Cycle jumped.</summary>
    private int _lastHeardFrame;

    public MainWindow(string? songPath = null)
    {
        Title = $"MagSID Editor {EditorVersion}";

        // As 1.64: exactly as wide as the instrument column, the splitter and the grid, so there
        // is no margin either side until the window is made wider.
        Width = InstrumentColumnWidth + SplitterWidth + TrackerGridControl.ContentWidth + 1;
        Height = 941;
        MinWidth = 900;
        MinHeight = 520;
        Background = WindowBackground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _digi = new DigiToSidOscView(_session);
        Content = BuildLayout();
        _autosave = new AutosaveManager(_session);
        WireEvents();
        RefreshAll();

        // The transport keys work wherever focus is - tunnelling, so they arrive before the grid
        // or a list would take them.
        AddHandler(KeyDownEvent, OnTransportKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnDawKeyUp, RoutingStrategies.Tunnel);

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            StopPlayback();
            StopPatternPreview();
            _signalPath.StopAudition();
            _digi.StopAudition();
            _jam?.Dispose();
            _midi?.Dispose();
            _autosave.Dispose();
        };

        Opened += (_, _) =>
        {
            ReopenMidi();
            if (songPath is not null)
                Open(songPath);
            _grid.Focus();
            if (FrameProbeLog is { } log)
            {
                StartFrameProbe(log);
                return;
            }
            PrepareJam();
            Dispatcher.UIThread.Post(() => _ = AskAboutUpdatesAsync(), DispatcherPriority.Background);
        };

        // Its state is EditorSettings' - the first start's question can set it too.
        _checkForUpdatesItem.IsChecked = EditorSettings.CheckForUpdates == true;
        _checkForUpdatesItem.Click += (_, _) =>
        {
            bool on = EditorSettings.CheckForUpdates != true;
            EditorSettings.CheckForUpdates = on;
            _checkForUpdatesItem.IsChecked = on;
            if (on)
                _ = CheckForUpdatesAsync();
        };
    }

    // ------------------------------------------------------------------------------------ updates

    /// <summary>MagSID's releases, from 1.7 this editor on every system - tagged v1.7 and on, the
    /// same repository and website the WinForms editor up to 1.64 looks to.</summary>
    private const string UpdateRepository = UpdateCheck.Repository;

    private const string UpdateTagPrefix = UpdateCheck.TagPrefix;

    private readonly MenuItem _checkForUpdatesItem = new() { Header = "Check for Updates", ToggleType = MenuItemToggleType.CheckBox };

    /// <summary>On the first start, whether to check for updates from now on - off unless the
    /// answer is yes; then, when it is on, the check.</summary>
    private async Task AskAboutUpdatesAsync()
    {
        if (EditorSettings.CheckForUpdates is null)
        {
            bool yes = await AskAsync("Check for updates",
                "Check for updates each time MagSID starts?",
                "MagSID would look up the latest version on its website when it starts, and tell you when a " +
                "newer one is out. Nothing else is sent. You can change this any time under Menu → Check for Updates.");
            EditorSettings.CheckForUpdates = yes;
            _checkForUpdatesItem.IsChecked = yes;
            if (!yes)
                return;
        }

        if (EditorSettings.CheckForUpdates == true)
            await CheckForUpdatesAsync();
    }

    /// <summary>Looks for a newer release, and offers the website when there is one; says nothing
    /// when there is not, or when the website cannot be reached.</summary>
    private async Task CheckForUpdatesAsync()
    {
        if (await UpdateCheck.NewerReleaseAsync(UpdateRepository, UpdateTagPrefix) is not { } newer)
            return;

        string version = newer[UpdateTagPrefix.Length..];
        string running = UpdateCheck.Running?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        if (await AskAsync("A newer MagSID", $"MagSID {version} is out - you have {running}.", "Open the website to download it?")
            && Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(UpdateCheck.Website));
    }

    /// <summary>A yes-or-no question; true for Yes.</summary>
    private async Task<bool> AskAsync(string title, string question, string detail) =>
        await MessageWindow.ChooseAsync(this, title, question, ["Yes", "No"], accept: 0, cancel: 1, detail) == 0;

    // ------------------------------------------------------------------------------------ layout

    private Control BuildLayout()
    {
        var menu = BuildMenu();

        _play.Content = "▶ Play";
        _play.Click += (_, _) =>
        {
            if (_output is not null)
                StopTransport();
            else
                StartFromBeginning();
            _grid.Focus();
        };

        var speed = BuildSpeedButton();

        _editMode.VerticalAlignment = VerticalAlignment.Center;
        _editMode.Cursor = new Cursor(StandardCursorType.Hand);
        _editMode.PointerPressed += (_, _) =>
        {
            _grid.ToggleEditMode();
            _grid.Focus();
        };

        _chip.Click += (_, _) =>
        {
            _chipModel = _chipModel == ChipModel.Mos6581 ? ChipModel.Mos8580 : ChipModel.Mos6581;
            EditorSettings.Chip6581 = _chipModel == ChipModel.Mos6581;
            _daw.ChipModel = _chipModel;
            _jam?.Dispose();
            _jam = null;
            PrepareJam();
            UpdateChipButton();
            if (_output is not null)
            {
                var resume = _engine?.PositionAt(_output.SamplesPlayed);
                StopPlayback();
                StartPlaybackFrom(resume);
            }
            _grid.Focus();
        };
        UpdateChipButton();

        var open = StripButton("Open", () => _ = OpenAsync());
        var save = StripButton("Save", () => _ = SaveAsync(saveAs: false));
        var previous = StripButton("◀", () => _grid.MoveCursorRowPage(-TrackerGridControl.PageRows));
        var next = StripButton("▶", () => _grid.MoveCursorRowPage(TrackerGridControl.PageRows));
        _play.Classes.Add("strip");
        _chip.Classes.Add("strip");
        _editMode.Margin = new Thickness(6, 0);

        // The Windows editor's toolbar, item for item: file, speed, transport, paging, size.
        var toolbar = new Border
        {
            Background = EditorTheme.Field,
            BorderBrush = EditorTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    open, save, StripSeparator(),
                    speed, StripSeparator(),
                    _play, _editMode, _chip, StripSeparator(),
                    Label("Scroll:"), previous, next, StripSeparator(),
                    _songLength, StripSeparator(),
                    _rasterTime,
                },
            },
        };
        foreach (var label in new[] { _songLength, _rasterTime })
        {
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Foreground = Body;
            label.Margin = new Thickness(4, 0);
        }

        var listHeader = BuildInstrumentListHeader();
        var list = BuildInstrumentTable();

        _instrumentEditor.Edited += () =>
        {
            _session.MarkDirty();
            RefreshInstrumentNames();
        };

        var left = new DockPanel { Width = InstrumentColumnWidth };
        DockPanel.SetDock(listHeader, Dock.Top);
        DockPanel.SetDock(list, Dock.Top);
        left.Children.Add(listHeader);
        left.Children.Add(list);
        left.Children.Add(_instrumentEditor);

        // The grid's columns are fixed pixel widths; it keeps its own width.
        _grid.Width = TrackerGridControl.ContentWidth;
        var gridHost = new Border { Child = _grid };

        // Instruments and the grid side by side, a splitter between them, as in the Windows
        // editor: the pair is centred and a wide window's spare width is margin either side of
        // it. Dragging the splitter gives the instrument column more room or less; the grid
        // keeps its width, so a channel is never sliced off.
        var retro = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var splitter = InstrumentSplitter(left);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(splitter, Dock.Left);
        retro.Children.Add(left);
        retro.Children.Add(splitter);
        retro.Children.Add(gridHost);
        _retroView = retro;

        // Both views fill the same place; only one is ever visible, and both stay alive so
        // selection and scroll survive a round trip.
        _daw.IsVisible = false;
        _daw.ChipModel = _chipModel;
        // Signal Path and Digi to SID-osc take the same place when opened, the song's view
        // waiting underneath.
        _signalPathHost = ToolHost("Signal Path", _signalPath);
        _digiHost = ToolHost("Digi to SID-osc", _digi);
        var centre = new Panel { Children = { retro, _daw, _signalPathHost, _digiHost } };

        // The file on the left, a passing notice and whether it is saved on the right - 1.64's
        // status strip.
        var statusRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _notice, _dirty },
        };
        _notice.Foreground = EditorTheme.Notice;
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _notice.Text = "";
        };
        var statusRow = new DockPanel();
        DockPanel.SetDock(statusRight, Dock.Right);
        statusRow.Children.Add(statusRight);
        statusRow.Children.Add(_status);
        var statusBar = new Border
        {
            Background = EditorTheme.Field,
            BorderBrush = EditorTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 3),
            Child = statusRow,
        };

        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(toolbar);
        root.Children.Add(statusBar);
        root.Children.Add(centre);
        return root;
    }

    private readonly TextBlock _notice = new();
    private readonly TextBlock _dirty = new();

    /// <summary>A notice is read once and then gets out of the way.</summary>
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>"v1.72", from the project's version - the same number About shows.</summary>
    private static string EditorVersion
    {
        get
        {
            var version = typeof(MainWindow).Assembly.GetName().Version;
            return $"v{version?.Major}.{version?.Minor}";
        }
    }

    /// <summary>A tool in the centre: its name and a Close on a strip above it.</summary>
    private Control ToolHost(string name, Control tool)
    {
        var close = StripButton("Close", CloseTool);
        ToolTip.SetTip(close, "Back to the song (Esc)");
        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = EditorTheme.Title,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var strip = new Border
        {
            Background = EditorTheme.Field,
            BorderBrush = EditorTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 1, 2, 1),
            Child = header,
        };
        var host = new DockPanel { IsVisible = false };
        DockPanel.SetDock(strip, Dock.Top);
        host.Children.Add(strip);
        host.Children.Add(tool);
        return host;
    }

    /// <summary>Opens <paramref name="host"/>'s tool in the centre, or closes it when it is
    /// already there.</summary>
    private void ToggleTool(Control host)
    {
        if (_toolShowing == host)
        {
            CloseTool();
            return;
        }

        CloseTool();
        _dawHeldKeys.Clear();
        _toolShowing = host;
        _retroView.IsVisible = false;
        _daw.IsVisible = false;
        host.IsVisible = true;
        if (host == _signalPathHost)
            _signalPath.ShowFor(_session.Song.Instruments, _dawShowing ? _daw.SelectedInstrumentNumber : SelectedInstrument?.Number);
        host.Focus();
    }

    /// <summary>Puts the song's view back, silencing whatever the tool was playing.</summary>
    private void CloseTool()
    {
        if (_toolShowing is null)
            return;

        _toolShowing.IsVisible = false;
        _toolShowing = null;
        _signalPath.StopAudition();
        _digi.StopAudition();

        _retroView.IsVisible = !_dawShowing;
        _daw.IsVisible = _dawShowing;
        // Its controls did not repaint while it was hidden.
        System.Windows.Forms.Control.RepaintAll(_dawShowing ? _daw : _retroView);
        if (_dawShowing)
            _daw.Focus();
        else
            _grid.Focus();
    }

    /// <summary>A flat toolbar button, as a ToolStripButton: no frame until the pointer is on it.</summary>
    private Button StripButton(string text, Action action)
    {
        var button = new Button { Content = text, Classes = { "strip" } };
        button.Click += (_, _) =>
        {
            action();
            _grid.Focus();
        };
        return button;
    }

    private static Control StripSeparator() => new Border
    {
        Width = 1,
        Margin = new Thickness(4, 3),
        Background = EditorTheme.Border,
    };

    /// <summary>The instrument column's width to start with, and how narrow and wide the splitter
    /// can make it. The splitter's own width is 1.64's SplitContainer's.</summary>
    private const double InstrumentColumnWidth = 620;
    private const double SplitterWidth = 4;
    private const double NarrowestInstrumentColumn = 260;
    private const double WidestInstrumentColumn = 900;

    /// <summary>The bar between the instrument column and the grid: a drag sets the column's width.</summary>
    private static Control InstrumentSplitter(Control column)
    {
        var bar = new Border
        {
            Width = SplitterWidth,
            Background = EditorTheme.Border,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };

        double? grabbedAt = null;
        double startWidth = 0;
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed)
                return;
            grabbedAt = e.GetPosition(null).X;
            startWidth = column.Bounds.Width;
            e.Pointer.Capture(bar);
            e.Handled = true;
        };
        bar.PointerMoved += (_, e) =>
        {
            if (grabbedAt is not { } from)
                return;
            column.Width = Math.Clamp(startWidth + e.GetPosition(null).X - from, NarrowestInstrumentColumn, WidestInstrumentColumn);
        };
        bar.PointerReleased += (_, e) =>
        {
            grabbedAt = null;
            e.Pointer.Capture(null);
        };
        bar.PointerCaptureLost += (_, _) => grabbedAt = null;
        return bar;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Body,
    };

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            if (gesture is not null)
                item.InputGesture = KeyGesture.Parse(gesture);
            return item;
        }

        // A preference the menu shows ticked or not. Its state is kept here and the tick set from
        // it, so the item reads right whether or not the menu flips the tick itself.
        MenuItem Setting(string header, bool on, Action<bool> changed)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
            item.Click += (_, _) =>
            {
                on = !on;
                item.IsChecked = on;
                changed(on);
            };
            return item;
        }

        string command = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

        // 1.64's menu bar, item for item: one Menu with the file, history and preferences, then
        // the tools as items of their own, MIDI, and the view switch last.
        var file = new MenuItem
        {
            Header = "_Menu",
            ItemsSource = new Control[]
            {
                Item("About", () => _ = new AboutWindow().ShowDialog(this)),
                new Separator(),
                Item("Undo", () => StepHistory(undo: true), $"{command}+Z"),
                Item("Redo", () => StepHistory(undo: false), $"{command}+Y"),
                new Separator(),
                Item("New", () => _ = NewAsync(), $"{command}+N"),
                Item("Open...", () => _ = OpenAsync(), "F10"),
                Item("Import MIDI...", () => _ = ImportMidiAsync()),
                Item("Import SID...", () => _ = ImportSidAsync()),
                new Separator(),
                Item("Save", () => _ = SaveAsync(saveAs: false), $"{command}+S"),
                Item("Save As...", () => _ = SaveAsync(saveAs: true), "F11"),
                new Separator(),
                Item("Load Instrument...", () => _ = LoadInstrumentsAsync()),
                Item("Save Instrument...", () => _ = SaveInstrumentAsync()),
                new Separator(),
                Item("Export to .sid...", () => _ = new SidExportWindow(_session.Song, _session.Name ?? "Untitled", _chipModel).ShowDialog(this), "F9"),
                Item("Load and Play .sid...", () => new SidPlayerWindow().Show()),
                new Separator(),
                // GoatTracker's forms in the instrument editor: $80+ notes, 9x filter modes.
                Setting("Show High Hex-Values", EditorSettings.ShowHighHex, on =>
                {
                    EditorSettings.ShowHighHex = on;
                    _instrumentEditor.RefreshHighHexDisplay();
                }),
                // Space and F5 are a pair: one plays and stops, the other flips Edit and Jam,
                // and which is which depends on the tracker a musician learnt on.
                Setting("Swap Spacebar Behavior", EditorSettings.SwapSpacebar, on =>
                {
                    EditorSettings.SwapSpacebar = on;
                    UpdateEditMode();
                }),
                // On by default: a loop drawn as it plays is what keeps the channels in line.
                Setting("Unroll Loops", !EditorSettings.CompactLoops, on =>
                {
                    EditorSettings.CompactLoops = !on;
                    _grid.UnrollLoops = on;
                    _daw.UnrollLoops = on;
                }),
                _checkForUpdatesItem,
                new Separator(),
                Item("Exit", Close),
            },
        };

        _viewToggle = new MenuItem { Header = "_DAW" };
        _viewToggle.Click += (_, _) => ToggleView();

        // Listed afresh each time it opens, so a keyboard plugged in mid-session shows up.
        _midiMenu = new MenuItem { Header = "_MIDI" };
        _midiMenu.Items.Add(new MenuItem { Header = "No MIDI inputs found", IsEnabled = false });
        _midiMenu.SubmenuOpened += (_, _) => BuildMidiMenu();

        return new Menu
        {
            ItemsSource = new[]
            {
                file,
                new MenuItem
                {
                    Header = "_Tools",
                    ItemsSource = new Control[]
                    {
                        Item("_Signal Path", () => ToggleTool(_signalPathHost)),
                        Item("_Digi to SID-osc", () => ToggleTool(_digiHost)),
                        Item("_Optimize", () => _ = OptimizeAsync()),
                    },
                },
                _midiMenu,
                _viewToggle,
            },
        };
    }

    // ------------------------------------------------------------------------------------ wiring

    private void WireEvents()
    {
        _session.StructureChanged += RefreshAll;
        // Another song, or instruments added: Signal Path lists what is there now.
        _session.StructureChanged += () =>
        {
            if (_toolShowing == _signalPathHost)
                _signalPath.ShowFor(_session.Song.Instruments, null);
        };
        WireInstrumentList();
        WireRasterTime();
        _grid.UnrollLoops = !EditorSettings.CompactLoops;
        _daw.UnrollLoops = !EditorSettings.CompactLoops;
        _session.DirtyChanged += RefreshStatus;
        _session.HistoryChanged += RefreshStatus;

        _grid.Edited += () => _session.MarkDirty();
        _grid.NoteEntered += PreviewNote;
        _grid.NoteReleased += ReleaseJamNote;
        _grid.InstrumentStepRequested += StepSelectedInstrument;
        _grid.JamSilenceRequested += () => _jam?.SilenceAll();
        _grid.EditModeChanged += UpdateEditMode;
        _grid.Notice += ShowNotice;
        _grid.SelectedInstrumentNumber = () =>
            SelectedInstrument?.Number ?? _session.Song.Instruments.FirstOrDefault()?.Number ?? 1;
        _grid.AudioClockSamples = () => _output?.SamplesPlayed ?? 0;
        _grid.ChannelMuteChanged += (channel, muted) =>
        {
            _engine?.SetVoiceMuted(channel, muted);
            _previewEngine?.SetVoiceMuted(channel, muted);
        };
        UpdateEditMode();

        WireDaw();

        _poll.Tick += (_, _) => DrainPlaybackEvents();
    }

    /// <summary>The DAW's events, as MainForm wires them: edits go through the session, and the
    /// tracker is told so the two views never disagree about the song.</summary>
    private void WireDaw()
    {
        _daw.InstrumentEdited += _ =>
        {
            _session.MarkDirty();
            RefreshInstrumentNames();
        };

        // A click in the piano roll is a short tap: a fixed release, whatever the instrument's own.
        _daw.NotePreviewRequested += (note, instrument) => PreviewNote(note, instrument, RollPreviewRelease);
        _daw.NotePreviewReleased += ReleaseJamNote;
        _daw.CycleChanged += () => ApplyCycle(_engine);
        _daw.RecordToggled += on =>
        {
            if (on)
                StartRecording();
            else
                StopAny();
        };
        _daw.MuteToggleRequested += channel => _grid.ToggleChannelMute(channel);
        _daw.IsChannelMuted = _grid.IsChannelMuted;
        _daw.Notice += ShowNotice;

        _daw.InstrumentAddRequested += AddInstrument;
        _daw.InstrumentFromBankRequested += AddFromBank;
        _daw.InstrumentSoundChangeRequested += ChangeSound;
        _daw.InstrumentLoadRequested += () => _ = LoadInstrumentsAsync();
        _daw.SoundAuditionRequested += AuditionSound;
        _daw.InstrumentCopyRequested += instrument => _session.CopyInstrument(instrument);
        _daw.InstrumentDeleteRequested += instrument => _session.DeleteInstrument(instrument);

        _daw.PatternEdited += SongEditedInDaw;
        _daw.OrderEdited += SongEditedInDaw;
    }

    private void SongEditedInDaw()
    {
        _session.MarkDirty();
        _grid.SetSong(_session.Song);
        RefreshStatus();
    }

    /// <summary>Swaps the centre between the tracker and the DAW. Both edit the same Song, so there
    /// is nothing to convert; the selected instrument is carried across.</summary>
    private void ToggleView()
    {
        // From a tool, the view the menu names: the tool goes, then the views swap as ever.
        CloseTool();
        _dawShowing = !_dawShowing;
        _dawHeldKeys.Clear();

        if (_dawShowing)
        {
            _daw.LoadSong(_session.Song);
            if (SelectedInstrument is { } selected)
                _daw.SelectInstrument(selected.Number);
        }
        else if (_daw.SelectedInstrumentNumber is { } number)
        {
            SelectInstrumentNumber(number);
        }

        _retroView.IsVisible = !_dawShowing;
        _daw.IsVisible = _dawShowing;

        // Its controls did not repaint while it was hidden.
        System.Windows.Forms.Control.RepaintAll(_dawShowing ? _daw : _retroView);
        _viewToggle.Header = _dawShowing ? "_Retro" : "_DAW";

        if (_dawShowing)
            _daw.Focus();
        else
            _grid.Focus();
    }

    private void OnTransportKey(object? sender, KeyEventArgs e)
    {
        // A tool's keys are its own; Esc closes it, unless it is closing a drop-down.
        if (_toolShowing is not null)
        {
            if (e.Key == Key.Escape && FocusManager?.GetFocusedElement() is not ComboBox { IsDropDownOpen: true })
            {
                CloseTool();
                e.Handled = true;
            }
            return;
        }

        // Typing in a field keeps its keys.
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        if (CommandKey(e))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F1:
                StartFromBeginning();
                break;
            case Key.F2:
                // In the DAW the project cursor says where, and F2 goes back to it while playing too.
                if (_dawShowing)
                    PlayFromProjectCursor();
                else
                    StartFromCurrentPosition();
                break;
            case Key.F3:
                PlayCurrentPattern();
                break;
            case Key.F4:
                // Stopped already: a second stop takes the DAW's project cursor back to where
                // playback last started, as Cubase does.
                if (_output is null && _previewOutput is null && _dawShowing)
                    _daw.ReturnToPlayStart();
                else
                    StopAny();
                break;
            case Key.F9:
                _ = new SidExportWindow(_session.Song, _session.Name ?? "Untitled", _chipModel).ShowDialog(this);
                break;
            case Key.F5:
                _grid.ToggleEditMode();
                break;
            case Key.Space when e.KeyModifiers == KeyModifiers.None:
                if (EditorSettings.SwapSpacebar)
                    ToggleTransport();
                else
                    _grid.ToggleEditMode();
                break;
            default:
                if (_dawShowing)
                    OnDawKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The piano keys, for the DAW view - caught at the window, before whatever has focus, as
    /// MainForm does with KeyPreview: the tracker grid that owns them in Retro is hidden here.
    /// Routed through the same path a MIDI key takes, so Edit mode decides whether the note is
    /// written into the roll and Jam mode only sounds it.
    /// </summary>
    private void OnDawKeyDown(KeyEventArgs e)
    {
        // The arrange lanes take digits as pattern numbers, and a selected LE in the roll takes
        // them as its loop count; 2, 3, 5, 6 and 7 would otherwise be piano keys.
        var modifiers = System.Windows.Forms.KeyMap.Modifiers(e.KeyModifiers);
        if ((modifiers & (WinKeys.Control | WinKeys.Alt)) != 0 || _daw.ArrangeHasFocus)
            return;

        var key = System.Windows.Forms.KeyMap.From(e);
        if (_daw.RollTakesDigits && key is >= WinKeys.D0 and <= WinKeys.D9 or >= WinKeys.NumPad0 and <= WinKeys.NumPad9)
            return;

        bool shift = (modifiers & WinKeys.Shift) != 0;

        // Numpad /, Cubase's Cycle key.
        if (key == WinKeys.Divide)
        {
            _daw.CycleOn = !_daw.CycleOn;
            e.Handled = true;
            return;
        }

        if (PianoKeyMap.IsOctaveKey(key))
        {
            _daw.StepOctave(shift);
            e.Handled = true;
            return;
        }

        // Shift+C and Shift+V are the roll's own copy and paste.
        if (shift && key is WinKeys.C or WinKeys.V)
            return;

        if (PianoKeyMap.TryNote(key, _daw.Octave) is not { } note)
            return;

        // A held key repeats; the note is already sounding.
        if (_dawHeldKeys.TryAdd(key, note))
            PlayDawNote(note);

        e.Handled = true;
    }

    private void OnDawKeyUp(object? sender, KeyEventArgs e)
    {
        if (_dawHeldKeys.Remove(System.Windows.Forms.KeyMap.From(e), out var note))
        {
            ReleaseDawNote(note);
            e.Handled = true;
        }
    }

    /// <summary>A piano key in the DAW. It always sounds; it is written down only by a take, a
    /// chord being recorded, or in Edit mode with the roll focused - see MainForm.OnMidiNote.</summary>
    private void PlayDawNote(NoteName note)
    {
        int instrument = _daw.SelectedInstrumentNumber ?? SelectedInstrument?.Number ?? 1;

        if (_daw.IsRecording)
            _daw.RecordNote(note, instrument);
        else if (_daw.IsRecordingChord)
            _daw.RecordChordNote(note);
        else if (_grid.EditMode && _daw.RollHasFocus)
            _daw.StepInput(note);

        PreviewNote(note, instrument);
    }

    private void ReleaseDawNote(NoteName note)
    {
        if (_daw.IsRecording)
            _daw.RecordRelease(note);
        _daw.RecordChordRelease(note);
        ReleaseJamNote(note);
    }

    private Instrument? SelectedInstrument => InstrumentInSlot(_instruments.SelectedIndex);

    private void RefreshAll()
    {
        _refreshing = true;
        try
        {
            _grid.SetSong(_session.Song);
            // Undo restores a whole-song snapshot, so the DAW is pointed at the new object too.
            _daw.LoadSong(_session.Song);
            LoadSpeed();
            RefreshInstrumentNames();
            ShowSelectedInstrument();
            RefreshStatus();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowSelectedInstrument() => _instrumentEditor.Load(SelectedInstrument);

    private void RefreshStatus()
    {
        string name = _session.FilePath is { } path ? Path.GetFileName(path) : _session.Name ?? "New Song";
        Title = $"MagSID Editor {EditorVersion} - {name}{(_session.IsDirty ? " *" : "")}";
        _status.Text = _session.FilePath ?? "(unsaved)";
        _dirty.Text = _session.IsDirty ? "Modified" : "Saved";

        RefreshSongLength();
    }

    /// <summary>
    /// The menus' command keys - Cmd on a Mac, Ctrl elsewhere. A menu item's gesture is only the
    /// words beside it, so without this they did nothing. Open and Save As are F10 and F11 too,
    /// as in the Windows editor.
    /// </summary>
    private bool CommandKey(KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Action? action = (e.Key, e.KeyModifiers) switch
        {
            (Key.F10, KeyModifiers.None) => () => _ = OpenAsync(),
            (Key.F11, KeyModifiers.None) => () => _ = SaveAsync(saveAs: true),
            (Key.N, var m) when m == command => () => _ = NewAsync(),
            (Key.O, var m) when m == command => () => _ = OpenAsync(),
            (Key.S, var m) when m == command => () => _ = SaveAsync(saveAs: false),
            (Key.S, var m) when m == (command | KeyModifiers.Shift) => () => _ = SaveAsync(saveAs: true),
            (Key.Z, var m) when m == command => () => StepHistory(undo: true),
            (Key.Y, var m) when m == command => () => StepHistory(undo: false),
            (Key.Z, var m) when m == (command | KeyModifiers.Shift) => () => StepHistory(undo: false),
            _ => null,
        };

        action?.Invoke();
        return action is not null;
    }

    /// <summary>Space plays and stops when the spacebar is swapped - from the project cursor in
    /// the DAW, from the top in Retro - as F4 and F1 do.</summary>
    private void ToggleTransport()
    {
        if (_output is not null)
            StopAny();
        else if (_dawShowing)
            PlayFromProjectCursor();
        else
            StartFromBeginning();
    }

    private void UpdateEditMode()
    {
        // The key is on the label, not only in the menu: Space changes meaning silently when the
        // switch is flipped, and a song that starts playing because of a setting made weeks ago
        // reads as a fault.
        string key = EditorSettings.SwapSpacebar ? "F5" : "Space";
        ToolTip.SetTip(_editMode, $"Edit/Jam ({key}) - click to flip");
        _editMode.Text = (_grid.EditMode ? "● EDIT" : "○ JAM") + " · " + key;
        _editMode.Foreground = _grid.EditMode ? Brushes.OrangeRed : Brushes.MediumSeaGreen;
    }

    private void UpdateChipButton() => _chip.Content = $"SID: {(_chipModel == ChipModel.Mos6581 ? "6581" : "8580")}";

    private void ShowNotice(string text)
    {
        _notice.Text = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void StepHistory(bool undo)
    {
        StopTransport();
        if (undo)
            _session.Undo();
        else
            _session.Redo();
    }

    // ------------------------------------------------------------------------------------ instruments

    private void AddInstrument()
    {
        if (_session.AddInstrument() is not { } added)
        {
            ShowInstrumentLimit();
            return;
        }

        SelectInstrumentNumber(added.Number);
        if (_dawShowing)
            _daw.RenameSelectedInstrument();
        else
            BeginRename(added.Number);
    }

    /// <summary>A copy of one of the GM bank's sounds, as it is in the bank, added after the
    /// song's own instruments.</summary>
    private void AddFromBank(GmBank.Sound sound)
    {
        var instrument = InstrumentFile.Read(InstrumentFile.Write(sound.Instrument));
        if (_session.ImportInstrument(instrument) is not { } added)
        {
            ShowInstrumentLimit();
            return;
        }

        SelectInstrumentNumber(added.Number);
        ShowNotice($"Added {added.Name} from the sound bank ({sound.Origin})");
    }

    private static readonly FilePickerFileType InstrumentFiles = new("MagSID instruments") { Patterns = ["*.json"] };

    private async Task LoadInstrumentsAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Load instrument",
            AllowMultiple = true,
            FileTypeFilter = [InstrumentFiles, FilePickerFileTypes.All],
        };
        options.SuggestedStartLocation = await StorageProvider.StartIn(DialogFolder.LoadInstrument);

        var files = await StorageProvider.OpenFilePickerAsync(options);
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0)
            return;

        EditorSettings.RememberFolder(DialogFolder.LoadInstrument, paths[0]);

        var problems = new List<string>();
        Instrument? last = null;
        foreach (string path in paths)
        {
            string file = Path.GetFileName(path);
            Instrument loaded;
            try
            {
                loaded = InstrumentFile.Read(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is InstrumentFileException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file}: {ex.Message}");
                continue;
            }

            if (_session.ImportInstrument(loaded) is null)
            {
                problems.Add($"{file}: the song already has all {SongValidator.MaxInstrumentNumber} instruments.");
                break;
            }
            last = loaded;
        }

        if (last is not null)
            SelectInstrumentNumber(last.Number);

        if (problems.Count > 0)
            await MessageWindow.ShowAsync(this, "Some instruments were not loaded", string.Join(Environment.NewLine + Environment.NewLine, problems));
    }

    /// <summary>Selects the instrument in both views' lists.</summary>
    private void SelectInstrumentNumber(int number)
    {
        if (InstrumentInSlot(number - 1) is not null)
            _instruments.SelectedIndex = number - 1;
        _daw.SelectInstrument(number);
    }

    private void ShowInstrumentLimit() =>
        ShowNotice($"This song already has all {SongValidator.MaxInstrumentNumber} instruments a pattern can select.");

    private void DeleteInstrument()
    {
        if (SelectedInstrument is { } instrument)
            _session.DeleteInstrument(instrument);
    }

    private void StepSelectedInstrument(int delta)
    {
        // Through the instruments there are, in number order, round from the last to the first.
        var numbers = _session.Song.Instruments.Select(i => i.Number).Order().ToList();
        if (numbers.Count == 0)
            return;

        int at = Math.Max(0, SelectedInstrument is { } current ? numbers.IndexOf(current.Number) : 0);
        int next = (at + delta + numbers.Count) % numbers.Count;
        SelectInstrumentNumber(numbers[next]);
    }

    // ------------------------------------------------------------------------------------ playback

    /// <summary>The release a note clicked into the piano roll is auditioned with - a tap, so the
    /// same short tail for every instrument. As MainForm.RollPreviewRelease.</summary>
    private const int RollPreviewRelease = 0x9;

    /// <summary>The longest release an Edit-mode note is auditioned with.</summary>
    private const int MaxEditPreviewRelease = 0xA;

    private void PreviewNote(NoteName note, int instrumentNumber) => PreviewNote(note, instrumentNumber, null);

    /// <param name="forcedRelease">A release nibble to audition with in place of the
    /// instrument's own, or null for the usual: capped in Edit mode only.</param>
    private void PreviewNote(NoteName note, int instrumentNumber, int? forcedRelease)
    {
        var instrument = _session.Song.Instruments.FirstOrDefault(i => i.Number == instrumentNumber);
        if (instrument is null)
            return;

        if (forcedRelease is { } release)
            instrument = WithRelease(instrument, release);
        else if (_grid.EditMode && (instrument.SustainRelease & 0x0F) > MaxEditPreviewRelease)
            instrument = WithRelease(instrument, MaxEditPreviewRelease);

        try
        {
            _jam ??= new JamEngine(_chipModel);
            _jam.Start();
            _jam.NoteOn(note, instrument);
            _daw.SetNoteSounding(note, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            _jam?.Dispose();
            _jam = null;
        }
    }

    /// <summary>
    /// Opens the note preview's sound in the background, so the first note does not wait for it:
    /// on Windows, starting PortAudio and opening a stream take most of a second, and a note
    /// waited that long on the UI thread - before it was heard, and before it was written into
    /// the pattern. Asked for at start-up, after the chip changes, and when playback stops, as
    /// playback closes it. Looked at once the UI is idle, so a play that is just starting does
    /// not get a second stream opened beside it.
    /// </summary>
    private void PrepareJam()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _jam is not null || _jamOpening || _output is not null || _previewOutput is not null)
                return;

            _jamOpening = true;
            var chip = _chipModel;
            Task.Run(() =>
            {
                try
                {
                    var jam = new JamEngine(chip);
                    jam.Start();
                    return jam;
                }
                catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException)
                {
                    return null;
                }
            }).ContinueWith(opened => Dispatcher.UIThread.Post(() =>
            {
                _jamOpening = false;
                if (!opened.IsCompletedSuccessfully || opened.Result is not { } jam)
                    return;

                // A note played meanwhile opened one of its own, or something else began.
                if (_closed || _jam is not null || _output is not null || _previewOutput is not null || chip != _chipModel)
                    jam.Dispose();
                else
                    _jam = jam;
            }));
        }, DispatcherPriority.Background);
    }

    private void ReleaseJamNote(NoteName note)
    {
        _jam?.NoteOff(note);
        _daw.SetNoteSounding(note, false);
    }

    /// <summary>A copy with the release nibble replaced; the song's own instrument is never touched.</summary>
    private static Instrument WithRelease(Instrument instrument, int release)
    {
        if ((instrument.SustainRelease & 0x0F) == release)
            return instrument;

        var copy = InstrumentFile.Read(InstrumentFile.Write(instrument));
        copy.Number = instrument.Number;
        copy.SustainRelease = (byte)((instrument.SustainRelease & 0xF0) | (release & 0x0F));
        return copy;
    }

    /// <summary>Plays a sound that is not in the song yet - the sound bank's cards - for half a second.</summary>
    private void AuditionSound(Instrument instrument, NoteName note)
    {
        try
        {
            _jam ??= new JamEngine(_chipModel);
            _jam.Start();
            _jam.ResetFilter();
            _jam.NoteOn(note, instrument);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            _jam?.Dispose();
            _jam = null;
            return;
        }

        var release = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        release.Tick += (_, _) =>
        {
            release.Stop();
            _jam?.NoteOff(note);
        };
        release.Start();
    }

    private void StartFromBeginning()
    {
        _daw.ResetProjectCursor();
        StopTransport();
        _resumeFrom = null;
        _resumeCursor = null;
        _grid.SetSongPosition(0);
        StartFromCurrentPosition();
    }

    private void StartFromCurrentPosition()
    {
        if (_output is not null)
            return;
        StartPlaybackFrom(ResumePosition());
    }

    /// <summary>Where the next play begins: exactly where it stopped while the cursor has not
    /// moved since, otherwise the cursor's row - found by running the song silently up to it, so
    /// every channel is where the song really has it. See MainForm.ResumePosition.</summary>
    private SongSequencer.SequencerPosition? ResumePosition()
    {
        if (_resumeFrom is not null && _resumeCursor is { } parked &&
            parked.Position == _grid.SongPosition && parked.Row == _grid.CursorRow)
            return _resumeFrom;

        if (_grid.CursorSongLocation() is not { } target || (target.OrderIndex == 0 && target.RowIndex == 0))
            return null;

        return target.TicksInto >= 0
            ? SongSequencer.FindPositionInEntry(_session.Song, target.Channel, target.OrderIndex, target.TicksInto)
            : SongSequencer.FindPosition(_session.Song, target.Channel, target.OrderIndex, target.RowIndex);
    }

    /// <summary>
    /// Cycle, when it is on in the DAW and the locators are set: the engine goes on from L each
    /// time the song reaches R. Cleared otherwise. As MainForm.ApplyCycle.
    /// </summary>
    private void ApplyCycle(SongPlaybackEngine? engine)
    {
        if (engine is null)
            return;

        if (!_daw.CycleOn || _daw.LocatorRange is not { } range)
        {
            engine.ClearCycle();
            return;
        }

        var start = range.Left is { } left
            ? SongSequencer.FindPositionInEntry(_session.Song, left.Channel, left.Index, left.TicksInto)
            : null;
        var end = SongSequencer.FindPositionInEntry(_session.Song, range.Right.Channel, range.Right.Index, range.Right.TicksInto);
        if (end is null)
        {
            engine.ClearCycle();
            return;
        }

        engine.SetCycle(start, end.Frame);
    }

    /// <summary>The DAW's F2: plays from the project cursor - where the song last stopped, or
    /// wherever it was put in the arrange ruler. Playing already, it starts again from there.</summary>
    private void PlayFromProjectCursor()
    {
        StopAny();
        _daw.MarkPlayStart();
        StartPlaybackFrom(_daw.PlayFrom is { } at
            ? SongSequencer.FindPositionInEntry(_session.Song, at.Channel, at.Index, at.TicksInto)
            : null);
    }

    /// <summary>
    /// The piano roll's REC: plays the song from the selected clip's place in it - or the left
    /// locator with PUNCH on, a bar earlier with PRE-ROLL - and the notes played go into that
    /// clip's pattern. As MainForm.StartRecording.
    /// </summary>
    private void StartRecording()
    {
        StopAny();
        if (_session.Song.Instruments.Count == 0 || !_daw.BeginRecording() || _daw.RecordingFrom is not { } clip)
            return;

        var song = _session.Song;
        SongSequencer.SequencerPosition? punchIn;
        int punchOut = int.MaxValue;
        if (_daw.PunchOn && _daw.LocatorRange is { } range)
        {
            punchIn = range.Left is { } left ? SongSequencer.FindPositionInEntry(song, left.Channel, left.Index, left.TicksInto) : null;
            punchOut = SongSequencer.FindPositionInEntry(song, range.Right.Channel, range.Right.Index, range.Right.TicksInto)?.Frame ?? int.MaxValue;
        }
        else
        {
            punchIn = clip.Index == 0 ? null : SongSequencer.FindPositionInEntry(song, clip.Channel, clip.Index, 0);
            if (clip.Index > 0 && punchIn is null)
            {
                _daw.EndRecording();
                return;
            }
        }

        // A bar is 16 rows, as the piano roll counts; a beat is 4.
        int framesPerBeat = (int)Math.Round(4 * song.AverageFramesPerRow);
        int punchInFrame = punchIn?.Frame ?? 0;
        var start = _daw.PreRollOn && punchInFrame > 0
            ? SongSequencer.PositionAtFrame(song, Math.Max(0, punchInFrame - (4 * framesPerBeat)))
            : punchIn;

        _recordWindow = (punchInFrame, punchOut);
        _daw.SetRecordWindow(false);
        _resumeFrom = null;
        _resumeCursor = null;
        StartPlaybackFrom(start);
        if (_output is null || _engine is null)
        {
            _daw.EndRecording();
            return;
        }

        if (_daw.ClickOn && _daw.PreRollOn && start is not null)
            _engine.SetClick(start.Frame, punchInFrame, framesPerBeat);
        else if (_daw.ClickOn)
            _engine.SetCountIn(4 * framesPerBeat, framesPerBeat);
    }

    /// <summary>F4: stops whichever is running, the song or an F3 loop.</summary>
    private void StopAny()
    {
        if (_previewOutput is not null)
            StopPatternPreview();
        else
            StopTransport();
    }

    /// <summary>
    /// F3: loops what is on screen - each channel's pattern at the cursor's row, from that pattern's
    /// own start - so a sound can be worked on without playing the song up to it. A channel whose
    /// order does not reach the row plays a silent row. As MainForm.PlayCurrentPattern.
    /// </summary>
    private void PlayCurrentPattern()
    {
        if (_session.Song.Instruments.Count == 0)
            return;

        StopTransport();
        StopPatternPreview();
        _jam?.Dispose();
        _jam = null;

        var patterns = _grid.CurrentPatternsAtCursor();
        if (Array.TrueForAll(patterns, p => p is null))
            return;

        var preview = new Song { Speed = _session.Song.Speed, FunkSpeed = _session.Song.FunkSpeed };
        preview.Instruments.AddRange(_session.Song.Instruments);
        var seen = new HashSet<int>();
        foreach (var pattern in patterns)
        {
            if (pattern is not null && seen.Add(pattern.Number))
                preview.Patterns.Add(pattern);
        }

        var silent = new Pattern { Number = preview.Patterns.Max(p => p.Number) + 1 };
        silent.Rows.Add(new HoldRow());
        preview.Patterns.Add(silent);
        for (int ch = 0; ch < 3; ch++)
            preview.ChannelOrders[ch].Tokens.Add(new PatternRefToken(patterns[ch]?.Number ?? silent.Number));

        try
        {
            var engine = new SongPlaybackEngine(preview, _chipModel);
            for (int ch = 0; ch < 3; ch++)
                engine.SetVoiceMuted(ch, _grid.IsChannelMuted(ch));
            _previewEngine = engine;
            _previewOutput = AudioOutput.Start(engine, 120);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException or ArgumentException)
        {
            StopPatternPreview();
            _ = MessageWindow.ShowAsync(this, "Cannot play pattern", ex.Message);
        }
    }

    private void StopPatternPreview()
    {
        var output = _previewOutput;
        _previewOutput = null;
        _previewEngine = null;
        output?.Stop();
        output?.Dispose();
        PrepareJam();
    }

    private void StartPlaybackFrom(SongSequencer.SequencerPosition? resume)
    {
        StopPatternPreview();
        try
        {
            _jam?.Dispose();
            _jam = null;

            var engine = new SongPlaybackEngine(_session.Song, _chipModel, resume);
            ApplyCycle(engine);
            for (int ch = 0; ch < 3; ch++)
                engine.SetVoiceMuted(ch, _grid.IsChannelMuted(ch));

            var output = AudioOutput.Open!(engine, 120);
            output.Stopped += _ => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_output, output))
                    StopPlayback();
            });

            _engine = engine;
            _output = output;
            StartLiveRaster(resume?.Frame ?? 0);
            output.Play();

            _grid.SetPlaybackActive(true);
            _grid.SetPlaybackStartEntries(
                resume is null ? null : [.. resume.Channels.Select(c => c.PlayingEntry)],
                resume is null ? null : SongSequencer.TicksIntoEntries(_session.Song, resume));
            _poll.Start();
            RequestPlaybackFrames();
            _play.Content = "■ Stop";
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException or ArgumentException)
        {
            StopPlayback();
            _ = MessageWindow.ShowAsync(this, "Cannot start playback", ex.Message);
        }
    }

    /// <summary>F4: parks the cursor where playback reached and keeps that as F2's resume point.</summary>
    private void StopTransport()
    {
        if (_output is null)
            return;

        _resumeFrom = _engine?.PositionAt(_output.SamplesPlayed);
        _grid.JumpEditCursorToLivePlaybackPosition();
        StopPlayback();
        _resumeCursor = (_grid.SongPosition, _grid.CursorRow);
    }

    private void StopPlayback()
    {
        var output = _output;
        _output = null;
        _engine = null;
        _poll.Stop();
        output?.Stop();
        output?.Dispose();
        if (output is not null)
            StopLiveRaster();
        _instrumentEditor.SetPlaybackCoverage(null);

        // A take ends with the song: a held key is let go where it got to, and the take becomes
        // one edit - one undo step.
        if (_daw.IsRecording)
            _daw.EndRecording();
        _grid.SetPlaybackActive(false);
        _daw.ClearPlayback();
        _play.Content = "▶ Play";
        PrepareJam();
    }

    private void RequestPlaybackFrames()
    {
        if (_framesRequested)
            return;
        _framesRequested = true;
        RequestAnimationFrame(OnPlaybackFrame);
    }

    /// <summary>
    /// Once per frame the display shows, while a song plays: the row events drained and the grid
    /// repainted, so the rows move on every frame and by the same amount. A timer cannot do this -
    /// Windows rounds its interval to a 15.6 ms tick, which never lines up with the display, and
    /// the scroll moved on some frames and not others: measured on a 140 Hz screen, the grid
    /// repainted at gaps of 13 to 21 ms and stood still for whole frames.
    /// </summary>
    private void OnPlaybackFrame(TimeSpan _)
    {
        _framesRequested = false;
        if (!_poll.IsEnabled)
            return;

        DrainPlaybackEvents();
        if (!_dawShowing)
            _grid.InvalidateVisual();
        RequestPlaybackFrames();
    }

    private void DrainPlaybackEvents()
    {
        if (_engine is not { } engine)
            return;

        while (engine.TryDequeueRowEvent(out var row))
            _grid.SetChannelPlaybackAnchor(row.Channel, row.PatternNumber, row.RowIndex, row.OrderEntrySequence, row.SampleIndex);

        if (_output is { } heard && engine.PositionAt(heard.SamplesPlayed, out _) is { } position)
            ShowLiveRaster(position.Frame);

        // The engine gathers which steps played frame by frame, so this poll's rate cannot miss
        // one; the playhead is taken against the device's play position, not the sequencer's.
        if (_output is { } playing && SelectedInstrument is { } shown)
        {
            engine.PlayheadAt(playing.SamplesPlayed, shown.Number, out int wave, out int arp, out int filter);
            _instrumentEditor.SetPlaybackCoverage(engine.CoverageFor(shown.Number), wave, arp, filter);
        }

        if (_dawShowing && _output is { } output)
            DrainToDaw(engine, output.SamplesPlayed);
    }

    /// <summary>The DAW's views follow the clock the listener hears, as in MainForm: the clips'
    /// playheads, the roll's row, the record window and the instrument page's program steps.</summary>
    private void DrainToDaw(SongPlaybackEngine engine, long heard)
    {
        if (engine.PositionAt(heard, out long capturedAt) is { Channels.Count: 3 } position)
        {
            _daw.SetPlayheads(
                position.Channels[0].PlayingEntry, position.Channels[1].PlayingEntry, position.Channels[2].PlayingEntry);

            double subFrame = (heard - capturedAt) / SongPlaybackEngine.SamplesPerFrame;
            int speed = position.TickSpeed(_session.Song);

            var at = new (int Pattern, int Row, double Fraction, double Ticks)[3];
            var lanes = new (int Entry, double Ticks)?[3];
            for (int ch = 0; ch < 3; ch++)
            {
                var channel = position.Channels[ch];
                var (row, fraction) = PlayheadPosition.Of(channel, position.SpeedCounter, speed, subFrame);
                double ticks = PlayheadPosition.TicksIntoEntry(channel, position.SpeedCounter, speed, subFrame);
                at[ch] = (channel.CurrentPatternNumber, row, fraction, ticks);
                lanes[ch] = (channel.PlayingEntry, ticks);
            }

            _daw.SetPlayRow(at[0], at[1], at[2]);
            _daw.SetSongPositions(lanes);

            if (_daw.IsRecording)
            {
                // Cycle took the song back from R to L: a punch-out and a punch-in at once.
                if (position.Frame < _lastHeardFrame)
                    _daw.SetRecordWindow(false);
                _daw.SetRecordWindow(position.Frame >= _recordWindow.In && position.Frame < _recordWindow.Out);
            }

            _lastHeardFrame = position.Frame;
        }

        if (_daw.SelectedInstrumentNumber is { } selected and > 0)
        {
            engine.PlayheadAt(heard, selected, out int wave, out int arp, out int filter);
            _daw.SetProgramPlayheads(wave, arp, filter);
            _daw.SetEnvelopePlayhead(engine.EnvelopeAt(heard, selected, out int stage, out int level) ? (stage, level) : null);
            _daw.SetCutoffPlayhead(engine.FilterAt(heard, selected, out int frame, out int cutoff) ? (frame, cutoff) : null);
        }
    }

    // ------------------------------------------------------------------------------------ MIDI

    private void BuildMidiMenu()
    {
        _midiMenu.Items.Clear();
        var devices = MidiInput.Devices();
        if (devices.Count == 0)
            _midiMenu.Items.Add(new MenuItem { Header = "No MIDI inputs found", IsEnabled = false });

        for (int i = 0; i < devices.Count; i++)
        {
            int index = i;
            var item = new MenuItem
            {
                Header = devices[i],
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _midi?.DeviceName == devices[i],
            };
            item.Click += (_, _) => OpenMidi(index, quiet: false);
            _midiMenu.Items.Add(item);
        }

        _midiMenu.Items.Add(new Separator());
        var close = new MenuItem { Header = "Close MIDI", IsEnabled = _midi?.IsOpen == true };
        close.Click += (_, _) => CloseMidi();
        _midiMenu.Items.Add(close);

        var forget = new MenuItem { Header = "Forget all knob bindings" };
        forget.Click += (_, _) =>
        {
            EditorSettings.MidiKnobBindings = string.Empty;
            _ = MessageWindow.ShowAsync(this, "MIDI", "Knob bindings cleared. They take effect next time MagSID starts.");
        };
        _midiMenu.Items.Add(forget);
    }

    /// <summary>Opens whatever was used last, by name - an input's place in the list moves when
    /// another is plugged in.</summary>
    private void ReopenMidi()
    {
        if (EditorSettings.MidiDevice is not { Length: > 0 } wanted)
            return;

        int index = MidiInput.Devices().ToList().FindIndex(name => name == wanted);
        if (index >= 0)
            OpenMidi(index, quiet: true);
    }

    private void OpenMidi(int index, bool quiet)
    {
        CloseMidi();

        var midi = new MidiInput(action => Dispatcher.UIThread.Post(action));
        if (!midi.Open(index))
        {
            midi.Dispose();
            if (!quiet)
                _ = MessageWindow.ShowAsync(this, "MIDI", "That MIDI input could not be opened. Another program usually has it.");
            return;
        }

        _midi = midi;
        EditorSettings.MidiDevice = midi.DeviceName ?? string.Empty;

        if (_midiBindings is null)
        {
            _midiBindings = new MidiKnobBindings(EditorSettings.MidiKnobBindings);
            _midiBindings.Changed += () => EditorSettings.MidiKnobBindings = _midiBindings.Save();
            foreach (var knob in _daw.Knobs())
                _midiBindings.Register(knob);
        }

        midi.ControlChanged += (controller, value) => _midiBindings.HandleControlChange(controller, value);
        midi.NotePressed += (note, _) => OnMidiNote(note);
        midi.NoteReleased += ReleaseDawNote;
        ShowNotice($"MIDI: {midi.DeviceName}");
    }

    private void CloseMidi()
    {
        _midi?.Dispose();
        _midi = null;
    }

    /// <summary>A key on the MIDI keyboard. It always sounds, and is written down only in Edit
    /// mode, in whichever view is on screen - see MainForm.OnMidiNote.</summary>
    private void OnMidiNote(NoteName note)
    {
        if (_dawShowing)
        {
            PlayDawNote(note);
            return;
        }

        // The tracker's own entry path decides whether Edit mode writes the note, and raises
        // NoteEntered, which already sounds it.
        _grid.EnterNote(note);
    }

    // ------------------------------------------------------------------------------------ optimize

    /// <summary>What every instrument's programs reach over one full pass of the song, played
    /// silently here - see MainForm.BuildInstrumentUsage.</summary>
    private Dictionary<int, MagSid.Core.InstrumentUsage> BuildInstrumentUsage()
    {
        var analyzed = SongCoverageAnalyzer.Run(_session.Song);
        var usage = new Dictionary<int, MagSid.Core.InstrumentUsage>();
        foreach (var instrument in _session.Song.Instruments)
        {
            if (instrument.Number < 0 || instrument.Number >= analyzed.Count)
                continue;

            var coverage = analyzed[instrument.Number];
            usage[instrument.Number] = new MagSid.Core.InstrumentUsage(
                HighestSet(coverage.Wavetable), HighestSet(coverage.Arpeggio), HighestSet(coverage.Filter));
        }

        return usage;

        static int HighestSet(bool[] reached) => Array.LastIndexOf(reached, true);
    }

    /// <summary>Shrinks the pattern data without changing what the song plays (SongOptimizer).
    /// Reports first and applies only on a yes; it lands on the undo stack like any edit.</summary>
    private async Task OptimizeAsync()
    {
        var usage = BuildInstrumentUsage();
        var result = MagSid.Core.SongOptimizer.Analyze(_session.Song, usage);
        string blank = Environment.NewLine + Environment.NewLine;

        if (!result.FoundAnything)
        {
            await MessageWindow.ShowAsync(this, "Optimize",
                "Nothing to change - the pattern data is already as small as loops can make it." + Environment.NewLine +
                "Every instrument's programs are fully used." + blank +
                $"Song length ${result.Before.EndAddress - 0x1000:X3}, patterns {result.Before.PatternBytes} bytes.");
            return;
        }

        var lines = result.Findings
            .Take(20)
            .Select(f => $"  PAT{f.PatternNumber:D2}  {f.Description}" + (f.BytesSaved > 0 ? $"  (-{f.BytesSaved} bytes)" : ""))
            .ToList();
        if (result.Findings.Count > lines.Count)
            lines.Add($"  ...and {result.Findings.Count - lines.Count} more");

        string summary = result.BytesSaved > 0
            ? $"Saves {result.BytesSaved} bytes: ${result.Before.EndAddress - 0x1000:X3} to ${result.After.EndAddress - 0x1000:X3}."
            : "Saves no bytes - a dead LS marker emits nothing - but the loops read as what they are.";
        const string caveat =
            "Program trims come from playing the whole song through silently, so they cover every " +
            "row of every pattern - no need to have listened to it first.";
        string undo = OperatingSystem.IsMacOS() ? "Cmd+Z" : "Ctrl+Z";

        if (!await ConfirmAsync("Optimize", summary, string.Join(Environment.NewLine, lines), caveat + blank + $"Apply? {undo} undoes it."))
            return;

        StopTransport();
        StopPatternPreview();
        MagSid.Core.SongOptimizer.Apply(_session.Song, usage);
        _session.NotifyStructureChanged();
    }

    /// <summary>A question with a monospaced list in the middle; true for Apply.</summary>
    private async Task<bool> ConfirmAsync(string title, string above, string list, string below)
    {
        var listing = new ScrollViewer
        {
            MaxHeight = 360,
            Content = new TextBlock { Text = list, FontFamily = Mono, Foreground = Body },
        };
        return await MessageWindow.ChooseAsync(this, title, above, ["Apply", "Cancel"], accept: 0, cancel: 1,
            below, listing, width: 600) == 0;
    }

    // ------------------------------------------------------------------------------------ files

    private static readonly FilePickerFileType SongFiles = new("MagSID songs") { Patterns = ["*.s"] };

    /// <summary>Before <paramref name="next"/> - "opening another song" - asks what becomes of
    /// unsaved changes, with buttons that say what each does. False to stay in the song.</summary>
    private async Task<bool> ConfirmDiscardAsync(string next)
    {
        if (!_session.IsDirty)
            return true;

        string name = _session.FilePath is { } path ? Path.GetFileName(path) : _session.Name ?? "the new song";
        var result = await AskAsync("Unsaved changes", $"Save the changes to {name} before {next}?");
        if (result is null)
            return false;
        return result == false || await SaveAsync(saveAs: false);
    }

    /// <summary>Makes a new song from a MIDI file - see MidiImportWindow for the choices and
    /// MidiImporter for how the notes are fitted to three voices.</summary>
    private async Task ImportMidiAsync()
    {
        if (!await ConfirmDiscardAsync("importing the MIDI file"))
            return;

        var options = new FilePickerOpenOptions
        {
            Title = "Import MIDI",
            FileTypeFilter = [new FilePickerFileType("MIDI files") { Patterns = ["*.mid", "*.midi"] }, FilePickerFileTypes.All],
        };
        options.SuggestedStartLocation = await StorageProvider.StartIn(DialogFolder.ImportMidi);

        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        EditorSettings.RememberFolder(DialogFolder.ImportMidi, path);

        MidiSong midi;
        try
        {
            midi = MidiFileReader.Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is MidiFileException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot read MIDI file", ex.Message);
            return;
        }

        if (midi.Sources().Count == 0)
        {
            await MessageWindow.ShowAsync(this, "Import MIDI", "The MIDI file has no notes.");
            return;
        }

        var result = await new MidiImportWindow(midi, Path.GetFileName(path), AuditionSound).ShowDialog<MidiImportResult?>(this);
        if (result is null)
            return;

        // The engines hold the old song; they stop before it is replaced.
        StopTransport();
        StopPatternPreview();
        _jam?.SilenceAll();
        _daw.ClearSoundingNotes();
        _session.LoadImported(result.Song, Path.GetFileNameWithoutExtension(path));
        _grid.SetSongPosition(0);
        ShowNotice($"Imported {Path.GetFileName(path)}: {result.Report.Bars} bars, {result.Report.TotalBytes} bytes");
    }

    /// <summary>Makes a new song from a .sid tune - see SidImportWindow for the choices and
    /// SidTranscriber for how what the tune does to the chip becomes notes and instruments.</summary>
    private async Task ImportSidAsync()
    {
        if (!await ConfirmDiscardAsync("importing the SID"))
            return;

        var options = new FilePickerOpenOptions
        {
            Title = "Import SID",
            FileTypeFilter = [new FilePickerFileType("SID tunes") { Patterns = ["*.sid"] }, FilePickerFileTypes.All],
        };
        options.SuggestedStartLocation = await StorageProvider.StartIn(DialogFolder.ImportSid);

        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        EditorSettings.RememberFolder(DialogFolder.ImportSid, path);

        SidFile file;
        try
        {
            file = SidFile.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is SidPlaybackException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot read .sid file", ex.Message);
            return;
        }

        if (file.UnsupportedReason() is { } reason)
        {
            await MessageWindow.ShowAsync(this, "Cannot import this .sid", reason);
            return;
        }

        // The engines hold the old song; they stop before it is replaced.
        StopTransport();
        StopPatternPreview();
        _jam?.SilenceAll();
        _daw.ClearSoundingNotes();

        var result = await new SidImportWindow(file, Path.GetFileName(path)).ShowDialog<SidImportResult?>(this);
        if (result is null)
            return;

        _session.LoadImported(result.Song, file.Name.Length > 0 ? file.Name : Path.GetFileNameWithoutExtension(path));
        _grid.SetSongPosition(0);
        ShowNotice($"Imported {Path.GetFileName(path)}: {result.Report.Rows} rows, {result.Report.TotalBytes} bytes");
    }

    private async Task NewAsync()
    {
        if (!await ConfirmDiscardAsync("starting a new song"))
            return;
        StopTransport();
        _session.New();
    }

    private async Task OpenAsync()
    {
        if (!await ConfirmDiscardAsync("opening another song"))
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open song",
            AllowMultiple = false,
            FileTypeFilter = [SongFiles, FilePickerFileTypes.All],
            SuggestedStartLocation = await StorageProvider.StartIn(DialogFolder.Open),
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            EditorSettings.RememberFolder(DialogFolder.Open, path);
            Open(path);
        }
    }

    private void Open(string path)
    {
        StopTransport();
        try
        {
            _session.Load(path);
            _grid.SetSongPosition(0);
        }
        catch (Exception ex)
        {
            _ = MessageWindow.ShowAsync(this, "Cannot open song", ex.Message);
        }
    }

    private async Task<bool> SaveAsync(bool saveAs)
    {
        string? path = _session.FilePath;
        if (saveAs || path is null)
        {
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save song",
                SuggestedFileName = (_session.Name ?? "Untitled") + ".s",
                DefaultExtension = "s",
                FileTypeChoices = [SongFiles],
                SuggestedStartLocation = await StorageProvider.StartIn(DialogFolder.SaveAs, _session.FilePath),
            });
            path = target?.TryGetLocalPath();
            if (path is null)
                return false;
            EditorSettings.RememberFolder(DialogFolder.SaveAs, path);
        }

        try
        {
            _session.Save(path);
            return true;
        }
        catch (Exception ex)
        {
            await MessageWindow.ShowAsync(this, "Cannot save song", ex.Message);
            return false;
        }
    }

    private bool _closeConfirmed;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_session.IsDirty)
            return;

        e.Cancel = true;
        if (await ConfirmDiscardAsync("closing MagSID"))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Yes, No or Cancel - null for Cancel.</summary>
    private async Task<bool?> AskAsync(string title, string question) =>
        await MessageWindow.ChooseAsync(this, title, question, ["Save", "Discard changes", "Keep editing"], accept: 0, cancel: 2) switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
}
