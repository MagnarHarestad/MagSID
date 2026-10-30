using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Core.Validation;
using MagSid.Editor.App;
using MagSid.Editor.App.Controls;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The Retro view's instrument list, as the Windows editor's InstrumentListControl and its header:
/// a table of all 16 slots - number, name and the programs it carries - with how many are in use;
/// add, and a menu of the other ways to add - a sound of the bank, a file - with changing the
/// selected instrument's sound, duplicating it and saving it to a file, and the bank folder;
/// duplicate and delete. A "+ New instrument" row stands in the first empty slot, Insert adds and
/// Delete deletes, a right-click offers the same, a drag up or down moves an instrument to another
/// number, and the name is typed in the list - F2, a double-click, or just typing.
/// </summary>
public sealed partial class MainWindow
{
    private const string NewInstrumentText = "+ New instrument";

    /// <summary>1.64's DataGridView: its header row, its rows, and the columns' shares of the width.</summary>
    private const double InstrumentHeaderHeight = 23;
    private const double InstrumentRowHeight = 25;
    private const string InstrumentColumns = "50*,200*,100*";

    private readonly ListBox _instruments = new();
    private readonly TextBlock _instrumentCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _addInstrument = IconButton("", "+");
    private readonly Button _moreInstrument = IconButton("", "v", width: 16);
    private readonly Button _duplicateInstrument = IconButton("", "Dup");
    private readonly Button _deleteInstrument = IconButton("", "Del");
    private readonly TextBox _rename = new() { IsVisible = false, Height = InstrumentRowHeight };
    private Instrument? _renaming;

    /// <summary>The list being refilled: the selection changes that makes are not the user's.</summary>
    private bool _listing;

    /// <summary>The row a drag to reorder is at, -1 when none is under way.</summary>
    private int _dragIndex = -1;

    /// <summary>One slot of the table. Row i is always slot i + 1, whether or not it holds an
    /// instrument, so the numbers shown count the instruments in use at a glance.</summary>
    private sealed record InstrumentSlot(string Number, string Name, string Programs, IBrush Ink);

