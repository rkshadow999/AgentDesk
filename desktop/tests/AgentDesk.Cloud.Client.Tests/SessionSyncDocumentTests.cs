using System.Text;
using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class SessionSyncDocumentTests
{
    [Fact]
    public void FromJsonRetainsOnlyCallerProvidedContentAndRedactsToString()
    {
        const string json = "{\"explicit\":\"caller content\"}";

        var document = SessionSyncDocument.FromJson(json);

        Assert.Equal(Encoding.UTF8.GetBytes(json), document.ExportUtf8Json());
        Assert.DoesNotContain("caller content", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", document.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void FromJsonRejectsInvalidOrNonObjectContent(string json)
    {
        Assert.Throws<ArgumentException>(() => SessionSyncDocument.FromJson(json));
    }
}
