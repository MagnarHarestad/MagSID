using MagSid.Editor.App;
using Xunit;

namespace MagSid.Assembler.Tests;

public class WindowFitTests
{
    [Theory]
    [InlineData(941, 1040, 1.0, 40, 941)]       // a 1080p display has room: nothing changes
    [InlineData(941, 728, 1.0, 40, 688)]        // a 1366x768 laptop: the frame stays on screen
    [InlineData(941, 1040, 1.5, 40, 653.33)]    // 1080p at 150%: the room is in the window's units
    [InlineData(1155, 1366, 1.0, 0, 1155)]
    [InlineData(1155, 1024, 1.0, 0, 1024)]
    public void The_window_stays_within_the_display(double wanted, int area, double scaling, double frame, double fits)
    {
        Assert.Equal(fits, WindowFit.Within(wanted, area, scaling, frame), 2);
    }
}