    private Control BuildInstrumentListHeader()
    {
        ToolTip.SetTip(_addInstrument, "New instrument (Ins)");
        ToolTip.SetTip(_moreInstrument, "More ways to add: from the sound bank, from a file, a duplicate");
        ToolTip.SetTip(_duplicateInstrument, "Duplicate the selected instrument");
        ToolTip.SetTip(_deleteInstrument, "Delete the selected instrument (Del)");
        _addInstrument.Click += (_, _) => AddInstrument();
        _duplicateInstrument.Click += (_, _) => DuplicateInstrument();
        _deleteInstrument.Click += (_, _) => DeleteInstrument();

        // Filled as it opens, so files dropped into the bank folder show up without a restart.
        var more = new MenuFlyout();
        more.Opening += (_, _) => FillInstrumentMenu(more.Items);
        _moreInstrument.Flyout = more;

        _instrumentCount.Foreground = EditorTheme.Subtitle;
        _instrumentCount.Margin = new Thickness(0, 0, 8, 0);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _instrumentCount, _addInstrument, _moreInstrument, _duplicateInstrument, _deleteInstrument },
        };
        var title = new TextBlock
        {
            Text = "Instruments",
            Foreground = EditorTheme.Heading,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        var header = new DockPanel { Height = 28, Background = EditorTheme.Background };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);
        return header;
    }

    /// <summary>The header's flat icon buttons: Windows' own icon font where there is one, as in
    /// 1.64, and short words elsewhere.</summary>
    private static Button IconButton(string glyph, string fallback, double width = 24)
    {
        bool icons = OperatingSystem.IsWindows();
        return new Button
        {
            Content = icons ? glyph : fallback,
            FontFamily = icons ? new FontFamily("Segoe MDL2 Assets") : EditorTheme.UiFont,
            FontSize = icons ? 12 : 11,
            Width = icons ? width : Math.Max(width, 30),
            Height = 22,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = EditorTheme.Heading,
            Classes = { "strip" },
            Focusable = false,
        };
    }

    /// <summary>The table: its header row, the 16 slots under it, and the box a name is typed in,
    /// laid over the Name cell of the row being renamed.</summary>
    private Control BuildInstrumentTable()
    {
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(InstrumentColumns), Height = InstrumentHeaderHeight, Background = EditorTheme.Field };
        string[] headings = ["#", "Name", "Prog."];
        for (int column = 0; column < headings.Length; column++)
        {
            var cell = new Border
            {
                BorderBrush = EditorTheme.Border,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = headings[column],
                    Foreground = EditorTheme.Heading,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                },
            };
            Grid.SetColumn(cell, column);
            header.Children.Add(cell);
        }

        _instruments.Background = EditorTheme.Background;
        _instruments.Padding = new Thickness(0);
        _instruments.Height = InstrumentRowHeight * SongValidator.MaxInstrumentNumber;
        ScrollViewer.SetVerticalScrollBarVisibility(_instruments, global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _instruments.ItemTemplate = new FuncDataTemplate<InstrumentSlot?>((slot, _) =>
        {
            // A row being taken away is shown nothing on its way out.
            if (slot is null)
                return new Panel();

            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(InstrumentColumns), Height = InstrumentRowHeight };
            string[] cells = [slot.Number, slot.Name, slot.Programs];
            for (int column = 0; column < cells.Length; column++)
            {
                var text = new TextBlock
                {
                    Text = cells[column],
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                // Occupied rows take the item's own ink, so a selected row turns the selection's
                // colour; empty ones are dimmer, and the "+ New instrument" slot is the accent.
                if (!ReferenceEquals(slot.Ink, EditorTheme.Body))
                    text.Foreground = slot.Ink;

                var cell = new Border
                {
                    BorderBrush = EditorTheme.Button,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = text,
                };
                Grid.SetColumn(cell, column);
                row.Children.Add(cell);
            }
            return row;
        }, supportsRecycling: false);

        _instruments.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(MinHeightProperty, 0.0),
                new Setter(HeightProperty, InstrumentRowHeight),
                new Setter(ForegroundProperty, EditorTheme.Body),
            },
        });
        foreach (string state in new[] { ":pointerover", ":pressed" })
        {
            _instruments.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(state).Template().OfType<ContentPresenter>())
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
        }
        foreach (string state in new[] { "", ":pointerover", ":pressed", ":focus" })
        {
            var selector = state.Length == 0
                ? (Func<Selector?, Selector>)(x => x.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
                : x => x.OfType<ListBoxItem>().Class(":selected").Class(state).Template().OfType<ContentPresenter>();
            _instruments.Styles.Add(new Style(selector)
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, EditorTheme.Selection),
                    new Setter(ContentPresenter.ForegroundProperty, EditorTheme.Title),
                },
            });
        }

        // Typed over the Name cell; Enter or leaving keeps it, Escape puts the old name back.
        _rename.VerticalAlignment = VerticalAlignment.Top;
        _rename.FontFamily = EditorTheme.UiFont;
        _rename.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                EndRename(keep: e.Key == Key.Enter);
                e.Handled = true;
            }
        };
        _rename.LostFocus += (_, _) => EndRename(keep: true);
        var renameLayer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(InstrumentColumns) };
        Grid.SetColumn(_rename, 1);
        renameLayer.Children.Add(_rename);

        var table = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(_instruments);
        return new Panel { Children = { table, renameLayer } };
    }

    /// <summary>Opens a slot's name for typing - what follows adding one, so it can be named
    /// straight away; F2 and a double-click do the same. A first letter typed replaces the name.</summary>
    private void BeginRename(int number, string? typed = null)
    {
        if (_session.Song.Instruments.FirstOrDefault(i => i.Number == number) is not { } instrument)
            return;

        _renaming = instrument;
        _rename.Text = typed ?? instrument.Name;
        _rename.Margin = new Thickness(0, InstrumentHeaderHeight + (number - 1) * InstrumentRowHeight, 0, 0);
        _rename.IsVisible = true;
        _rename.Focus();
        if (typed is null)
            _rename.SelectAll();
        else
            _rename.CaretIndex = typed.Length;
    }

    private void EndRename(bool keep)
    {
        if (_renaming is not { } instrument)
            return;

        _renaming = null;
        _rename.IsVisible = false;
        string name = (_rename.Text ?? "").Trim();
        if (keep && name != instrument.Name)
        {
            instrument.Name = name;
            _session.MarkDirty();
            RefreshInstrumentNames();
        }
        _instruments.Focus();
    }

    private void WireInstrumentList()
    {
        _instruments.SelectionChanged += (_, _) =>
        {
            if (_listing)
                return;

            ShowSelectedInstrument();
            UpdateInstrumentButtons();
        };

        _instruments.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Insert)
            {
                AddInstrument();
                e.Handled = true;
            }
            else if (e.Key is Key.Delete && SelectedInstrument is not null)
            {
                DeleteInstrument();
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && SelectedInstrument is { } instrument)
            {
                BeginRename(instrument.Number);
                e.Handled = true;
            }
        };

        // Typing on a selected instrument names it, as in 1.64's table.
        _instruments.TextInput += (_, e) =>
        {
            if (SelectedInstrument is { } instrument && !string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]))
            {
                BeginRename(instrument.Number, e.Text);
                e.Handled = true;
            }
        };
        _instruments.DoubleTapped += (_, e) =>
        {
            if (InstrumentIndexAt(e) is var index and >= 0 && InstrumentInSlot(index) is { } instrument)
                BeginRename(instrument.Number);
        };

        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            bool selected = SelectedInstrument is not null;
            menu.ItemsSource = new Control[]
            {
                MenuEntry("Add", AddInstrument, !InstrumentsFull),
                MenuEntry("Copy", DuplicateInstrument, selected && !InstrumentsFull),
                MenuEntry("Delete", DeleteInstrument, selected),
            };
        };
        _instruments.ContextMenu = menu;

        // A right-click picks the row under it first, so Delete acts on what was clicked; a left
        // press on the "+ New instrument" slot adds one, and on an instrument arms the drag that
        // moves it.
        _instruments.AddHandler(PointerPressedEvent, (_, e) =>
        {
            int index = InstrumentIndexAt(e);
            var point = e.GetCurrentPoint(_instruments);
            if (point.Properties.IsRightButtonPressed && index >= 0)
                _instruments.SelectedIndex = index;

            // Posted, so the list is done with its own click before it is refilled.
            if (point.Properties.IsLeftButtonPressed && index >= 0 && index == NewInstrumentRow && !InstrumentsFull)
                Dispatcher.UIThread.Post(AddInstrument);

            _dragIndex = point.Properties.IsLeftButtonPressed && InstrumentInSlot(index) is not null ? index : -1;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // A move each time the drag crosses into another row, not each pixel; the list is
        // refilled after every move, and the next row is found again by position.
        _instruments.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_dragIndex < 0)
                return;
            if (!e.GetCurrentPoint(_instruments).Properties.IsLeftButtonPressed)
            {
                _dragIndex = -1;
                return;
            }

            int index = InstrumentIndexAt(e);
            if (index < 0 || index == _dragIndex || InstrumentInSlot(_dragIndex) is not { } dragged)
                return;

            _session.MoveInstrument(dragged, index + 1);
            SelectInstrumentNumber(dragged.Number);
            _dragIndex = index;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _instruments.AddHandler(PointerReleasedEvent, (_, _) => _dragIndex = -1, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The row under the pointer, or -1.</summary>
    private int InstrumentIndexAt(RoutedEventArgs e)
    {
        var at = e switch
        {
            PointerEventArgs pointer => pointer.GetPosition(_instruments),
            TappedEventArgs tap => tap.GetPosition(_instruments),
            _ => default(Point?),
        };
        if (at is not { } point)
            return -1;

        var hit = _instruments.InputHitTest(point) as Visual;
        var item = hit as ListBoxItem ?? hit?.FindAncestorOfType<ListBoxItem>();
        return item is null ? -1 : _instruments.IndexFromContainer(item);
    }

    /// <summary>The instrument in a row - slot row + 1 - or null for an empty one.</summary>
    private Instrument? InstrumentInSlot(int row) =>
        row < 0 ? null : _session.Song.Instruments.FirstOrDefault(i => i.Number == row + 1);

    /// <summary>The first empty slot, which offers "+ New instrument"; -1 when all 16 are used.</summary>
    private int NewInstrumentRow
    {
        get
        {
            for (int row = 0; row < SongValidator.MaxInstrumentNumber; row++)
            {
                if (InstrumentInSlot(row) is null)
                    return row;
            }
            return -1;
        }
    }

    private bool InstrumentsFull => _session.Song.Instruments.Count >= SongValidator.MaxInstrumentNumber;

    private void RefreshInstrumentNames()
    {
        _listing = true;
        try
        {
            int selected = _instruments.SelectedIndex;
            int newRow = NewInstrumentRow;
            var rows = new List<InstrumentSlot>(SongValidator.MaxInstrumentNumber);
            for (int row = 0; row < SongValidator.MaxInstrumentNumber; row++)
            {
                rows.Add(InstrumentInSlot(row) is { } instrument
                    ? new InstrumentSlot($"{instrument.Number:00}", InstrumentNames.Display(instrument), DescribePrograms(instrument), EditorTheme.Body)
                    : new InstrumentSlot("", row == newRow ? NewInstrumentText : "", "", row == newRow ? EditorTheme.Title : EditorTheme.Subtitle));
            }
            _instruments.ItemsSource = rows;

            // The same slot as before, or the first instrument when nothing was.
            _instruments.SelectedIndex = selected >= 0 ? selected
                : _session.Song.Instruments.Count > 0 ? _session.Song.Instruments.Min(i => i.Number) - 1
                : -1;
        }
        finally
        {
            _listing = false;
        }

        UpdateInstrumentButtons();
    }

    /// <summary>The programs an instrument carries - an arpeggio or a filter sweep is otherwise
    /// invisible until the instrument is opened - or &lt;Edit&gt; when it has none.</summary>
    private static string DescribePrograms(Instrument instrument)
    {
        var programs = new List<string>(3);
        if (instrument.Arpeggio.Count > 0)
            programs.Add($"ARP{instrument.Arpeggio.Count}");
        if (instrument.Filter.Count > 0)
            programs.Add($"FLT{instrument.Filter.Count}");
        if (!instrument.HasDefaultPulse)
            programs.Add("PW");
        return programs.Count > 0 ? string.Join(" ", programs) : "<Edit>";
    }

    private void UpdateInstrumentButtons()
    {
        int max = SongValidator.MaxInstrumentNumber;
        bool selected = SelectedInstrument is not null;
        _instrumentCount.Text = $"{_session.Song.Instruments.Count}/{max}";
        _addInstrument.IsEnabled = !InstrumentsFull;
        _duplicateInstrument.IsEnabled = selected && !InstrumentsFull;
        _deleteInstrument.IsEnabled = selected;
        ToolTip.SetTip(_addInstrument, InstrumentsFull
            ? $"All {max} instruments are in use - a pattern names one with a single byte"
            : "New instrument (Ins)");
    }

    private void FillInstrumentMenu(ItemCollection items)
    {
        bool selected = SelectedInstrument is not null;
        var fromBank = new MenuItem { Header = "From sound _bank", IsEnabled = !InstrumentsFull };
        foreach (var entry in BankEntries(AddFromBank))
            fromBank.Items.Add(entry);
        var change = new MenuItem { Header = "_Change sound of selected", IsEnabled = selected };
        foreach (var entry in BankEntries(sound =>
                 {
                     if (SelectedInstrument is { } target)
                         ChangeSound(target, sound);
                 }))
            change.Items.Add(entry);

        items.Clear();
        items.Add(MenuEntry("_New instrument", AddInstrument, !InstrumentsFull));
        items.Add(fromBank);
        items.Add(MenuEntry("_Load from file...", () => _ = LoadInstrumentsAsync(), !InstrumentsFull));
        items.Add(MenuEntry("_Save selected to file...", () => _ = SaveInstrumentAsync(), selected));
        items.Add(new Separator());
        items.Add(change);
        items.Add(MenuEntry("_Duplicate selected", DuplicateInstrument, selected && !InstrumentsFull));
        items.Add(new Separator());
        items.Add(MenuEntry("Open bank _folder", InstrumentListHeader.OpenBankFolder, true));
    }

    /// <summary>One item per sound of the bank as it stands now - the built-in sounds with the
    /// user's own files over them - a separator between its sections.</summary>
    private static IEnumerable<Control> BankEntries(Action<GmBank.Sound> picked)
    {
        GmBank.BankSection? section = null;
        foreach (var sound in GmBank.Load(EditorSettings.BankDirectory).All)
        {
            if (section is not null && sound.Section != section)
                yield return new Separator();
            section = sound.Section;

            // An underscore in a menu header marks its access key; a name's own is doubled.
            string header = $"{GmBank.Label(sound.Key)}  -  {sound.Instrument.Name} ({sound.Origin})".Replace("_", "__");
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => picked(sound);
            yield return item;
        }
    }

    private static MenuItem MenuEntry(string header, Action action, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A sound of the bank onto an instrument the song has, in place of its own - its
    /// number stays, so every note on it plays the new sound. One undo step.</summary>
    private void ChangeSound(Instrument target, GmBank.Sound sound)
    {
        _session.ReplaceInstrumentSound(target, sound.Instrument);
        SelectInstrumentNumber(target.Number);
        ShowNotice($"Instrument {target.Number:00} is now {target.Name} from the sound bank ({sound.Origin})");
    }

    /// <summary>A copy of the selected instrument under the next free number, selected so it is
    /// ready to rename and edit.</summary>
    private void DuplicateInstrument()
    {
        if (SelectedInstrument is not { } source)
            return;

        if (_session.CopyInstrument(source) is { } copy)
            SelectInstrumentNumber(copy.Number);
        else
            ShowInstrumentLimit();
    }

    /// <summary>The instrument selected in whichever view is on screen.</summary>
    private Instrument? CurrentInstrument()
    {
        int? number = _dawShowing ? _daw.SelectedInstrumentNumber ?? SelectedInstrument?.Number : SelectedInstrument?.Number;
        return _session.Song.Instruments.FirstOrDefault(i => i.Number == number);
    }

    private async Task SaveInstrumentAsync()
    {
        if (CurrentInstrument() is not { } instrument)
        {
            await MessageWindow.ShowAsync(this, "Save instrument", "Select an instrument to save first.");
            return;
        }

        string name = instrument.Name.Length > 0 ? instrument.Name : $"Instrument {instrument.Number:D2}";
        var options = new FilePickerSaveOptions
        {
            Title = "Save instrument",
            SuggestedFileName = string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".json",
            DefaultExtension = "json",
            FileTypeChoices = [InstrumentFiles],
        };
        options.SuggestedStartLocation = await StorageProvider.StartIn(DialogFolder.SaveInstrument);

        var file = await StorageProvider.SaveFilePickerAsync(options);
        if (file?.TryGetLocalPath() is not { } path)
            return;

        try
        {
            File.WriteAllText(path, InstrumentFile.Write(instrument));
            EditorSettings.RememberFolder(DialogFolder.SaveInstrument, path);
            ShowNotice($"Saved {name} to {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot save instrument", ex.Message);
        }
    }
}
