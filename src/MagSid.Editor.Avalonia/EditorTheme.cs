using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The Windows editor's look, for every control the Avalonia one shares with it: Segoe UI at 9
/// points, flat dark buttons and fields with a thin border, square corners, and the palette of
/// EditorPalette and DarkTheme - so 1.71 looks like 1.64, not like Fluent's roomier defaults.
/// </summary>
internal static class EditorTheme
{
    public static readonly AvColor BackgroundColour = AvColor.FromRgb(0x10, 0x10, 0x20);
    public static readonly AvColor FieldColour = AvColor.FromRgb(0x1C, 0x1C, 0x30);

    public static readonly IBrush Background = new SolidColorBrush(BackgroundColour);
    public static readonly IBrush Field = new SolidColorBrush(FieldColour);
    public static readonly IBrush Button = new SolidColorBrush(AvColor.FromRgb(0x28, 0x28, 0x3C));
    public static readonly IBrush ButtonHover = new SolidColorBrush(AvColor.FromRgb(0x36, 0x36, 0x50));
    public static readonly IBrush Border = new SolidColorBrush(AvColor.FromRgb(0x4A, 0x4A, 0x6E));
    public static readonly IBrush Selection = new SolidColorBrush(AvColor.FromRgb(0x20, 0x30, 0x4A));
    public static readonly IBrush Title = new SolidColorBrush(AvColor.FromRgb(0x66, 0xCC, 0xFF));
    public static readonly IBrush Subtitle = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    public static readonly IBrush Heading = new SolidColorBrush(AvColor.FromRgb(0xAA, 0xAA, 0xFF));
    public static readonly IBrush Body = new SolidColorBrush(AvColor.FromRgb(0xD2, 0xD2, 0xDE));
    public static readonly IBrush Mono = new SolidColorBrush(AvColor.FromRgb(0x9E, 0xC8, 0xFF));
    public static readonly IBrush Notice = new SolidColorBrush(AvColor.FromRgb(0xC8, 0xC2, 0xF4));

    /// <summary>Segoe UI where there is one, as WinForms uses; the system's own font elsewhere.</summary>
    public static readonly FontFamily UiFont = new("Segoe UI, Inter, Helvetica Neue, Arial, sans-serif");

    public static readonly FontFamily MonoFont = new("Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>9 points: WinForms' default font size, in Avalonia's pixels.</summary>
    public const double FontSize = 12;

    /// <summary>Consolas 9.5 points, the fields' and grids' font in the Windows editor.</summary>
    public const double MonoSize = 12.5;

    public static void Apply(Application app)
    {
        // Fluent sizes its controls from these; 14 and the roomy paddings are what made every
        // field and button half as big again as the Windows editor's.
        app.Resources["ControlContentThemeFontSize"] = FontSize;
        app.Resources["ControlContentThemeFontFamily"] = UiFont;
        app.Resources["ContentControlThemeFontFamily"] = UiFont;
        app.Resources["TextControlThemeMinHeight"] = 20.0;
        app.Resources["TextControlThemePadding"] = new Thickness(3, 1, 3, 1);
        app.Resources["ButtonPadding"] = new Thickness(6, 2, 6, 2);
        app.Resources["ComboBoxMinHeight"] = 20.0;
        app.Resources["MenuFlyoutItemThemePadding"] = new Thickness(10, 3, 10, 3);
        app.Resources["MenuFlyoutPresenterThemePadding"] = new Thickness(0, 2, 0, 2);
        app.Resources["MenuBarItemPadding"] = new Thickness(7, 3, 7, 3);
        app.Resources["MenuFlyoutPresenterBackground"] = Field;
        app.Resources["MenuFlyoutPresenterBorderBrush"] = Border;
        app.Resources["FlyoutPresenterBackground"] = Field;
        app.Resources["FlyoutBorderThemeBrush"] = Border;
        app.Resources["MenuFlyoutItemBackgroundPointerOver"] = ButtonHover;
        app.Resources["MenuFlyoutItemBackgroundPressed"] = Button;
        app.Resources["ToolTipBackground"] = Field;
        app.Resources["ToolTipBorderBrush"] = Border;
        app.Resources["ToolTipForeground"] = Body;
        app.Resources["ToolTipContentThemeFontSize"] = FontSize;

        // Every window, the main one and each dialog: Is, not OfType, so subclasses match too.
        var window = new Style(x => x.Is<Window>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontFamilyProperty, UiFont),
                new Setter(TemplatedControl.FontSizeProperty, FontSize),
                new Setter(TemplatedControl.ForegroundProperty, Body),
                new Setter(TemplatedControl.BackgroundProperty, Background),
            },
        };
        if (AppIcon() is { } icon)
            window.Setters.Add(new Setter(Window.IconProperty, icon));
        app.Styles.Add(window);

