using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The second operator applies once, after the model has read the descriptor and before its next request.</summary>
public sealed class InterferingChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A history in which the model has read the descriptor.</summary>
    private static ChatMessage[] AfterRead =>
    [
        new(ChatRole.User, "hi"),
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "get_descriptor")]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", "{}")]),
    ];

    [Fact]
    public async Task It_interferes_once_before_the_first_request_that_follows_a_descriptor_read()
    {
        var model = new CountingModel();
        var requestsSeenAtInterference = new List<int>();
        using var client = new InterferingChatClient(model, _ =>
        {
            requestsSeenAtInterference.Add(model.Requests);
            return Task.CompletedTask;
        });
        await DrainAsync(client, [new ChatMessage(ChatRole.User, "hi")]);
        await DrainAsync(client, AfterRead);
        await DrainAsync(client, AfterRead);

        requestsSeenAtInterference.ShouldBe([1]);
    }

    [Fact]
    public async Task It_interferes_on_a_non_streaming_request_too()
    {
        var model = new CountingModel();
        var interferences = 0;
        using var client = new InterferingChatClient(model, _ =>
        {
            interferences++;
            return Task.CompletedTask;
        });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);
        await client.GetResponseAsync(AfterRead, cancellationToken: Ct);
        await client.GetResponseAsync(AfterRead, cancellationToken: Ct);

        interferences.ShouldBe(1);
        model.Requests.ShouldBe(3);
    }

    private static async Task DrainAsync(InterferingChatClient client, ChatMessage[] messages)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: Ct))
        {
        }
    }

    /// <summary>A model that counts the requests it was sent and answers each with one text update.</summary>
    private sealed class CountingModel : IChatClient
    {
        internal int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
