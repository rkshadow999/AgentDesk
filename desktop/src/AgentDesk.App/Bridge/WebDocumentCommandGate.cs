using System.Security.Cryptography;

namespace AgentDesk.App.Bridge;

internal sealed class WebDocumentCommandGate
{
    private const int DocumentTokenBytes = 32;
    private readonly Dictionary<string, DocumentState> _documents =
        new(StringComparer.Ordinal);

    public void BeginNavigation(string surfaceId, ulong navigationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        _documents[surfaceId] = new DocumentState(navigationId, DocumentToken: null);
    }

    public string? CompleteNavigation(string surfaceId, ulong navigationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        if (!_documents.TryGetValue(surfaceId, out var document) ||
            document.NavigationId != navigationId ||
            document.DocumentToken is not null)
        {
            return null;
        }

        var documentToken = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(DocumentTokenBytes));
        _documents[surfaceId] = document with { DocumentToken = documentToken };
        return documentToken;
    }

    public WebCommand ParseCurrentCommand(string surfaceId, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        _documents.TryGetValue(surfaceId, out var document);
        return WebMessageProtocol.ParseAuthenticatedCommand(
            json,
            document?.DocumentToken);
    }

    private sealed record DocumentState(ulong NavigationId, string? DocumentToken);
}