        // Windows draws the title bar itself, white unless asked - 1.64 asked for a dark one.
        if (OperatingSystem.IsWindows())
            Window.WindowOpenedEvent.AddClassHandler<Window>((opened, _) => DarkTitleBar(opened));

        // Buttons and toggles: flat, a thin border, the editor's button colours.
        foreach (var type in new[] { typeof(Button), typeof(ToggleButton) })
        {
            app.Styles.Add(Of(type, "",
                (TemplatedControl.BackgroundProperty, Button),
                (TemplatedControl.ForegroundProperty, Body),
                (TemplatedControl.BorderBrushProperty, Border),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                (TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                (TemplatedControl.PaddingProperty, new Thickness(6, 2, 6, 2)),
                (Layoutable.MinHeightProperty, 0.0)));
            foreach (string state in new[] { ":pointerover", ":pressed" })
            {
                app.Styles.Add(Part(type, state, "PART_ContentPresenter",
                    (ContentPresenter.BackgroundProperty, ButtonHover),
                    (ContentPresenter.BorderBrushProperty, Border),
                    (ContentPresenter.ForegroundProperty, Body)));
            }
            app.Styles.Add(Part(type, ":disabled", "PART_ContentPresenter",
                (ContentPresenter.BackgroundProperty, Field),
                (ContentPresenter.BorderBrushProperty, Button),
                (ContentPresenter.ForegroundProperty, Subtitle)));
        }
        foreach (string state in new[] { ":checked", ":checked:pointerover", ":checked:pressed" })
        {
            app.Styles.Add(Part(typeof(ToggleButton), state, "PART_ContentPresenter",
                (ContentPresenter.BackgroundProperty, Selection),
                (ContentPresenter.BorderBrushProperty, Title),
                (ContentPresenter.ForegroundProperty, Title)));
        }

        // On/off switches: no frame, a light that is lit when on. The name is grey when off.
        app.Styles.Add(Of(typeof(ToggleButton), ".switch",
            (TemplatedControl.BackgroundProperty, Brushes.Transparent),
            (TemplatedControl.BorderBrushProperty, Brushes.Transparent),
            (TemplatedControl.ForegroundProperty, Subtitle),
            (TemplatedControl.PaddingProperty, new Thickness(4, 1, 5, 1))));
        foreach (string state in new[] { ":pointerover", ":pressed", ":checked:pointerover", ":checked:pressed" })
        {
            app.Styles.Add(Part(typeof(ToggleButton), ".switch" + state, "PART_ContentPresenter",
                (ContentPresenter.BackgroundProperty, ButtonHover),
                (ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                (ContentPresenter.ForegroundProperty, Body)));
        }
        app.Styles.Add(Part(typeof(ToggleButton), ".switch:checked", "PART_ContentPresenter",
            (ContentPresenter.BackgroundProperty, Brushes.Transparent),
            (ContentPresenter.BorderBrushProperty, Brushes.Transparent),
            (ContentPresenter.ForegroundProperty, Body)));
        app.Styles.Add(new Style(x => x.OfType<ToggleButton>().Class("switch").Descendant().OfType<global::Avalonia.Controls.Shapes.Ellipse>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.Shapes.Shape.StrokeProperty, Subtitle),
                new Setter(global::Avalonia.Controls.Shapes.Shape.StrokeThicknessProperty, 1.0),
                new Setter(global::Avalonia.Controls.Shapes.Shape.FillProperty, Brushes.Transparent),
            },
        });
        app.Styles.Add(new Style(x => x.OfType<ToggleButton>().Class("switch").Class(":checked").Descendant().OfType<global::Avalonia.Controls.Shapes.Ellipse>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.Shapes.Shape.StrokeProperty, Title),
                new Setter(global::Avalonia.Controls.Shapes.Shape.FillProperty, Title),
            },
        });

        // A choice of segments: the ones not chosen are quiet - grey text in a dim frame.
        app.Styles.Add(Of(typeof(ToggleButton), ".segment",
            ((AvaloniaProperty)TemplatedControl.BackgroundProperty, (object)Field),
            (TemplatedControl.ForegroundProperty, Subtitle)));
        // Neighbours share a border line; the chosen one is drawn over them, so all of its frame shows.
        app.Styles.Add(Of(typeof(ToggleButton), ".segment:checked", (Visual.ZIndexProperty, 1)));

        // Text fields: a dark box with a thin border, brighter where the caret is.
        app.Styles.Add(Of(typeof(TextBox), "",
            (TemplatedControl.BackgroundProperty, Field),
            (TemplatedControl.BorderBrushProperty, Border),
            (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
            (TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
            (TemplatedControl.PaddingProperty, new Thickness(3, 1, 3, 1)),
            (TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
            (Layoutable.MinHeightProperty, 0.0)));
        foreach (var (state, border) in new[] { (":pointerover", Border), (":focus", Title) })
        {
            app.Styles.Add(Part(typeof(TextBox), state, "PART_BorderElement",
                (global::Avalonia.Controls.Border.BackgroundProperty, Field),
                (global::Avalonia.Controls.Border.BorderBrushProperty, border),
                (global::Avalonia.Controls.Border.BorderThicknessProperty, new Thickness(1))));
        }
        app.Styles.Add(Part(typeof(TextBox), ":disabled", "PART_BorderElement",
            (global::Avalonia.Controls.Border.BackgroundProperty, Field),
            (global::Avalonia.Controls.Border.BorderBrushProperty, Button)));
        app.Styles.Add(Of(typeof(TextBox), ":disabled", (TemplatedControl.ForegroundProperty, Subtitle)));

        // Drop-down lists the same.
        app.Styles.Add(Of(typeof(ComboBox), "",
            (TemplatedControl.BackgroundProperty, Field),
            (TemplatedControl.BorderBrushProperty, Border),
            (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
            (TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
            (TemplatedControl.PaddingProperty, new Thickness(4, 1, 2, 1)),
            (Layoutable.MinHeightProperty, 0.0)));
        foreach (string state in new[] { ":pointerover", ":pressed", ":focus" })
        {
            app.Styles.Add(Part(typeof(ComboBox), state, "Background",
                (global::Avalonia.Controls.Border.BackgroundProperty, Field),
                (global::Avalonia.Controls.Border.BorderBrushProperty, Border)));
        }

        // A number field's arrows: narrow, beside the number, as WinForms draws them.
        app.Styles.Add(Of(typeof(NumericUpDown), "",
            (TemplatedControl.BackgroundProperty, Field),
            (TemplatedControl.BorderBrushProperty, Border),
            (TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
            (TemplatedControl.PaddingProperty, new Thickness(3, 0, 0, 0)),
            (NumericUpDown.VerticalContentAlignmentProperty, VerticalAlignment.Center),
            (Layoutable.MinHeightProperty, 0.0),
            (Layoutable.HeightProperty, 20.0)));
        // Fluent's template fixes its arrows' width, so the spinner gets a template of its own:
        // the field, and two small arrows stacked at its right edge.
        app.Styles.Add(new Style(x => x.OfType<ButtonSpinner>())
        {
            Setters = { new Setter(TemplatedControl.TemplateProperty, SpinnerTemplate()) },
        });

        // A dialog's usual answer - what Enter picks - lit as a chosen toggle is.
        foreach (string state in new[] { "", ":pointerover", ":pressed" })
        {
            app.Styles.Add(Part(typeof(Button), ".accent" + state, "PART_ContentPresenter",
                (ContentPresenter.BackgroundProperty, state == "" ? Selection : ButtonHover),
                (ContentPresenter.BorderBrushProperty, Title),
                (ContentPresenter.ForegroundProperty, Title)));
        }

        // Toolbar and header buttons: flat until the pointer is on them, as ToolStripButton.
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("strip"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
            },
        });

        // The program table's cells: the grid lines are their frames', so a cell draws no border
        // of its own - only the caret's, while it is typed in.
        foreach (string state in new[] { ":pointerover", ":disabled" })
        {
            app.Styles.Add(new Style(x => Select(x.OfType<TextBox>().Class("cell"), state).Template().Name("PART_BorderElement"))
            {
                Setters =
                {
                    new Setter(global::Avalonia.Controls.Border.BackgroundProperty, Field),
                    new Setter(global::Avalonia.Controls.Border.BorderThicknessProperty, new Thickness(0)),
                },
            });
        }
        foreach (string state in new[] { ":pointerover", ":pressed", ":focus", ":disabled" })
        {
            app.Styles.Add(new Style(x => Select(x.OfType<ComboBox>().Class("cell"), state).Template().Name("Background"))
            {
                Setters =
                {
                    new Setter(global::Avalonia.Controls.Border.BackgroundProperty, Field),
                    new Setter(global::Avalonia.Controls.Border.BorderThicknessProperty, new Thickness(0)),
                },
            });
        }

        // The menu bar is the strips' colour, as MenuStrip is in DarkTheme.
        app.Styles.Add(Of(typeof(Menu), "",
            (TemplatedControl.BackgroundProperty, Field),
            (Layoutable.MinHeightProperty, 0.0)));
        app.Styles.Add(Of(typeof(MenuItem), "",
            (TemplatedControl.FontSizeProperty, FontSize),
            (TemplatedControl.ForegroundProperty, Body)));

        app.Styles.Add(Of(typeof(ScrollBar), "",
            (TemplatedControl.BackgroundProperty, Background)));
    }

    /// <summary>A number field as NumericUpDown is in WinForms: the number, and a narrow column of
    /// two arrows - the parts ButtonSpinner looks for by name.</summary>
    private static FuncControlTemplate<ButtonSpinner> SpinnerTemplate() => new((spinner, scope) =>
    {
        RepeatButton Arrow(string name, string glyph) => new RepeatButton
        {
            Name = name,
            Content = glyph,
            Width = 15,
            MinHeight = 0,
            Padding = new Thickness(0),
            FontSize = 7,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = false,
        }.RegisterInNameScope(scope);

        var up = Arrow("PART_IncreaseButton", "▲");
        var down = Arrow("PART_DecreaseButton", "▼");
        var arrows = new Grid { RowDefinitions = RowDefinitions.Parse("*,*"), Children = { up, down } };
        Grid.SetRow(down, 1);

        var content = new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            [!ContentPresenter.ContentProperty] = spinner[!ContentControl.ContentProperty],
            [!ContentPresenter.ContentTemplateProperty] = spinner[!ContentControl.ContentTemplateProperty],
        }.RegisterInNameScope(scope);

        var layout = new DockPanel();
        DockPanel.SetDock(arrows, Dock.Right);
        layout.Children.Add(arrows);
        layout.Children.Add(content);

        return new global::Avalonia.Controls.Border
        {
            [!global::Avalonia.Controls.Border.BackgroundProperty] = spinner[!TemplatedControl.BackgroundProperty],
            [!global::Avalonia.Controls.Border.BorderBrushProperty] = spinner[!TemplatedControl.BorderBrushProperty],
            [!global::Avalonia.Controls.Border.BorderThicknessProperty] = spinner[!TemplatedControl.BorderThicknessProperty],
            Child = layout,
        };
    });

    /// <summary>A dark caption, as DarkTheme.ApplyTitleBar gives 1.64's windows: attribute 20 from
    /// Windows 10 20H1, 19 before it; one an older Windows does not know just fails.</summary>
    private static void DarkTitleBar(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle)
            return;

        int enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>MagSID.ico, embedded; null if it is missing,
    /// so a build without it still runs, on the system's default icon.</summary>
    private static WindowIcon? AppIcon()
    {
        using var stream = typeof(EditorTheme).Assembly.GetManifestResourceStream("MagSID.ico");
        return stream is null ? null : new WindowIcon(stream);
    }

    private static Style Of(Type type, string state, params (AvaloniaProperty Property, object Value)[] setters)
    {
        var style = new Style(x => Select(x.OfType(type), state));
        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));
        return style;
    }

    private static Style Part(Type type, string state, string part, params (AvaloniaProperty Property, object Value)[] setters)
    {
        var style = new Style(x => Select(x.OfType(type), state).Template().Name(part));
        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));
        return style;
    }

    /// <summary>Adds <paramref name="state"/>'s classes: ".switch:checked" is the class "switch"
    /// and the pseudo-class ":checked".</summary>
    private static Selector Select(Selector selector, string state)
    {
        foreach (System.Text.RegularExpressions.Match part in System.Text.RegularExpressions.Regex.Matches(state, @"([.:])([^.:]+)"))
            selector = selector.Class(part.Groups[1].Value == ":" ? ":" + part.Groups[2].Value : part.Groups[2].Value);
        return selector;
    }
}
