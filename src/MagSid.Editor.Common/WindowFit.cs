namespace MagSid.Editor.App;

/// <summary>
/// How much of a window's wanted size a display has room for. A window taller than the display,
/// centred on it, starts with its title bar above the top of the screen, and with the title bar
/// goes every way to move, resize, maximize or minimize it.
/// </summary>
public static class WindowFit
{
    /// <summary>The system's frame around the client area, which is not known before the window
    /// opens: more than any title bar at normal scaling takes.</summary>
    public const double FrameAllowance = 40;

    /// <param name="wanted">In the window's own units.</param>
    /// <param name="workingArea">The display's free length in pixels, without the taskbar.</param>
    /// <param name="scaling">Pixels to one unit.</param>
    /// <param name="frame">What the window's frame takes of that length.</param>
    public static double Within(double wanted, int workingArea, double scaling, double frame = 0) =>
        Math.Min(wanted, (workingArea / scaling) - frame);
}
