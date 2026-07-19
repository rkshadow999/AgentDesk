using System.Xml.Linq;
using AgentDesk.App.Bridge;

namespace AgentDesk.App.Tests;

public sealed class ExtensionApprovalPresentationTests
{
    private static readonly string[] ActionResourceKeys =
    [
        "ExtensionActionToggle",
        "ExtensionActionConfigureStdio",
        "ExtensionActionConfigureHttp",
        "ExtensionActionDelete",
        "ExtensionActionAddPath",
        "ExtensionActionRemovePath",
        "ExtensionActionReset",
        "ExtensionActionReload",
        "ExtensionActionTrust",
        "ExtensionActionUntrust",
        "ExtensionActionAdd",
        "ExtensionActionRemove",
        "ExtensionActionEnable",
        "ExtensionActionDisable",
        "ExtensionActionToggleSource",
        "ExtensionActionInstall",
        "ExtensionActionUpdate",
        "ExtensionActionUninstall",
    ];

    [Theory]
    [InlineData("toggle", "ExtensionActionToggle")]
    [InlineData("upsert_stdio", "ExtensionActionConfigureStdio")]
    [InlineData("upsert_http", "ExtensionActionConfigureHttp")]
    [InlineData("delete", "ExtensionActionDelete")]
    [InlineData("add_path", "ExtensionActionAddPath")]
    [InlineData("remove_path", "ExtensionActionRemovePath")]
    [InlineData("reset", "ExtensionActionReset")]
    [InlineData("reload", "ExtensionActionReload")]
    [InlineData("trust", "ExtensionActionTrust")]
    [InlineData("untrust", "ExtensionActionUntrust")]
    [InlineData("add", "ExtensionActionAdd")]
    [InlineData("remove", "ExtensionActionRemove")]
    [InlineData("enable", "ExtensionActionEnable")]
    [InlineData("disable", "ExtensionActionDisable")]
    [InlineData("toggle_source", "ExtensionActionToggleSource")]
    [InlineData("install", "ExtensionActionInstall")]
    [InlineData("update", "ExtensionActionUpdate")]
    [InlineData("uninstall", "ExtensionActionUninstall")]
    public void ActionResourceKey_MapsEverySupportedInternalAction(
        string action,
        string expectedResourceKey)
    {
        Assert.Equal(
            expectedResourceKey,
            ExtensionApprovalPresentation.ActionResourceKey(action));
    }

    [Fact]
    public void ActionResourceKey_RejectsUnknownActions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExtensionApprovalPresentation.ActionResourceKey("run_arbitrary_code"));
    }

    [Fact]
    public void LocalizedResources_ContainEveryExtensionActionKey()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            var document = XDocument.Load(Path.Combine(
                repositoryRoot,
                "desktop",
                "src",
                "AgentDesk.App",
                "Strings",
                language,
                "Resources.resw"));
            var keys = document
                .Root!
                .Elements("data")
                .Select(element => (string?)element.Attribute("name"))
                .Where(key => key is not null)
                .ToHashSet(StringComparer.Ordinal);

            Assert.All(ActionResourceKeys, key => Assert.Contains(key, keys));
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(
                    directory.FullName,
                    "desktop",
                    "src",
                    "AgentDesk.App",
                    "Strings")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The AgentDesk repository root was not found.");
    }
}
