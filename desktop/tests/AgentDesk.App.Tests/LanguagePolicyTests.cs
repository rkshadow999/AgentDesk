namespace AgentDesk.App.Tests;

public sealed class LanguagePolicyTests
{
    [Fact]
    public void ApplyDefault_UsesChineseWhenNoExplicitOverrideExists()
    {
        string? appliedLanguage = null;

        LanguagePolicy.ApplyDefault(
            currentOverride: string.Empty,
            value => appliedLanguage = value);

        Assert.Equal("zh-CN", appliedLanguage);
    }

    [Fact]
    public void ApplyDefault_PreservesAnExplicitLanguageOverride()
    {
        var wasCalled = false;

        LanguagePolicy.ApplyDefault(
            currentOverride: "en-US",
            _ => wasCalled = true);

        Assert.False(wasCalled);
    }

    [Fact]
    public void ApplyPreferred_UsesTheSavedSupportedLanguage()
    {
        string? appliedLanguage = null;

        LanguagePolicy.ApplyPreferred(
            savedLanguage: "en-US",
            currentOverride: "zh-CN",
            value => appliedLanguage = value);

        Assert.Equal("en-US", appliedLanguage);
    }

    [Fact]
    public void ApplyPreferred_FallsBackToChineseForAnUnsupportedSavedLanguage()
    {
        string? appliedLanguage = null;

        LanguagePolicy.ApplyPreferred(
            savedLanguage: "fr-FR",
            currentOverride: string.Empty,
            value => appliedLanguage = value);

        Assert.Equal("zh-CN", appliedLanguage);
    }
}
