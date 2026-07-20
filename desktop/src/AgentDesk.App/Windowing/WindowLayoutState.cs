namespace AgentDesk.App.Windowing;

public sealed record WindowLayoutState(double InspectorPaneWidth)
{
    public static WindowLayoutState Default { get; } = new(
        InspectorPaneLayout.DefaultInspectorWidth);

    public WindowLayoutState Normalize() => new(
        InspectorPaneLayout.NormalizePreferredWidth(InspectorPaneWidth));
}
