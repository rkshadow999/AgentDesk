using Microsoft.Windows.ApplicationModel.Resources;

namespace AgentDesk.App;

internal sealed class NativeStringResources
{
    private readonly ResourceMap _resourceMap;
    private readonly ResourceContext _resourceContext;

    public NativeStringResources()
    {
        var manager = new ResourceManager();
        _resourceMap = manager.MainResourceMap.GetSubtree("Resources");
        _resourceContext = manager.CreateResourceContext();
    }

    public string Get(string name) =>
        _resourceMap.GetValue(name, _resourceContext).ValueAsString;
}
