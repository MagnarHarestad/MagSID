using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Editor.App;
using MagSid.Editor.App.Export;
using MagSid.Sid;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// Menu → Export: the format, the load address, and for a .sid its header's name, author and release
/// line. The address is checked as it is typed against the song's real size - the same rules as the
/// Windows editor's export dialog (SidLoadAddress). The file itself comes from SidExporter, which
/// builds the C64 image in C# - no assembler needed, on any system.
/// </summary>
internal sealed class SidExportWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    private static readonly IBrush Warning = new SolidColorBrush(AvColor.FromRgb(0xFF, 0xB0, 0x5C));

    private readonly Song _song;
    private readonly ChipModel _chip;
    private readonly int _bytes;
    private readonly ComboBox _format = new()
    {
        ItemsSource = new[] { "SID tune (.sid)", "C64 program (.prg)", "Raw binary (.bin)" },
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBox _name = new();
    private readonly TextBox _author = new();
    private readonly TextBox _released = new();
    private readonly TextBox _address = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
    private readonly TextBlock _range = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _about = new() { TextWrapping = TextWrapping.Wrap, Foreground = Dim };
    private readonly TextBlock _size = new() { Foreground = Dim };
    private readonly Control[] _sidOnly;
    private readonly string _zeroPage;
    private readonly Button _export = new() { Content = "Export...", MinWidth = 90 };

    public SidExportWindow(Song song, string name, ChipModel chip)
    {
        _song = song;
        _chip = chip;
        _bytes = SongSizeCalculator.Measure(song.WithUnusedPatternsRemoved()).TotalBytes;

        var (zpFirst, zpLast) = SidExporter.ZeroPageRange(song);
        _zeroPage = $"zero page ${zpFirst:X2}-${zpLast:X2}";

        Title = "Export";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = name;
        _author.Text = EditorSettings.SidAuthor;
        _released.Text = $"{DateTime.Now.Year} {EditorSettings.SidAuthor}".Trim();
        _address.Text = $"${EditorSettings.SidLoadAddress:X4}";
        _address.TextChanged += (_, _) => CheckAddress();

        _format.SelectedIndex = (int)EditorSettings.ExportFormat;
        _format.SelectionChanged += (_, _) => ShowFormat();

        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close();
        _export.Click += async (_, _) => await ExportAsync();

        _sidOnly = [Labelled("Name", _name), Labelled("Author", _author), Labelled("Released", _released)];

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                Labelled("Format", _format),
                _sidOnly[0],
                _sidOnly[1],
                _sidOnly[2],
                Labelled("Load address", _address),
                _range,
                _about,
                _size,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { _export, cancel },
                },
            },
        };

        ShowFormat();
    }

    private SongExportFormat Format => (SongExportFormat)Math.Max(0, _format.SelectedIndex);

    /// <summary>Only a .sid has a header to fill in; the other two are for a coder, who needs to
    /// know how to call it.</summary>
    private void ShowFormat()
    {
        foreach (var row in _sidOnly)
            row.IsVisible = Format == SongExportFormat.Sid;

        _about.Text = Format switch
        {
            SongExportFormat.Prg => "The load address comes first in the file. To play it from your own code, call init once and play once a frame; it does not start by itself with RUN.",
            SongExportFormat.Binary => "Nothing in the file says where it goes: load it at this address, or it will not run. Call init once and play once a frame.",
            _ => "With a header, for SID players.",
        };
        _size.Text = Format switch
        {
            SongExportFormat.Prg => $"{_bytes} bytes with the player; the file is {_bytes + 2}.",
            SongExportFormat.Binary => $"{_bytes} bytes with the player.",
            // Only a .sid says which chip it is for: its header does.
            _ => $"{_bytes} bytes with the player, for the {(_chip == ChipModel.Mos8580 ? "8580" : "6581")}.",
        };
        CheckAddress();
    }

    private static Control Labelled(string label, Control field)
    {
        var row = new DockPanel();
        var caption = new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim };
        DockPanel.SetDock(caption, Dock.Left);
        row.Children.Add(caption);
        row.Children.Add(field);
        return row;
    }

    private int? CheckAddress()
    {
        string? problem = SidLoadAddress.TryParse(_address.Text) is { } address
            ? SidLoadAddress.Validate(address, _bytes)
            : "Write the address in hex, as $1000.";

        if (problem is null && SidLoadAddress.TryParse(_address.Text) is { } ok)
        {
            _range.Text = $"Loads at ${ok:X4}-${ok + _bytes - 1:X4}; init ${ok:X4}, play ${ok + 3:X4}; {_zeroPage}.";
            _range.Foreground = Dim;
            _export.IsEnabled = true;
            return ok;
        }

        _range.Text = problem;
        _range.Foreground = Warning;
        _export.IsEnabled = false;
        return null;
    }

    private async Task ExportAsync()
    {
        if (CheckAddress() is not { } address)
            return;

        var format = Format;
        var (extension, kind) = format switch
        {
            SongExportFormat.Prg => ("prg", "C64 programs"),
            SongExportFormat.Binary => ("bin", "Raw binaries"),
            _ => ("sid", "SID tunes"),
        };

        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to ." + extension,
            SuggestedFileName = (string.IsNullOrWhiteSpace(_name.Text) ? "Untitled" : _name.Text.Trim()) + "." + extension,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(kind) { Patterns = ["*." + extension] }],
            SuggestedStartLocation = await StorageProvider.StartIn(DialogFolder.ExportSid),
        });
        if (target?.TryGetLocalPath() is not { } path)
            return;

        EditorSettings.RememberFolder(DialogFolder.ExportSid, path);

        try
        {
            byte[] bytes;
            switch (format)
            {
                case SongExportFormat.Prg:
                    bytes = SidExporter.ExportPrg(_song, address);
                    break;
                case SongExportFormat.Binary:
                    bytes = SidExporter.ExportBinary(_song, address);
                    break;
                default:
                    var metadata = new SidExporter.Metadata(_name.Text ?? "", _author.Text ?? "", _released.Text ?? "");
                    bytes = SidExporter.Export(_song, metadata, _chip, address);
                    EditorSettings.SidAuthor = _author.Text ?? "";
                    break;
            }

            File.WriteAllBytes(path, bytes);
            EditorSettings.SidLoadAddress = address;
            EditorSettings.ExportFormat = format;
            Close();
        }
        catch (Exception ex) when (ex is SidExportException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot export", ex.Message);
        }
    }
}
