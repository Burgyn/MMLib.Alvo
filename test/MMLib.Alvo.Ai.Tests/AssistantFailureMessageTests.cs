using System.ClientModel;
using System.ClientModel.Primitives;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// What a failed turn tells the operator, decided from the exception alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The live defect this pins</b> (reported 24 Sep 2026, driving the bike-workshop demo with an OpenAI
/// key): a turn failed with <c>System.ClientModel.ClientResultException: HTTP 401 (invalid_request_error:
/// invalid_api_key)</c> — the endpoint answered and refused the key — and the operator read "The AI endpoint
/// did not answer", which is what a socket that never got a reply says. The host log had the real answer;
/// the screen did not.
/// </para>
/// <para>
/// <b>Pure and static, so no test here dials anything.</b> <see cref="AlvoAssistant.FailureMessage"/> decides
/// everything from the exception and the model name already resolved for the turn, and
/// <see cref="ClientResultException"/> is built from a hand-written <see cref="PipelineResponse"/> rather than
/// a real HTTP round trip.
/// </para>
/// </remarks>
public sealed class AssistantFailureMessageTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void The_provider_refusing_the_key_says_so(int status)
    {
        var message = AlvoAssistant.FailureMessage(ClientFailure(status), "gpt-5");

        message.ShouldContain("refused the key");
        message.ShouldContain("Settings");
        message.ShouldContain("Alvo:Ai:ApiKeySecretRef");
    }

    /// <summary>The model name may appear: it is configuration, already shown on Settings.</summary>
    [Fact]
    public void No_such_model_names_the_configured_model()
    {
        var message = AlvoAssistant.FailureMessage(ClientFailure(404), "gpt-5-turbo");

        message.ShouldContain("no model called 'gpt-5-turbo'");
    }

    [Fact]
    public void A_429_says_the_key_is_rate_limited_or_out_of_quota()
    {
        var message = AlvoAssistant.FailureMessage(ClientFailure(429), "gpt-5");

        message.ShouldContain("rate-limiting");
        message.ShouldContain("quota");
    }

    [Fact]
    public void An_unrecognised_status_names_the_status_code()
    {
        var message = AlvoAssistant.FailureMessage(ClientFailure(500), "gpt-5");

        message.ShouldContain("The AI provider answered with an error (500).");
    }

    /// <summary>
    /// Something that never got a response — a wrong address, a closed socket, a timeout — keeps the old
    /// sentence, and it is true of this case in a way it was not of a 401.
    /// </summary>
    [Fact]
    public void A_call_that_never_reached_the_endpoint_still_says_it_did_not_answer()
    {
        var message = AlvoAssistant.FailureMessage(new HttpRequestException("connection refused"), "gpt-5");

        message.ShouldContain("did not answer");
    }

    /// <summary>
    /// The provider's own message routinely echoes the request — the live case this pins carried
    /// <c>invalid_api_key</c> straight from the endpoint — and none of it may reach the screen.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(503)]
    public void The_providers_own_message_never_reaches_the_sentence(int status)
    {
        var failure = ClientFailure(status, "invalid_request_error: sk-live-should-not-leak");

        AlvoAssistant.FailureMessage(failure, "gpt-5").ShouldNotContain("sk-live-should-not-leak");
    }

    private static ClientResultException ClientFailure(int status, string? providerMessage = null) =>
        providerMessage is null
            ? new ClientResultException(new FakeResponse(status), innerException: null)
            : new ClientResultException(providerMessage, new FakeResponse(status), innerException: null);

    /// <summary>
    /// The narrowest <see cref="PipelineResponse"/> that can carry a status code — everything else a real
    /// HTTP response would have is unused by <see cref="ClientResultException"/> or by the classifier under
    /// test.
    /// </summary>
    private sealed class FakeResponse(int status) : PipelineResponse
    {
        public override int Status { get; } = status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.Empty;

        protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            new(BinaryData.Empty);

        public override void Dispose()
        {
        }
    }

    /// <summary>Headers nothing here reads.</summary>
    private sealed class FakeHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            yield break;
        }
    }
}
