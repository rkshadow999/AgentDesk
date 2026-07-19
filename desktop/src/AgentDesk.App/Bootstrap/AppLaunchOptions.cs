namespace AgentDesk.App;

public sealed record AppLaunchOptions(string? WebRoot, string? WorkspacePath)
{
    public static AppLaunchOptions Parse(
        IReadOnlyList<string> arguments,
        bool allowExternalWebRoot = false)
    {
        string? webRoot = null;
        string? workspacePath = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (index + 1 >= arguments.Count)
            {
                throw new ArgumentException($"启动参数 {option} 缺少路径。", nameof(arguments));
            }

            var value = Path.GetFullPath(arguments[++index]);
            if (option.Equals("--web-root", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowExternalWebRoot)
                {
                    throw new ArgumentException(
                        "当前构建不允许使用 --web-root 加载外部界面资源。",
                        nameof(arguments));
                }

                webRoot = value;
            }
            else if (option.Equals("--workspace", StringComparison.OrdinalIgnoreCase))
            {
                workspacePath = value;
            }
            else
            {
                throw new ArgumentException($"不支持的启动参数：{option}", nameof(arguments));
            }
        }

        return new(webRoot, workspacePath);
    }
}
