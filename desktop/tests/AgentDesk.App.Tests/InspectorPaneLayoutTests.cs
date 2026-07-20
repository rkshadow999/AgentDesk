using AgentDesk.App.Windowing;

namespace AgentDesk.App.Tests;

public sealed class InspectorPaneLayoutTests
{
    [Theory]
    [InlineData(319, InspectorPaneLayout.MinimumInspectorWidth)]
    [InlineData(320, InspectorPaneLayout.MinimumInspectorWidth)]
    [InlineData(640, 640)]
    [InlineData(960, InspectorPaneLayout.MaximumInspectorWidth)]
    [InlineData(961, InspectorPaneLayout.MaximumInspectorWidth)]
    [InlineData(double.NaN, InspectorPaneLayout.DefaultInspectorWidth)]
    [InlineData(double.PositiveInfinity, InspectorPaneLayout.DefaultInspectorWidth)]
    [InlineData(double.NegativeInfinity, InspectorPaneLayout.DefaultInspectorWidth)]
    public void NormalizePreferredWidth_ClampsAndFallsBack(
        double requestedWidth,
        double expectedWidth)
    {
        Assert.Equal(
            expectedWidth,
            InspectorPaneLayout.NormalizePreferredWidth(requestedWidth));
    }

    [Fact]
    public void GetVisibleWidth_PreservesTheMinimumWorkbenchAndMaximumInspectorWidths()
    {
        var layout = new InspectorPaneLayout(900);

        Assert.Equal(432, layout.GetVisibleWidth(1_000));
        Assert.Equal(900, layout.GetVisibleWidth(1_600));
        Assert.Equal(InspectorPaneLayout.MinimumInspectorWidth, layout.GetVisibleWidth(800));

        var maximum = new InspectorPaneLayout(double.MaxValue);
        Assert.Equal(InspectorPaneLayout.MaximumInspectorWidth, maximum.GetVisibleWidth(4_000));
    }

    [Fact]
    public void TemporaryNarrowing_DoesNotOverwriteThePreferredWidth()
    {
        var layout = new InspectorPaneLayout(780);

        Assert.Equal(382, layout.GetVisibleWidth(950));
        Assert.Equal(780, layout.PreferredWidth);
        Assert.Equal(780, layout.GetVisibleWidth(1_600));
    }

    [Fact]
    public void ResizeWhileTemporarilyNarrowed_AppliesTheDeltaToThePreferredWidth()
    {
        var layout = new InspectorPaneLayout(780);
        var temporarilyVisibleWidth = layout.GetVisibleWidth(950);

        Assert.Equal(382, temporarilyVisibleWidth);
        Assert.Equal(
            820,
            layout.ResizeFromHorizontalDelta(horizontalDelta: -40));
        Assert.Equal(820, layout.PreferredWidth);
        Assert.Equal(820, layout.GetVisibleWidth(1_600));
    }

    [Fact]
    public void ResizeFromHorizontalDelta_UsesTheSplitterDirectionAndClampsThePreference()
    {
        var layout = new InspectorPaneLayout(500);

        Assert.Equal(540, layout.ResizeFromHorizontalDelta(horizontalDelta: -40));
        Assert.Equal(510, layout.ResizeFromHorizontalDelta(horizontalDelta: 30));
        Assert.Equal(
            InspectorPaneLayout.MaximumInspectorWidth,
            layout.ResizeFromHorizontalDelta(horizontalDelta: -10_000));
        Assert.Equal(
            InspectorPaneLayout.MinimumInspectorWidth,
            layout.ResizeFromHorizontalDelta(horizontalDelta: 10_000));
    }

    [Fact]
    public void ResizeAndAdjust_NonFiniteInputsDoNotCorruptThePreference()
    {
        var layout = new InspectorPaneLayout(500);

        Assert.Equal(500, layout.ResizeFromHorizontalDelta(double.PositiveInfinity));
        Assert.Equal(500, layout.AdjustPreferredWidth(double.NegativeInfinity));
    }

    [Fact]
    public void AdjustAndReset_UpdateThePreferredWidth()
    {
        var layout = new InspectorPaneLayout(500);

        Assert.Equal(516, layout.AdjustPreferredWidth(16));
        Assert.Equal(500, layout.AdjustPreferredWidth(-16));
        Assert.Equal(InspectorPaneLayout.DefaultInspectorWidth, layout.Reset());
        Assert.Equal(InspectorPaneLayout.DefaultInspectorWidth, layout.PreferredWidth);
    }

    [Fact]
    public void WindowLayoutState_NormalizeUsesTheInspectorPolicy()
    {
        Assert.Equal(
            WindowLayoutState.Default,
            new WindowLayoutState(double.NaN).Normalize());
        Assert.Equal(
            InspectorPaneLayout.MinimumInspectorWidth,
            new WindowLayoutState(10).Normalize().InspectorPaneWidth);
        Assert.Equal(
            InspectorPaneLayout.MaximumInspectorWidth,
            new WindowLayoutState(10_000).Normalize().InspectorPaneWidth);
    }
}
