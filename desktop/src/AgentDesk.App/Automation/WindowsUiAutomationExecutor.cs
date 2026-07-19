using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace AgentDesk.App.Automation;

public sealed class WindowsUiAutomationExecutor : IWindowsAutomationExecutor
{
    public Task<WindowsAutomationResult> ExecuteAsync(
        WindowsAutomationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Execute(request, cancellationToken), cancellationToken);
    }

    private static WindowsAutomationResult Execute(
        WindowsAutomationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var application = Application.Attach(request.ProcessId);
        using var automation = new UIA3Automation();
        var root = application.GetMainWindow(automation) ??
            throw new InvalidOperationException("The Windows Automation target window was not found.");
        if (request.Action is WindowsAutomationAction.FocusWindow)
        {
            root.Focus();
            return Result(request, root);
        }

        var target = FindTarget(root, request) ??
            throw new InvalidOperationException("The Windows Automation target control was not found.");
        cancellationToken.ThrowIfCancellationRequested();
        switch (request.Action)
        {
            case WindowsAutomationAction.Invoke:
                if (!target.Patterns.Invoke.IsSupported)
                {
                    throw new InvalidOperationException("The target control cannot be invoked.");
                }
                target.Patterns.Invoke.Pattern.Invoke();
                break;
            case WindowsAutomationAction.SetValue:
                if (!target.Patterns.Value.IsSupported ||
                    target.Patterns.Value.Pattern.IsReadOnly.Value)
                {
                    throw new InvalidOperationException("The target control does not accept values.");
                }
                target.Patterns.Value.Pattern.SetValue(request.Value!);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
        return Result(request, target);
    }

    private static AutomationElement? FindTarget(
        AutomationElement root,
        WindowsAutomationRequest request) => (request.AutomationId, request.Name) switch
        {
            ({ } id, { } name) => root.FindFirstDescendant(
                condition => condition.ByAutomationId(id).And(condition.ByName(name))),
            ({ } id, null) => root.FindFirstDescendant(
                condition => condition.ByAutomationId(id)),
            (null, { } name) => root.FindFirstDescendant(
                condition => condition.ByName(name)),
            _ => throw new InvalidOperationException("The Windows Automation selector is missing."),
        };

    private static WindowsAutomationResult Result(
        WindowsAutomationRequest request,
        AutomationElement target)
    {
        var identifier = target.AutomationId;
        if (string.IsNullOrWhiteSpace(identifier))
        {
            identifier = target.Name;
        }
        if (string.IsNullOrWhiteSpace(identifier))
        {
            identifier = "window";
        }
        if (identifier.Length > 256)
        {
            identifier = identifier[..256];
        }
        return new WindowsAutomationResult(request.Action, request.ProcessId, identifier);
    }
}
