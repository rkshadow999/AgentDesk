namespace AgentDesk.App.Bridge;

internal static class ExtensionApprovalPresentation
{
    public static string ActionResourceKey(string action) => action switch
    {
        "toggle" => "ExtensionActionToggle",
        "upsert_stdio" => "ExtensionActionConfigureStdio",
        "upsert_http" => "ExtensionActionConfigureHttp",
        "delete" => "ExtensionActionDelete",
        "add_path" => "ExtensionActionAddPath",
        "remove_path" => "ExtensionActionRemovePath",
        "reset" => "ExtensionActionReset",
        "reload" => "ExtensionActionReload",
        "trust" => "ExtensionActionTrust",
        "untrust" => "ExtensionActionUntrust",
        "add" => "ExtensionActionAdd",
        "remove" => "ExtensionActionRemove",
        "enable" => "ExtensionActionEnable",
        "disable" => "ExtensionActionDisable",
        "toggle_source" => "ExtensionActionToggleSource",
        "install" => "ExtensionActionInstall",
        "update" => "ExtensionActionUpdate",
        "uninstall" => "ExtensionActionUninstall",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
