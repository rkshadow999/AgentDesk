namespace AgentDesk.App;

public static class LanguagePolicy
{
    public const string DefaultLanguage = "zh-CN";
    public const string EnglishLanguage = "en-US";

    public static void ApplyDefault(
        string? currentOverride,
        Action<string> setOverride) =>
        ApplyPreferred(null, currentOverride, setOverride);

    public static void ApplyPreferred(
        string? savedLanguage,
        string? currentOverride,
        Action<string> setOverride)
    {
        ArgumentNullException.ThrowIfNull(setOverride);

        var preferredLanguage = IsSupported(savedLanguage)
            ? savedLanguage!
            : IsSupported(currentOverride)
                ? currentOverride!
                : DefaultLanguage;
        if (!string.Equals(currentOverride, preferredLanguage, StringComparison.Ordinal))
        {
            setOverride(preferredLanguage);
        }
    }

    private static bool IsSupported(string? language) =>
        language is DefaultLanguage or EnglishLanguage;
}
