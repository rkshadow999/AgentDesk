using System.Text;
using System.Text.Json;

namespace AgentDesk.Core.Engine;

public sealed class EngineSessionDocument
{
    public const int MaximumBytes = 16 * 1024 * 1024;

    private readonly byte[] _utf8Json;

    private EngineSessionDocument(byte[] utf8Json)
    {
        _utf8Json = utf8Json;
    }

    public int ByteLength => _utf8Json.Length;

    public static EngineSessionDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return FromUtf8Json(Encoding.UTF8.GetBytes(json));
    }

    public static EngineSessionDocument FromUtf8Json(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utf8Json),
                "The engine session document size is invalid.");
        }

        var copy = utf8Json.ToArray();
        try
        {
            using var document = JsonDocument.Parse(
                copy,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "The engine session document must be a JSON object.",
                    nameof(utf8Json));
            }
            return new EngineSessionDocument(copy);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The engine session document is not valid JSON.",
                nameof(utf8Json),
                exception);
        }
    }

    public byte[] ExportUtf8Json() => _utf8Json.ToArray();

    public override string ToString() =>
        $"EngineSessionDocument {{ ByteLength = {ByteLength} }}";
}
