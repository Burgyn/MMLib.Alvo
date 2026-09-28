using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The recorder's bookkeeping: requests, tool rounds, tokens, and each call matched to its own answer.</summary>
public sealed class RecordingChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_call_id_reused_across_rounds_keeps_both_calls_and_their_own_answers()
    {
        var model = new ScriptedModel(
            [new FunctionCallContent("call_0", "get_descriptor"), new UsageContent(new UsageDetails { TotalTokenCount = 10 })],
            [new FunctionCallContent("call_0", "propose_change"), new UsageContent(new UsageDetails { TotalTokenCount = 15 })],
            [new TextContent("Done.")]);
        using var recorder = new RecordingChatClient(model);

        await DrainAsync(recorder, []);
        await DrainAsync(recorder, [Result("call_0", """{"revision":1}""")]);
        await DrainAsync(recorder, [Result("call_0", """{"valid":true}""")]);

        recorder.Requests.ShouldBe(3);
        recorder.ToolRounds.ShouldBe(2);
        recorder.Tokens.ShouldBe(25);
        recorder.Calls.Select(call => (call.Round, call.Tool, call.Result)).ShouldBe(
        [
            (1, "get_descriptor", """{"revision":1}"""),
            (2, "propose_change", """{"valid":true}"""),
        ]);
    }

    [Fact]
    public async Task A_string_kind_json_result_is_recorded_as_the_json_it_holds()
    {
        var model = new ScriptedModel([new FunctionCallContent("a", "check_change")], [new TextContent("ok")]);
        using var recorder = new RecordingChatClient(model);

        await DrainAsync(recorder, []);
        await DrainAsync(recorder, [new FunctionResultContent("a", JsonSerializer.SerializeToElement("""{"valid":false}"""))]);

        recorder.Calls.ShouldHaveSingleItem().Result.ShouldBe("""{"valid":false}""");
    }

    private static FunctionResultContent Result(string callId, string json) =>
        new(callId, JsonSerializer.SerializeToElement(json));

    private static async Task DrainAsync(RecordingChatClient client, AIContent[] carried)
    {
        ChatMessage[] messages = carried.Length == 0
            ? [new ChatMessage(ChatRole.User, "hi")]
            : [new ChatMessage(ChatRole.User, "hi"), new ChatMessage(ChatRole.Tool, [.. carried])];
        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: Ct))
        {
        }
    }

    /// <summary>A model that answers each request with the next scripted contents, as one streamed update.</summary>
    private sealed class ScriptedModel(params AIContent[][] answers) : IChatClient
    {
        private int _next;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. answers[_next++]])));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [.. answers[_next++]]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
