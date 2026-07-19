using System.Text.Json;
using AgentDesk.App.Bridge;

namespace AgentDesk.App.Tests;

public sealed class WebDocumentCommandGateTests
{
    private const string WorkbenchSurface = "workbench";
    private const string InspectorSurface = "inspector";

    public static TheoryData<string> MutatingCommands => new()
    {
        """
        {
          "schemaVersion": 1,
          "type": "session/archive",
          "documentToken": "$TOKEN",
          "requestId": "00000000-0000-4000-8000-000000000010",
          "sessionId": "session-42",
          "archived": true
        }
        """,
        """
        {
          "schemaVersion": 1,
          "type": "backup/restore",
          "documentToken": "$TOKEN",
          "requestId": "00000000-0000-4000-8000-000000000001"
        }
        """,
        """
        {
          "schemaVersion": 1,
          "type": "update/apply",
          "documentToken": "$TOKEN",
          "requestId": "00000000-0000-4000-8000-000000000002"
        }
        """,
        """
        {
          "schemaVersion": 1,
          "type": "permission/respond",
          "documentToken": "$TOKEN",
          "requestId": "permission-42",
          "outcome": "cancelled"
        }
        """,
    };

    [Theory]
    [MemberData(nameof(MutatingCommands))]
    public void NavigationStarting_RejectsQueuedCommandsFromThePreviousDocument(string template)
    {
        var gate = new WebDocumentCommandGate();
        gate.BeginNavigation(WorkbenchSurface, navigationId: 1);
        var oldToken = Assert.IsType<string>(
            gate.CompleteNavigation(WorkbenchSurface, navigationId: 1));
        var oldDocumentCommand = WithToken(template, oldToken);

        _ = gate.ParseCurrentCommand(WorkbenchSurface, oldDocumentCommand);

        gate.BeginNavigation(WorkbenchSurface, navigationId: 2);

        Assert.Throws<InvalidDataException>(() =>
            gate.ParseCurrentCommand(WorkbenchSurface, oldDocumentCommand));

        var currentToken = Assert.IsType<string>(
            gate.CompleteNavigation(WorkbenchSurface, navigationId: 2));
        Assert.NotEqual(oldToken, currentToken);
        Assert.Throws<InvalidDataException>(() =>
            gate.ParseCurrentCommand(WorkbenchSurface, oldDocumentCommand));
        _ = gate.ParseCurrentCommand(
            WorkbenchSurface,
            WithToken(template, currentToken));
    }

    [Fact]
    public void ParseCurrentCommand_FailsClosedBeforeInitializationAndForAMissingToken()
    {
        var gate = new WebDocumentCommandGate();
        const string uninitialized = """
            {
              "schemaVersion": 1,
              "type": "ui/ready",
              "documentToken": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            }
            """;
        const string missing = """
            {
              "schemaVersion": 1,
              "type": "ui/ready"
            }
            """;

        Assert.Throws<InvalidDataException>(() =>
            gate.ParseCurrentCommand(WorkbenchSurface, uninitialized));

        gate.BeginNavigation(WorkbenchSurface, navigationId: 1);
        _ = Assert.IsType<string>(gate.CompleteNavigation(WorkbenchSurface, navigationId: 1));

        Assert.Throws<InvalidDataException>(() =>
            gate.ParseCurrentCommand(WorkbenchSurface, missing));
    }

    [Fact]
    public void FailedNavigation_RemainsFailClosedUntilARetryCompletes()
    {
        var gate = new WebDocumentCommandGate();
        var template = """
            {
              "schemaVersion": 1,
              "type": "ui/ready",
              "documentToken": "$TOKEN"
            }
            """;
        const string guessedToken =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        gate.BeginNavigation(WorkbenchSurface, navigationId: 1);

        Assert.Throws<InvalidDataException>(() => gate.ParseCurrentCommand(
            WorkbenchSurface,
            WithToken(template, guessedToken)));

        gate.BeginNavigation(WorkbenchSurface, navigationId: 2);
        Assert.Throws<InvalidDataException>(() => gate.ParseCurrentCommand(
            WorkbenchSurface,
            WithToken(template, guessedToken)));

        var retryToken = Assert.IsType<string>(
            gate.CompleteNavigation(WorkbenchSurface, navigationId: 2));
        Assert.IsType<UiReadyWebCommand>(gate.ParseCurrentCommand(
            WorkbenchSurface,
            WithToken(template, retryToken)));
    }

    [Fact]
    public void CompleteNavigation_IgnoresAStaleNavigationCompletion()
    {
        var gate = new WebDocumentCommandGate();
        gate.BeginNavigation(WorkbenchSurface, navigationId: 1);
        gate.BeginNavigation(WorkbenchSurface, navigationId: 2);

        Assert.Null(gate.CompleteNavigation(WorkbenchSurface, navigationId: 1));
        Assert.Throws<InvalidDataException>(() => gate.ParseCurrentCommand(
            WorkbenchSurface,
            WithToken(
                """
                {
                  "schemaVersion": 1,
                  "type": "ui/ready",
                  "documentToken": "$TOKEN"
                }
                """,
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")));

        Assert.IsType<string>(gate.CompleteNavigation(WorkbenchSurface, navigationId: 2));
    }

    [Fact]
    public void Tokens_AreRandomAndIsolatedBetweenWorkbenchAndInspector()
    {
        var gate = new WebDocumentCommandGate();
        gate.BeginNavigation(WorkbenchSurface, navigationId: 1);
        gate.BeginNavigation(InspectorSurface, navigationId: 1);

        var workbenchToken = Assert.IsType<string>(
            gate.CompleteNavigation(WorkbenchSurface, navigationId: 1));
        var inspectorToken = Assert.IsType<string>(
            gate.CompleteNavigation(InspectorSurface, navigationId: 1));

        Assert.Matches("^[0-9A-F]{64}$", workbenchToken);
        Assert.Matches("^[0-9A-F]{64}$", inspectorToken);
        Assert.NotEqual(workbenchToken, inspectorToken);

        var template = """
            {
              "schemaVersion": 1,
              "type": "ui/ready",
              "documentToken": "$TOKEN"
            }
            """;
        Assert.IsType<UiReadyWebCommand>(gate.ParseCurrentCommand(
            WorkbenchSurface,
            WithToken(template, workbenchToken)));
        Assert.IsType<UiReadyWebCommand>(gate.ParseCurrentCommand(
            InspectorSurface,
            WithToken(template, inspectorToken)));
        Assert.Throws<InvalidDataException>(() => gate.ParseCurrentCommand(
            WorkbenchSurface,
            WithToken(template, inspectorToken)));
        Assert.Throws<InvalidDataException>(() => gate.ParseCurrentCommand(
            InspectorSurface,
            WithToken(template, workbenchToken)));
    }

    [Fact]
    public void SerializeDocumentToken_EmitsOnlyTheVersionedBootstrapEnvelope()
    {
        const string token =
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        using var document = JsonDocument.Parse(WebMessageProtocol.SerializeDocumentToken(token));
        var root = document.RootElement;

        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("host/document-token", root.GetProperty("type").GetString());
        Assert.Equal(token, root.GetProperty("documentToken").GetString());
    }

    private static string WithToken(string template, string token) =>
        template.Replace("$TOKEN", token, StringComparison.Ordinal);
}
