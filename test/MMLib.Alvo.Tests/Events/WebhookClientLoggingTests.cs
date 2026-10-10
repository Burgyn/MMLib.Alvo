using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

using System.Net;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The named webhook client, as the library registers it, writes no log line that carries the endpoint's
/// path or query — whatever level the host's logging is set to (#347).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wired through the real registration, not a stub factory.</b> <c>EventActionExecutorTests</c> pins the
/// lines Alvo itself writes, over a substituted <see cref="IHttpClientFactory"/> — which is exactly why it never
/// saw the lines <c>IHttpClientFactory</c>'s own logging handlers write under
/// <c>System.Net.Http.HttpClient.{name}.*</c>: <c>"Sending HTTP request POST https://…/path?query"</c> at
/// Information, the request URI in the structured state, and an <c>"HTTP POST …"</c> scope around it.
/// </para>
/// <para>
/// <b>The minimum level is Trace and nothing is filtered</b>, because the guarantee has to hold in an embedded
/// host whose logging configuration Alvo does not own. Messages, structured state and scopes are all read:
/// a pipeline ships every one of them.
/// </para>
/// </remarks>
public sealed class WebhookClientLoggingTests
{
    private const string SecretPath = "/services/T00000000/B11111111/Kd7xQ2vSecretToken";
    private const string SecretQuery = "token=Zq9SecretQueryValue";
    private const string SecretUrl = $"https://hooks.example.test{SecretPath}?{SecretQuery}";

    [Fact]
    public async Task A_delivery_through_the_registered_client_logs_neither_the_path_nor_the_query()
    {
        var logs = new StructuredLogCapture();
        var receiver = new AcceptingReceiver();
        await using var provider = Services(logs, receiver).BuildServiceProvider();

        await provider.GetRequiredService<WebhookDelivery>().PostAsync(
            new WebhookTarget("crm-sync", new Uri(SecretUrl)), "{}", TestContext.Current.CancellationToken);

        receiver.Targets.ShouldHaveSingleItem().AbsoluteUri.ShouldBe(
            SecretUrl, "the positive control: the secret really was on the wire this run");
        logs.Shipped.ShouldAllBe(text => !text.Contains("SecretToken", StringComparison.Ordinal));
        logs.Shipped.ShouldAllBe(text => !text.Contains("SecretQueryValue", StringComparison.Ordinal));
    }

    private static ServiceCollection Services(StructuredLogCapture logs, HttpMessageHandler receiver)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddAlvoEvents();
        services.AddHttpClient(WebhookDelivery.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
        return services;
    }

    /// <summary>
    /// A provider that keeps everything a pipeline would ship from one entry: the rendered message, each
    /// structured state value, and the rendering and values of every scope opened around it.
    /// </summary>
    private sealed class StructuredLogCapture : ILoggerProvider, ILogger
    {
        private readonly List<string> _shipped = [];
        private readonly object _gate = new();

        internal IReadOnlyList<string> Shipped
        {
            get
            {
                lock (_gate)
                {
                    return [.. _shipped];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Keep(state, formatted: state.ToString());
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Keep(state, formatted: $"{formatter(state, exception)} {exception}");
        }

        public void Dispose()
        {
            // Intentionally empty: the captured lines are read after the provider is disposed.
        }

        private void Keep<TState>(TState state, string? formatted)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.Select(pair => $"{pair.Key}={pair.Value}")
                : [];
            lock (_gate)
            {
                _shipped.Add(formatted ?? string.Empty);
                _shipped.AddRange(values);
            }
        }
    }

    /// <summary>Answers every request with 200 and records the URI it was sent to.</summary>
    private sealed class AcceptingReceiver : HttpMessageHandler
    {
        private readonly List<Uri> _targets = [];

        internal IReadOnlyList<Uri> Targets => _targets;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            _targets.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
