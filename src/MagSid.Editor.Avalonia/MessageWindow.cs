using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace MagSid.Editor.Avalonia;

/// <summary>The editor's small modal questions and messages - Avalonia has no MessageBox of its
/// own. One look for all of them: the window as big as what it says, the text wrapped to a
/// comfortable width, and the choices a centred row of equal buttons, the usual one lit.</summary>
internal static class MessageWindow
{
    public static Task ShowAsync(Window owner, string title, string message) =>
        ChooseAsync(owner, title, message, ["OK"], accept: 0, cancel: 0);

    /// <summary>
    /// Asks <paramref name="message"/> and returns the index of the choice made, or
    /// <paramref name="cancel"/> when the window is closed instead. Enter is
    /// <paramref name="accept"/>, Esc <paramref name="cancel"/>. <paramref name="middle"/> goes
    /// under the message, and <paramref name="detail"/> under that, in grey. Text wraps at
    /// <paramref name="width"/>; a short message makes a narrower window.
    /// </summary>
    public static async Task<int> ChooseAsync(Window owner, string title, string message, string[] choices,
        int accept, int cancel, string? detail = null, Control? middle = null, double width = 360)
    {
        int answer = cancel;
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            CanMinimize = false,
            CanMaximize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        // Equal widths, so the row reads as one set of answers; centred under the text.
        var buttons = new UniformGrid { Rows = 1, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        for (int i = 0; i < choices.Length; i++)
        {
            int choice = i;
            var button = new Button
            {
                Content = choices[i],
                MinWidth = 92,
                Margin = new Thickness(4, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsDefault = i == accept,
                IsCancel = i == cancel,
            };
            if (i == accept)
                button.Classes.Add("accent");
            button.Click += (_, _) =>
            {
                answer = choice;
                window.Close();
            };
            buttons.Children.Add(button);
        }

        var body = new StackPanel { MaxWidth = width, Margin = new Thickness(24, 20, 24, 18), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (middle is not null)
            body.Children.Add(middle);
        if (detail is not null)
            body.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.Subtitle });
        body.Children.Add(buttons);
        window.Content = body;

        await window.ShowDialog(owner);
        return answer;
    }
}
