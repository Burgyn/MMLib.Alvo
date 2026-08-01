using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// One running standalone host, started through <see cref="AlvoHost"/>'s own two methods over
/// <see cref="TestServer"/> — never a hand-rolled <c>WebApplication</c>.
/// </summary>
/// <remarks>
/// The composition <em>is</em> the thing under test: a fixture that assembled its own pipeline would go on
/// passing after <see cref="AlvoHost.BuildAsync"/> stopped applying the descriptor, stopped mapping the Data
/// API, or stopped registering the exception handler. Configuration arrives as an in-memory source keyed
/// exactly as the container's environment variables are, so a fact about <c>Alvo:Database:Provider</c> is a
/// fact about <c>Alvo__Database__Provider</c>.
/// </remarks>
internal sealed class AlvoHostWorld : IAsyncDisposable
{
    internal const string AdminKeyId = "host-admin";
    internal const string AdminSecret = "host-admin-secret";
    internal const string ApiKeyHeader = "X-Alvo-Api-Key";
    internal const string DefaultDescriptorFileName = "host-boot.alvo.json";

    private readonly WebApplication _app;
    private readonly string _databasePath;

    private AlvoHostWorld(WebApplication app, string databasePath, CapturingLoggerProvider logs)
    {
        _app = app;
        _databasePath = databasePath;
        Logs = logs;
        Client = app.GetTestClient();
    }

    internal HttpClient Client { get; }

    internal CapturingLoggerProvider Logs { get; }

    /// <summary>Starts one host over the named descriptor.</summary>
    /// <param name="descriptor">
    /// A bare file name, resolved under this project's <c>descriptors/</c> output directory, or an already
    /// rooted path — which is how a fact points at a descriptor that deliberately does not exist.
    /// </param>
    /// <param name="overrides">Configuration keys to overlay; a <see langword="null"/> value unsets one.</param>
    internal static async Task<AlvoHostWorld> StartAsync(
        string descriptor = DefaultDescriptorFileName,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var descriptorPath = Path.IsPathRooted(descriptor) ? descriptor : DescriptorPath(descriptor);
        var databasePath = Path.Combine(Path.GetTempPath(), $"alvo-host-tests-{Guid.NewGuid():N}.db");
        var logs = new CapturingLoggerProvider();
        var settings = Settings(descriptorPath, databasePath, overrides);

        var builder = AlvoHost.CreateBuilder(
            [], configuration => configuration.AddInMemoryCollection(settings));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.WebHost.UseTestServer();

        var app = await AlvoHost.BuildAsync(builder, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new AlvoHostWorld(app, databasePath, logs);
    }

    private static Dictionary<string, string?> Settings(
        string descriptorPath,
        string databasePath,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Alvo:DescriptorPath"] = descriptorPath,
            ["Alvo:Database:Provider"] = "sqlite",
            ["Alvo:Database:SqliteConnectionString"] = $"Data Source={databasePath}",
            ["Alvo:Auth:DevKeys:0:KeyId"] = AdminKeyId,
            ["Alvo:Auth:DevKeys:0:Secret"] = AdminSecret,
            ["Alvo:Auth:DevKeys:0:User"] = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            ["Alvo:Auth:DevKeys:0:Roles:0"] = "admin",
            ["Alvo:Auth:DevKeys:0:Roles:1"] = "authenticated",
            ["Alvo:Auth:DevKeys:0:Scopes:0"] = "*:read",
            ["Alvo:Auth:DevKeys:0:Scopes:1"] = "*:write",
        };

        Apply(overrides, settings);

        return settings;
    }

    /// <summary>Overlays <paramref name="overrides"/>, where a <see langword="null"/> value unsets the key.</summary>
    /// <remarks>
    /// A key left <em>present</em> with a null value is not "not configured": a configuration provider still
    /// reports it as a child key, so <c>Alvo:Auth:DevKeys:0:KeyId = null</c> binds a dev key with an empty
    /// <c>KeyId</c>, which the core's own startup validation refuses. A fact about a host with no credential
    /// would then measure an options-validation failure instead of the default-deny it names, so removal is
    /// the only spelling of "unset" a test can rely on.
    /// </remarks>
    private static void Apply(IReadOnlyDictionary<string, string?>? overrides, Dictionary<string, string?> settings)
    {
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>(StringComparer.Ordinal))
        {
            if (value is null)
            {
                settings.Remove(key);
            }
            else
            {
                settings[key] = value;
            }
        }
    }

    internal static string DescriptorPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "descriptors", fileName);

    /// <summary>
    /// The simple names of every <c>IExceptionHandler</c> the host registered, read off the container rather
    /// than off the composition's source — a fact about the source would pass by restating the code.
    /// </summary>
    internal IReadOnlyList<string> ExceptionHandlerTypeNames() =>
        [.. _app.Services.GetServices<Microsoft.AspNetCore.Diagnostics.IExceptionHandler>()
            .Select(handler => handler.GetType().Name)];

    internal Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, body: null);

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, $"{AdminKeyId}.{AdminSecret}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal async Task<HttpResponseMessage> SendAnonymouslyAsync(
        HttpMethod method, string path, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
        TryDeleteDatabase();
    }

    private void TryDeleteDatabase()
    {
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Every log record the host wrote, so a fact can assert a warning was actually delivered.</summary>
/// <remarks>
/// Deviation 34's stated cost is that "with no logging <em>provider</em> configured the warning is dropped
/// silently". A standalone host configures providers, so that cost is observable here and nowhere else —
/// which is why this is a provider rather than an assertion on <c>ILogger</c> being resolvable.
/// </remarks>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LoggedRecord> _records = [];

    /// <summary>Every record as <c>Level: message</c> — the form a fact about a warning's text reads best.</summary>
    internal IReadOnlyList<string> Records => [.. Entries.Select(entry => $"{entry.Level}: {entry.Message}")];

    /// <summary>
    /// The same records with the <see cref="Exception"/> each one carried, for the one claim the formatted
    /// message cannot express: that a failure was logged <em>as</em> a failure, stack trace and all, rather
    /// than flattened into prose.
    /// </summary>
    internal IReadOnlyList<LoggedRecord> Entries
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void Record(LogLevel level, string message, Exception? exception)
    {
        lock (_records)
        {
            _records.Add(new LoggedRecord(level, message, exception));
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Record(logLevel, formatter(state, exception), exception);
        }
    }
}

/// <summary>One log record the host wrote.</summary>
/// <param name="Level">The level it was written at.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="Exception">The failure it carried, or <see langword="null"/> for an ordinary record.</param>
internal sealed record LoggedRecord(LogLevel Level, string Message, Exception? Exception);
