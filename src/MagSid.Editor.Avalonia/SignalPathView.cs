using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MagSid.Core.Model;
using MagSid.Editor.App;
using MagSid.Playback;
using MagSid.Sid;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The Signal Path view, in the main window in place of the song: pick an instrument, a note and
/// how many frames, and Trace takes a <see cref="SignalPathTrace"/> of it - register writes, gate,
/// envelope, filter, oscillator and output, frame by frame, out of the real sequencer and SID -
/// draws it on the shared <see cref="SignalPathCanvas"/> and plays it while a marker follows.
/// </summary>
internal sealed class SignalPathView : DockPanel
{
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));

    private List<Instrument> _instruments = [];
    private readonly ComboBox _instrumentPicker = new() { Width = 220 };
    private readonly ComboBox _notePicker = new() { Width = 90 };
    private readonly NumericUpDown _frameCount = new() { Minimum = 4, Maximum = 200, Value = 24, Width = 110, FormatString = "0" };
    private readonly TextBlock _summary = new() { Foreground = Dim, Margin = new Thickness(8, 4) };
    private readonly SignalPathCanvas _canvas = new();
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };

    // Trace plays what it draws: the song engine on the one-note song the trace was taken from,
    // so a drum moving through noise into pulse sounds as the picture beside it shows.
    private IAudioOutput? _audition;
    private SongPlaybackEngine? _auditionEngine;
    private readonly DispatcherTimer _auditionStop = new();
    private readonly DispatcherTimer _playhead = new() { Interval = TimeSpan.FromMilliseconds(15) };

    public SignalPathView()
    {
        var notes = NoteName.AllNotes.Select(n => n.ToString()).ToList();
        _notePicker.ItemsSource = notes;
        _notePicker.SelectedIndex = Math.Max(0, notes.IndexOf("C-3"));

        var trace = new Button { Content = "Trace", Width = 90, VerticalAlignment = VerticalAlignment.Bottom };
        trace.Click += (_, _) => RunTrace(play: true);
        _auditionStop.Tick += (_, _) => StopAudition();
        _playhead.Tick += (_, _) => UpdatePlayhead();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(8, 6, 8, 0),
            Children =
            {
                Labelled("Instrument", _instrumentPicker),
                Labelled("Note", _notePicker),
                Labelled("Frames", _frameCount),
                trace,
            },
        };

        _scroll.Content = _canvas;
        SetDock(toolbar, Dock.Top);
        SetDock(_summary, Dock.Top);
        Children.Add(toolbar);
        Children.Add(_summary);
        Children.Add(_scroll);
    }

    /// <summary>Lists the song's instruments, picks <paramref name="number"/> when it is one of
    /// them, and traces it - silently: sound on open startles, and nobody asked for it by
    /// getting here.</summary>
    public void ShowFor(IReadOnlyList<Instrument> instruments, int? number)
    {
        StopAudition();
        var was = _instrumentPicker.SelectedIndex >= 0 && _instrumentPicker.SelectedIndex < _instruments.Count
            ? _instruments[_instrumentPicker.SelectedIndex].Number
            : (int?)null;
        _instruments = instruments.ToList();
        _instrumentPicker.ItemsSource = _instruments.Select(SignalPathTrace.Label).ToList();

        int index = _instruments.FindIndex(i => i.Number == (number ?? was));
        _instrumentPicker.SelectedIndex = index >= 0 ? index : _instruments.Count > 0 ? 0 : -1;
        if (_instruments.Count > 0)
            RunTrace(play: false);
        else
            _summary.Text = "The song has no instruments to trace.";
    }

    private static Control Labelled(string caption, Control field) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = caption, Foreground = Dim }, field },
    };

    private void RunTrace(bool play)
    {
        if (_instrumentPicker.SelectedIndex < 0 || _notePicker.SelectedIndex < 0)
            return;

        var instrument = _instruments[_instrumentPicker.SelectedIndex];
        var note = NoteName.AllNotes[_notePicker.SelectedIndex];
        int frameCount = (int)(_frameCount.Value ?? 24);

        var trace = SignalPathTrace.Capture(instrument, note, frameCount);
        _summary.Text = trace.Summary;
        _canvas.ShowTrace(trace);

        // Started once the trace is on screen: tracing blocks for a good fraction of a second,
        // and sound started first had the marker a third of the way along when it first showed.
        if (play)
            Dispatcher.UIThread.Post(() => PlayAudition(trace.Song, frameCount), DispatcherPriority.Background);
    }

    /// <summary>Sounds the traced note. Does nothing if the sound card is unavailable - a trace is
    /// still perfectly usable without audio.</summary>
    private void PlayAudition(Song song, int frameCount)
    {
        StopAudition();
        try
        {
            _auditionEngine = new SongPlaybackEngine(song, ChipModel.Mos8580);
            _audition = AudioOutput.Open!(_auditionEngine, 150);
            _audition.Play();

            _auditionStop.Interval = TimeSpan.FromMilliseconds(SignalPathTrace.AuditionMilliseconds(frameCount));
            _auditionStop.Start();
            _canvas.PlayheadFrame = 0;
            _playhead.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException or ArgumentException)
        {
            StopAudition();
        }
    }

    public void StopAudition()
    {
        _auditionStop.Stop();
        _playhead.Stop();
        _canvas.PlayheadFrame = null;
        var audition = _audition;
        _audition = null;
        _auditionEngine = null;
        audition?.Stop();
        audition?.Dispose();
    }

    /// <summary>Moves the marker to the frame being heard - the device's play position, not what
    /// has been handed to it - and keeps it in view once the trace is wider than the view.</summary>
    private void UpdatePlayhead()
    {
        if (_audition is null || _canvas.Trace is not { } trace)
            return;

        double frame = _audition.SamplesPlayed / (double)SongPlaybackEngine.SampleRate * SignalPathTrace.FrameRate;

        // Parked on the last traced frame: the audition runs on for the release tail, and the
        // marker would otherwise carry on into blank canvas past the picture's right edge.
        _canvas.PlayheadFrame = Math.Min(frame, trace.Frames.Count - 1);

        const int frameWidth = SignalPathCanvas.FrameWidth;
        double x = SignalPathCanvas.XOfFrame(frame);
        double scrollX = _scroll.Offset.X;
        double visible = _scroll.Viewport.Width;
        if (x > scrollX + visible - frameWidth * 2)
        {
            double target = Math.Max(0, Math.Min(x - visible + frameWidth * 4, Math.Max(0, _canvas.Width - visible)));
            _scroll.Offset = new Vector(target, _scroll.Offset.Y);
        }
        else if (x < scrollX)
        {
            _scroll.Offset = new Vector(Math.Max(0, x - frameWidth), _scroll.Offset.Y);
        }
    }
}
