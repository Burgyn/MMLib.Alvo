using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Spec §5.7's table, read off one request: which credential a caller presented, decided once for the Data API,
/// the command gate and the Management API.
/// </summary>
/// <remarks>
/// The rows are the table's own cells plus the plan's D7 — the scheme compared case-insensitively, a non-Bearer
/// scheme ignored, a repeated or whitespace-bearing Bearer malformed rather than "take the first". The enum names
/// travel as text because the reading's types are internal and a public theory cannot name them.
/// </remarks>
public sealed class CredentialReadingTests
{
    private static readonly AlvoAuthOptions _options = new();

    public static TheoryData<string, bool, string?, string[], string, string?> Readings() => new()
    {
        { "Data", false, null, ["Bearer t"], "None", null },
        { "Data", false, "k", ["Bearer t"], "ApiKey", "k" },
        { "Data", true, null, ["Bearer t"], "Bearer", "t" },
        { "Data", true, null, ["bearer t"], "Bearer", "t" },
        { "Data", true, null, ["Basic dXNlcjpwYXNz"], "None", null },
        { "Data", true, "k", ["Basic dXNlcjpwYXNz"], "ApiKey", "k" },
        { "Data", true, "k", ["Bearer t"], "Ambiguous", null },
        { "Data", true, "k", ["Bearer"], "Ambiguous", null },
        { "Data", true, "k", ["Bearer a b"], "Ambiguous", null },
        { "Data", true, null, ["Bearer"], "MalformedBearer", null },
        { "Data", true, null, ["Bearer a b"], "MalformedBearer", null },
        { "Data", true, null, ["Bearer a", "Bearer b"], "MalformedBearer", null },
        { "Data", true, null, ["Basic x", "Bearer t"], "MalformedBearer", null },
        { "Data", true, null, [], "None", null },
        { "Management", true, null, ["Bearer t"], "None", null },
        { "Management", true, "k", ["Bearer t"], "ApiKey", "k" },
    };

    [Theory, MemberData(nameof(Readings))]
    public void A_request_presents_exactly_one_reading(
        string surface, bool personRegistered, string? key, string[] authorization, string channel, string? value)
    {
        var request = Request(_options.HeaderName, key, authorization);

        var reading = CallerResolution.Read(
            request, _options, Enum.Parse<CredentialSurface>(surface), personRegistered);

        reading.ShouldBe(new PresentedCredential(Enum.Parse<CredentialChannel>(channel), value));
    }

    /// <summary>
    /// Review Focus 2: a host that moved the API key into <c>Authorization</c> keeps that header on the API-key
    /// channel even once a person resolver is registered — the person resolver never sees it.
    /// </summary>
    [Fact]
    public void An_api_key_header_named_authorization_keeps_the_header_from_the_person_channel()
    {
        var options = new AlvoAuthOptions { HeaderName = "Authorization" };
        var request = Request(options.HeaderName, "Bearer k", []);

        var reading = CallerResolution.Read(request, options, CredentialSurface.Data, personRegistered: true);

        reading.ShouldBe(new PresentedCredential(CredentialChannel.ApiKey, "Bearer k"));
    }

    /// <summary>
    /// D6's fail-safe: a container that cannot say whether a keyed service exists has no person resolver, so the
    /// bearer channel stays closed — today's behaviour — rather than open on a guess.
    /// </summary>
    [Fact]
    public void A_container_that_cannot_answer_keyed_questions_has_no_person_resolver()
    {
        var resolvers = new CallerResolvers(new RefusingResolver(), new NoKeyedQuestions());

        resolvers.HasPerson.ShouldBeFalse();
    }

    /// <summary>The control for the fact above: a container that registers the key does have one.</summary>
    [Fact]
    public void A_container_with_a_resolver_under_the_person_key_has_a_person_resolver()
    {
        using var services = new ServiceCollection()
            .AddKeyedSingleton<IAlvoContextResolver>(CallerResolution.PersonResolverKey, new RefusingResolver())
            .BuildServiceProvider();

        new CallerResolvers(new RefusingResolver(), services).HasPerson.ShouldBeTrue();
    }

    private static HttpRequest Request(string keyHeader, string? key, string[] authorization)
    {
        var request = new DefaultHttpContext().Request;
        if (authorization.Length > 0)
        {
            request.Headers.Authorization = new StringValues(authorization);
        }

        if (key is not null)
        {
            request.Headers[keyHeader] = key;
        }

        return request;
    }

    private sealed class RefusingResolver : IAlvoContextResolver
    {
        public ValueTask<AlvoPrincipal?> ResolveAsync(
            string? presentedKey, string? requestedTenant, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AlvoPrincipal?>(null);
    }

    private sealed class NoKeyedQuestions : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
