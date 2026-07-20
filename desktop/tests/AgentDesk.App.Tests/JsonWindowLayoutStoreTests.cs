using System.Text.Json;
using AgentDesk.App.Windowing;

namespace AgentDesk.App.Tests;

public sealed class JsonWindowLayoutStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "AgentDesk.WindowLayout.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsTheNormalizedPreferredWidth()
    {
        var path = LayoutPath();
        var store = new JsonWindowLayoutStore(path);

        await store.SaveAsync(new WindowLayoutState(640));
        var loaded = await store.LoadAsync();

        Assert.Equal(new WindowLayoutState(640), loaded);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(640, document.RootElement.GetProperty("inspectorPaneWidth").GetDouble());
        Assert.Equal(
            ["inspectorPaneWidth", "schemaVersion"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Save_NormalizesNonFiniteAndOutOfRangeWidths()
    {
        var store = new JsonWindowLayoutStore(LayoutPath());

        await store.SaveAsync(new WindowLayoutState(double.NaN));
        Assert.Equal(WindowLayoutState.Default, await store.LoadAsync());

        await store.SaveAsync(new WindowLayoutState(double.MaxValue));
        Assert.Equal(
            InspectorPaneLayout.MaximumInspectorWidth,
            (await store.LoadAsync()).InspectorPaneWidth);
    }

    [Fact]
    public async Task Load_MissingFileReturnsTheDefault()
    {
        var store = new JsonWindowLayoutStore(LayoutPath());

        Assert.Equal(WindowLayoutState.Default, await store.LoadAsync());
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public async Task Load_OldMalformedOrNonStrictDocumentsReturnTheDefault(string json)
    {
        Directory.CreateDirectory(_directory);
        var path = LayoutPath();
        await File.WriteAllTextAsync(path, json);

        var loaded = await new JsonWindowLayoutStore(path).LoadAsync();

        Assert.Equal(WindowLayoutState.Default, loaded);
    }

    [Fact]
    public async Task Load_ClampsFiniteWidthsFromAValidSchemaOneDocument()
    {
        Directory.CreateDirectory(_directory);
        var path = LayoutPath();
        await File.WriteAllTextAsync(
            path,
            """
            { "schemaVersion": 1, "inspectorPaneWidth": 1200 }
            """);

        var loaded = await new JsonWindowLayoutStore(path).LoadAsync();

        Assert.Equal(InspectorPaneLayout.MaximumInspectorWidth, loaded.InspectorPaneWidth);
    }

    [Fact]
    public async Task Load_AnOversizedFileReturnsTheDefault()
    {
        Directory.CreateDirectory(_directory);
        var path = LayoutPath();
        await File.WriteAllTextAsync(path, new string(' ', JsonWindowLayoutStore.MaximumFileBytes + 1));

        Assert.Equal(WindowLayoutState.Default, await new JsonWindowLayoutStore(path).LoadAsync());
    }

    [Fact]
    public async Task ConcurrentSaves_AreSerializedAndLeaveOneCompleteAtomicFile()
    {
        var path = LayoutPath();
        var store = new JsonWindowLayoutStore(path);
        var widths = Enumerable.Range(0, 40).Select(index => 400d + index).ToArray();

        await Task.WhenAll(widths.Select(width => store.SaveAsync(new WindowLayoutState(width))));

        var loaded = await store.LoadAsync();
        Assert.Contains(loaded.InspectorPaneWidth, widths);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            [Path.GetFileName(path)],
            Directory.EnumerateFiles(_directory).Select(Path.GetFileName));
    }

    public static TheoryData<string> InvalidDocuments => new()
    {
        "",
        "not json",
        "[]",
        "{}",
        "{ \"schemaVersion\": 0, \"inspectorPaneWidth\": 500 }",
        "{ \"schemaVersion\": 2, \"inspectorPaneWidth\": 500 }",
        "{ \"schemaVersion\": 1 }",
        "{ \"schemaVersion\": 1, \"inspectorPaneWidth\": \"500\" }",
        "{ \"schemaVersion\": 1, \"inspectorPaneWidth\": 500, \"extra\": true }",
        "{ \"schemaVersion\": 1, \"schemaVersion\": 1, \"inspectorPaneWidth\": 500 }",
        "{ \"schemaVersion\": 1, \"inspectorPaneWidth\": 1e400 }",
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string LayoutPath() => Path.Combine(_directory, "window-layout.json");
}
