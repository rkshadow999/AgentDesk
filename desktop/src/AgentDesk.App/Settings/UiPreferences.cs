using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;

namespace AgentDesk.App.Settings;

public sealed record UiPreferences(
    string Language,
    string ComposerDraft,
    SessionMode SessionMode,
    ExecutionProfile ExecutionProfile,
    bool NotificationsEnabled = false,
    bool WindowsAutomationEnabled = false,
    bool BackgroundUpdateChecksEnabled = false,
    bool FullAccessEnabled = false,
    int FontScalePercent = 110)
{
    public const int MaximumComposerDraftLength = 64 * 1024;

    public static UiPreferences Default { get; } = new(
        "zh-CN",
        string.Empty,
        SessionMode.Default,
        ExecutionProfile.NativeProtected,
        NotificationsEnabled: false,
        WindowsAutomationEnabled: false,
        BackgroundUpdateChecksEnabled: false,
        FullAccessEnabled: false,
        FontScalePercent: 110);

    public UiPreferences Validate()
    {
        if (Language is not ("zh-CN" or "en-US"))
        {
            throw new ArgumentException("The UI language is not supported.", nameof(Language));
        }
        if (ComposerDraft is null || ComposerDraft.Length > MaximumComposerDraftLength)
        {
            throw new ArgumentException("The composer draft is too long.", nameof(ComposerDraft));
        }
        if (SessionMode is not (SessionMode.Default or SessionMode.Plan))
        {
            throw new ArgumentException("The session mode is not supported.", nameof(SessionMode));
        }
        if (ExecutionProfile is not (ExecutionProfile.NativeProtected or ExecutionProfile.WslStrict))
        {
            throw new ArgumentException(
                "The execution profile is not supported.",
                nameof(ExecutionProfile));
        }
        if (FontScalePercent is not (90 or 100 or 110 or 125 or 140))
        {
            throw new ArgumentException(
                "The interface font scale is not supported.",
                nameof(FontScalePercent));
        }
        return this;
    }

    public UiPreferences ApplyHostCapabilities(bool isWslStrictAvailable) =>
        !isWslStrictAvailable && ExecutionProfile is ExecutionProfile.WslStrict
            ? this with { ExecutionProfile = ExecutionProfile.NativeProtected }
            : this;
}
