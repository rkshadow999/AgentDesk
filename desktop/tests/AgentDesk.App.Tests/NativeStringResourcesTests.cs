namespace AgentDesk.App.Tests;

public sealed class NativeStringResourcesTests
{
    [Theory]
    [InlineData("InspectorSplitter.AutomationProperties.Name", "InspectorSplitter/AutomationProperties/Name")]
    [InlineData("InspectorSplitter.AutomationProperties.HelpText", "InspectorSplitter/AutomationProperties/HelpText")]
    [InlineData("WorkbenchAutomationName", "WorkbenchAutomationName")]
    public void NormalizeLookupName_UsesResourceMapPathSyntax(string name, string expected)
    {
        Assert.Equal(expected, global::AgentDesk.App.NativeStringResources.NormalizeLookupName(name));
    }
}
