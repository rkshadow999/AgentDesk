namespace AgentDesk.App.Windowing;

public sealed class InspectorPaneLayout
{
    public const double DefaultInspectorWidth = 360d;
    public const double MinimumInspectorWidth = 320d;
    public const double MaximumInspectorWidth = 960d;
    public const double MinimumWorkbenchWidth = 560d;
    public const double SplitterWidth = 8d;

    public InspectorPaneLayout(double preferredWidth = DefaultInspectorWidth)
    {
        PreferredWidth = NormalizePreferredWidth(preferredWidth);
    }

    public double PreferredWidth { get; private set; }

    public double GetVisibleWidth(double surfaceWidth)
    {
        if (!double.IsFinite(surfaceWidth))
        {
            return Math.Min(PreferredWidth, DefaultInspectorWidth);
        }

        var availableWidth = surfaceWidth - MinimumWorkbenchWidth - SplitterWidth;
        var maximumVisibleWidth = Math.Clamp(
            availableWidth,
            MinimumInspectorWidth,
            MaximumInspectorWidth);
        return Math.Min(PreferredWidth, maximumVisibleWidth);
    }

    public double ResizeFromHorizontalDelta(double horizontalDelta)
    {
        if (!double.IsFinite(horizontalDelta))
        {
            return PreferredWidth;
        }

        return AdjustPreferredWidth(-horizontalDelta);
    }

    public double AdjustPreferredWidth(double inspectorDelta)
    {
        if (!double.IsFinite(inspectorDelta))
        {
            return PreferredWidth;
        }

        PreferredWidth = NormalizePreferredWidth(PreferredWidth + inspectorDelta);
        return PreferredWidth;
    }

    public double SetPreferredWidth(double preferredWidth)
    {
        PreferredWidth = NormalizePreferredWidth(preferredWidth);
        return PreferredWidth;
    }

    public double Reset() => SetPreferredWidth(DefaultInspectorWidth);

    public static double NormalizePreferredWidth(double preferredWidth)
    {
        if (!double.IsFinite(preferredWidth))
        {
            return DefaultInspectorWidth;
        }

        return Math.Clamp(preferredWidth, MinimumInspectorWidth, MaximumInspectorWidth);
    }
}
