using MDViewer.ViewModels;

namespace MDViewer.Tests;

public class ZoomLevelTests
{
    [Fact]
    public void Starts_at_100_percent()
    {
        var zoom = new ZoomLevel();

        Assert.Equal(100, zoom.Percent);
        Assert.Equal(1.0, zoom.Factor);
        Assert.Equal("100%", zoom.Label);
    }

    [Fact]
    public void Repeated_steps_do_not_drift()
    {
        // Regression: the old double arithmetic (1.0 - 7 × 0.1) showed "29%".
        var zoom = new ZoomLevel();
        for (var i = 0; i < 7; i++) zoom.ZoomOut();

        Assert.Equal("30%", zoom.Label);
        Assert.Equal(0.3, zoom.Factor);
    }

    [Fact]
    public void Clamps_at_the_limits_and_reports_no_change()
    {
        var zoom = new ZoomLevel();
        for (var i = 0; i < 100; i++) zoom.ZoomOut();

        Assert.Equal(ZoomLevel.MinPercent, zoom.Percent);
        Assert.False(zoom.CanZoomOut);
        Assert.False(zoom.ZoomOut());

        for (var i = 0; i < 100; i++) zoom.ZoomIn();

        Assert.Equal(ZoomLevel.MaxPercent, zoom.Percent);
        Assert.False(zoom.CanZoomIn);
        Assert.False(zoom.ZoomIn());
    }

    [Fact]
    public void Reset_returns_to_100_and_is_a_no_op_there()
    {
        var zoom = new ZoomLevel();
        Assert.False(zoom.Reset());

        zoom.ZoomIn();
        Assert.True(zoom.Reset());
        Assert.Equal(100, zoom.Percent);
    }
}
