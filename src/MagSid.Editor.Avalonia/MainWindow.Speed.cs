using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The song's speed on the toolbar: one button, "Speed 6" or "Speed 6/4 funk", whose drop-down
/// sets the speed and the funk speed and shows what they do - how long each row lasts, drawn,
/// and the tempo that gives. The mouse wheel on the button steps the speed without opening it.
/// </summary>
/// <remarks>
/// Shown as frames a row, as GoatTracker's tempo is: speed 6 is six frames. The song and the
/// player keep one less (<see cref="MagSid.Core.Model.Song.Speed"/> 5), which is what the player counts down from,
/// so files and the player are as they always were.
/// </remarks>
public sealed partial class MainWindow
{
    private const int MaxFrames = 32;

    private readonly Button _speedButton = new();
    private readonly NumericUpDown _speed = Spinner();
    private readonly CheckBox _funkOn = new() { Content = "Funk - every second row at another speed" };

    // Kept while funk is off, so turning it back on returns to the speed it had.
    private readonly NumericUpDown _funkSpeed = Spinner();
    private readonly TextBlock _speedSummary = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 330 };
    private readonly RowLengthsView _rowLengths = new();

    private static NumericUpDown Spinner() =>
        new() { Minimum = 1, Maximum = MaxFrames, Width = 64, FormatString = "0" };

    private Control BuildSpeedButton()
    {
        _speedButton.Classes.Add("strip");
        ToolTip.SetTip(_speedButton, "Song speed: click to set it and funk, or scroll to change it");
        _speedButton.PointerWheelChanged += (_, e) =>
        {
            _speed.Value = Math.Clamp((int)(_speed.Value ?? 7) + (e.Delta.Y > 0 ? 1 : -1), 1, MaxFrames);
            e.Handled = true;
        };

        _speed.ValueChanged += (_, _) => ApplySpeed();
        _funkSpeed.ValueChanged += (_, _) => ApplySpeed();
        _funkOn.IsCheckedChanged += (_, _) =>
        {
            _funkSpeed.IsEnabled = _funkOn.IsChecked == true;
            ApplySpeed();
        };

        var speedRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { FlyoutLabel("Speed"), _speed, Hint("frames a row, as GoatTracker's tempo") },
        };
        var funkRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(22, 0, 0, 0),
            Children = { FlyoutLabel("Funk speed"), _funkSpeed, Hint("6 bytes in the player") },
        };
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                speedRow,
                _funkOn,
                funkRow,
                _rowLengths,
                _speedSummary,
            },
        };
        _speedSummary.Foreground = EditorTheme.Subtitle;

        _speedButton.Flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new Border { Padding = new Thickness(4), Child = panel },
        };
        _speedButton.Flyout.Closed += (_, _) => _grid.Focus();
        return _speedButton;
    }

    private static TextBlock FlyoutLabel(string text) => new()
    {
        Text = text,
        Width = 72,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Body,
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = EditorTheme.Subtitle,
    };

    /// <summary>The fields, in frames, into the song, one less. Funk at the same speed as the
    /// rest is no funk, and is stored as none.</summary>
    private void ApplySpeed()
    {
        if (_refreshing || _speed.Value is not { } frames)
            return;

        var song = _session.Song;
        int speed = (int)frames - 1;
        int? funk = _funkOn.IsChecked == true && _funkSpeed.Value is { } f && (int)f - 1 != speed ? (int)f - 1 : null;
        if (song.Speed != speed || song.FunkSpeed != funk)
        {
            song.Speed = speed;
            song.FunkSpeed = funk;
            _session.MarkDirty();
        }
        ShowSpeed();
        RefreshStatus();
    }

    /// <summary>The song's speed into the fields - when a song is opened, or undo restores one.</summary>
    private void LoadSpeed()
    {
        var song = _session.Song;
        int frames = song.Speed + 1;
        _speed.Value = frames;
        _funkOn.IsChecked = song.HasFunk;
        _funkSpeed.IsEnabled = song.HasFunk;
        // A first funk speed two either side of the speed: a swing that is plainly heard.
        _funkSpeed.Value = song.HasFunk ? song.FunkSpeed + 1 : frames > 2 ? frames - 2 : frames + 2;
        ShowSpeed();
    }

    private void ShowSpeed()
    {
        var song = _session.Song;
        _speedButton.Content = song.HasFunk ? $"Speed {song.Speed + 1}/{song.FunkSpeed + 1} funk ▾" : $"Speed {song.Speed + 1} ▾";
        _rowLengths.Show(song.Speed + 1, song.HasFunk ? song.FunkSpeed!.Value + 1 : song.Speed + 1);

        // PAL: 50 frames a second, and a beat taken as four rows.
        string bpm = (50.0 * 60 / (song.AverageFramesPerRow * 4)).ToString("0", CultureInfo.InvariantCulture);
        _speedSummary.Text = song.HasFunk
            ? $"Rows alternate {song.Speed + 1} and {song.FunkSpeed + 1} frames long, from the song's first row - " +
              $"a swing. About {bpm} BPM at four rows a beat."
            : $"Every row lasts {song.Speed + 1} frames. About {bpm} BPM at four rows a beat.";
    }

    /// <summary>Eight rows drawn to length, so a swing can be seen: the speed's rows and the funk
    /// speed's in two colours, each with its frame count.</summary>
    private sealed class RowLengthsView : Control
    {
        private const int Rows = 8;
        private const double RowHeight = 22;
        private static readonly IBrush Even = new SolidColorBrush(Color.FromRgb(0x2E, 0x4A, 0x78));
        private static readonly IBrush Odd = new SolidColorBrush(Color.FromRgb(0x5A, 0x3A, 0x78));
        private static readonly Typeface Face = new("Segoe UI, Inter");
        private int _even = 7, _odd = 7;

        public RowLengthsView()
        {
            Height = RowHeight;
            Width = 330;
            HorizontalAlignment = HorizontalAlignment.Left;
        }

        public void Show(int evenFrames, int oddFrames)
        {
            (_even, _odd) = (evenFrames, oddFrames);
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            // Scaled so the eight rows fill the width: what shows is how long rows are to each other.
            double frameWidth = Bounds.Width / (Rows / 2 * (double)(_even + _odd));
            double x = 0;
            for (int row = 0; row < Rows; row++)
            {
                int frames = row % 2 == 0 ? _even : _odd;
                var box = new Rect(x, 0, Math.Max(1, frames * frameWidth - 2), RowHeight);
                context.FillRectangle(_even != _odd && row % 2 == 1 ? Odd : Even, box);
                var label = new FormattedText(frames.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Face, 11, EditorTheme.Body);
                if (label.Width < box.Width - 2)
                    context.DrawText(label, new Point(box.X + (box.Width - label.Width) / 2, (RowHeight - label.Height) / 2));
                x += frames * frameWidth;
            }
        }
    }
}
