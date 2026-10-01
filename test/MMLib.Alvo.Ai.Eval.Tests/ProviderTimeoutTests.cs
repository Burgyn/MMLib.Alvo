using Microsoft.Extensions.AI;
using MMLib.Alvo.Management;

using NSubstitute;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>A provider that times out fails its turn; only the run's own cancellation stops the suite.</summary>
public sealed class ProviderTimeoutTests
{
    private static readonly AlvoAiConnection _connection =
        new(AiConnectionKind.OpenAiCompatible, new Uri("http://127.0.0.1:9/v1"), "m", ApiKey: null);

    [Fact]
    public async Task A_provider_timeout_is_a_failed_turn_recorded_as_timeout()
    {
        var turn = await EvalRunner.AskAsync(
            Substitute.For<IAlvoManagement>(), _connection, _ => new TimingOut(), Turns.Original, "hi",
            TestContext.Current.CancellationToken);

        turn.Updates.OfType<AssistantUpdate.Failed>().ShouldHaveSingleItem();
        turn.ProviderStatus.ShouldBe(EvalRunner.TimedOut);
        EvalRunner.Invariants(turn).Why.ShouldContain("provider status: timeout");
    }

    [Fact]
    public async Task The_runs_own_cancellation_still_stops_the_run()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => EvalRunner.AskAsync(
            Substitute.For<IAlvoManagement>(), _connection, _ => new TimingOut(), Turns.Original, "hi", cancel.Token));
    }

    /// <summary>What an HTTP client's own timeout looks like from above: a cancellation nobody asked for.</summary>
    private sealed class TimingOut : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
