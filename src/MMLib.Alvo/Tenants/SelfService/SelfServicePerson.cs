using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The person gate of the self-service routes (S3 plan D7) — tenants, members, invitations and tokens: only a signed-in
/// person, from a session, reaches them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credential is read by <see cref="CallerResolution.PersonBearer"/></b>, the one reading every person-only route
/// shares, and a Bearer token is resolved by the registered person resolver with no requested tenant — the route's path
/// names the tenant, so the tenant header is ignored here.
/// </para>
/// <list type="table">
///   <listheader><term>Presented</term><description>Answer</description></listheader>
///   <item>
///   <term>nothing usable, or an API key of any kind</term>
///   <description>401 with the plain Bearer challenge (RFC 6750 §3.1): Bearer is this route's only scheme (#542 item 2)</description>
///   </item>
///   <item><term>a malformed Bearer, or an API key beside one</term><description>401 <c>invalid_request</c>, resolving nothing</description></item>
///   <item>
///   <term>a personal or service token as a Bearer</term>
///   <description>401 <c>invalid_token</c> naming the API-key header, resolving nothing (#539)</description>
///   </item>
///   <item><term>a Bearer token that does not hold</term><description>401 <c>invalid_token</c></description></item>
///   <item>
///   <term>a Bearer a host's own person resolver resolves to anything but a person</term>
///   <description>403 <c>forbidden</c>: authenticated, and insufficient (RFC 9110 §15.5.4)</description>
///   </item>
/// </list>
/// </remarks>
internal static class SelfServicePerson
{
    private const string InvalidToken = "invalid_token";

    /// <summary>
    /// The person the request's Bearer token names, with the session it was resolved from (<see cref="PersonSessionKey"/>;
    /// <see langword="null"/> from a host's own person resolver), or the refusal to answer instead.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <returns>Exactly one of the person and the refusal.</returns>
    internal static async ValueTask<(SessionPerson? Person, IResult? Refusal)> AdmitAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var services = http.RequestServices;
        var resolvers = services.GetRequiredService<CallerResolvers>();
        var auth = services.GetRequiredService<IOptions<AlvoAuthOptions>>().Value;
        var (token, refusal) = CallerResolution.PersonBearer(http.Request, auth, resolvers.HasPerson);
        return token is null
            ? (null, refusal)
            : await ResolveAsync(resolvers.Person(http), token, http).ConfigureAwait(false);
    }

    private static async ValueTask<(SessionPerson? Person, IResult? Refusal)> ResolveAsync(
        IAlvoContextResolver resolver, string token, HttpContext http)
    {
        var principal = await resolver.ResolveAsync(token, requestedTenant: null, http.RequestAborted).ConfigureAwait(false);
        return principal?.Context switch
        {
            null => (null, ProblemResultFactory.BearerUnauthenticated(InvalidToken)),
            { ActorKind: AlvoActorKind.User } person => (new SessionPerson(person.User, PersonSessionKey.SessionOf(principal.KeyId)), null),
            _ => (null, ProblemResultFactory.NotAPerson()),
        };
    }
}

/// <summary>An admitted person and the session their Bearer token was resolved from.</summary>
/// <param name="User">The person.</param>
/// <param name="Session">The session, or <see langword="null"/> when the resolver names none.</param>
internal readonly record struct SessionPerson(UserId User, Guid? Session);
