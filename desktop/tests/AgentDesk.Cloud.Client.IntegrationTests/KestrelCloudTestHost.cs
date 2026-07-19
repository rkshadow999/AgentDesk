using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentDesk.Cloud.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentDesk.Cloud.Client.IntegrationTests;

internal sealed class KestrelCloudTestHost : IAsyncDisposable
{
    private readonly string _root;
    private readonly CapturingLoggerProvider _logs;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _httpClient;
    private bool _stopped;

    private KestrelCloudTestHost(
        string root,
        string bootstrapToken,
        CapturingLoggerProvider logs,
        WebApplicationFactory<Program> factory,
        HttpClient httpClient)
    {
        _root = root;
        BootstrapToken = bootstrapToken;
        _logs = logs;
        _factory = factory;
        _httpClient = httpClient;
        BaseAddress = httpClient.BaseAddress ?? throw new InvalidOperationException(
            "The Kestrel test client did not expose a base address.");
    }

    public Uri BaseAddress { get; }

    public string BootstrapToken { get; }

    public static async Task<KestrelCloudTestHost> StartAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-cloud-client-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var bootstrapToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var logs = new CapturingLoggerProvider();
        var factory = new ConfiguredCloudFactory(root, bootstrapToken, logs);
        factory.UseKestrel(0);
        factory.StartServer();
        var addresses = factory.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = addresses?.SingleOrDefault(value => value.StartsWith(
            "http://",
            StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
            "Kestrel did not publish its dynamic loopback address.");
        var httpClient = new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
            })
        {
            BaseAddress = new Uri(address.TrimEnd('/') + '/', UriKind.Absolute),
        };
        var host = new KestrelCloudTestHost(
            root,
            bootstrapToken,
            logs,
            factory,
            httpClient);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var ready = await httpClient.GetAsync("health/ready", timeout.Token);
            ready.EnsureSuccessStatusCode();
            if (!host.BaseAddress.IsLoopback)
            {
                throw new InvalidOperationException("The Kestrel test server is not bound to loopback.");
            }
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public IAgentDeskCloudClient CreateCloudClient(string accessToken)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        return new AgentDeskCloudClient(
            _httpClient,
            new CloudConnectionOptions(
                BaseAddress,
                requestTimeout: TimeSpan.FromSeconds(10)),
            new StaticAccessTokenProvider(accessToken));
    }

    public ICloudNotificationClient CreateNotificationClient(
        string accessToken,
        string deviceId)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        return new SignalRCloudNotificationClient(
            new CloudConnectionProfile(BaseAddress, "default", deviceId),
            new StaticAccessTokenProvider(accessToken));
    }

    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        _httpClient.Dispose();
        await _factory.DisposeAsync();
    }

    public void AssertArtifactsDoNotContain(IReadOnlyCollection<string> sensitiveValues)
    {
        if (!_stopped)
        {
            throw new InvalidOperationException("Stop Kestrel before inspecting persisted artifacts.");
        }

        var logText = string.Join('\n', _logs.Messages);
        foreach (var value in sensitiveValues)
        {
            Assert.DoesNotContain(value, logText, StringComparison.Ordinal);
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var bytes = ReadSharedFile(file);
            foreach (var value in sensitiveValues)
            {
                Assert.DoesNotContain(Encoding.UTF8.GetBytes(value), bytes);
                Assert.DoesNotContain(Encoding.Unicode.GetBytes(value), bytes);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await DeleteTemporaryDirectoryAsync(_root);
    }

    private static byte[] ReadSharedFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static async Task DeleteTemporaryDirectoryAsync(string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar);
        var expectedParent = Path.GetDirectoryName(normalizedRoot);
        if (!string.Equals(expectedParent, temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(normalizedRoot).StartsWith(
                "agentdesk-cloud-client-e2e-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(normalizedRoot))
                {
                    Directory.Delete(normalizedRoot, recursive: true);
                }
                return;
            }
            catch (Exception exception) when (
                attempt < 4 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
            }
        }
    }

    private sealed class ConfiguredCloudFactory(
        string root,
        string bootstrapToken,
        CapturingLoggerProvider logs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AgentDeskCloud:BootstrapToken"] = bootstrapToken,
                        ["AgentDeskCloud:DatabasePath"] = Path.Combine(root, "cloud.db"),
                        ["AgentDeskCloud:RequireHttps"] = "false",
                        ["AgentDeskCloud:AutomationPollingIntervalSeconds"] = "1",
                    }));
            builder.ConfigureLogging(
                logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(logs);
                });
        }
    }

    private sealed class StaticAccessTokenProvider(string accessToken) :
        ICloudAccessTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(accessToken);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(
            categoryName,
            _messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string categoryName,
            ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                messages.Enqueue(
                    exception is null
                        ? $"{logLevel}:{categoryName}:{eventId.Id}:{message}"
                        : $"{logLevel}:{categoryName}:{eventId.Id}:{message}{Environment.NewLine}{exception}");
            }
        }
    }
}
