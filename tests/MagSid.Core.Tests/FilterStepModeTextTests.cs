using MagSid.Core.Model;

namespace MagSid.Core.Tests;

public class FilterStepModeTextTests
{
    [Theory]
    [InlineData(0x0F, "off")]
    [InlineData(0x1F, "LP")]
    [InlineData(0x2F, "BP")]
    [InlineData(0x4F, "HP")]
    [InlineData(0x5F, "LP+HP")]
    [InlineData(0x6F, "BP+HP")]
    [InlineData(0x7F, "LP+BP+HP")]
    [InlineData(0x10, "LP")]        // the volume is no part of it
    [InlineData(0x9F, "LP")]        // nor is 3OFF, which a setup step never keeps
    public void A_setup_names_its_modes(int modeVolume, string text)
    {
        Assert.Equal(text, FilterStep.Setup((byte)modeVolume, 0xF1, 0x20).ModeText);
    }

    [Fact]
    public void A_sweep_has_no_mode_of_its_own()
    {
        Assert.Null(FilterStep.Sweep(0x20).ModeText);
    }
}
