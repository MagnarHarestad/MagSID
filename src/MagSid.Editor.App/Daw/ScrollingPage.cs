namespace MagSid.Editor.App.Daw;

/// <summary>
/// Holds a page at the height its contents need and scrolls it in a window shorter than that,
/// because a page that shrinks with the window squeezes its lanes until their steps and buttons
/// are cut off. In a tall enough window there is no bar and the page simply fills it.
///
/// The wheel scrolls over anything that does not take it itself: knobs and lanes keep theirs.
/// </summary>
internal sealed class ScrollingPage : Panel
{
    private const int BarWidth = 12;

    private readonly SlimScrollBar _bar = new(Orientation.Vertical);
    private readonly Control _page;
    private readonly int _pageHeight;

    public ScrollingPage(Control page, int pageHeight)
    {
        _page = page;
        _pageHeight = pageHeight;

        _page.Dock = DockStyle.None;
        _bar.SmallChange = 40;
        _bar.Scroll += (_, _) => Place();
        Controls.Add(_bar);
        Controls.Add(_page);
        WheelScrolls(_page);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Place();
    }

    private void WheelScrolls(Control control)
    {
        control.MouseWheel += (_, e) =>
        {
            _bar.Value -= Math.Sign(e.Delta) * _bar.SmallChange;
            Place();
        };

        foreach (Control child in control.Controls)
            WheelScrolls(child);
    }

    private void Place()
    {
        bool scrolls = Height < _pageHeight;

        _bar.Maximum = _pageHeight - 1;
        _bar.LargeChange = Math.Max(1, Height);
        _bar.Visible = scrolls;
        if (scrolls)
            _bar.Bounds = new Rectangle(Width - BarWidth, 0, BarWidth, Height);
        else
            _bar.Value = 0;

        _page.Bounds = new Rectangle(
            0, -_bar.Value, scrolls ? Width - BarWidth : Width, Math.Max(Height, _pageHeight));
    }
}
