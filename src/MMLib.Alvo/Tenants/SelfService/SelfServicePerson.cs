using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The person gate of the self-service routes (S3 plan D7): only a signed-in person, from a session, manages tenants.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credential is read as the Data API reads it</b>, by <see cref="CallerResolution.Read"/>, and a Bearer token is
/// resolved by the registered person resolver with no requested tenant — the route's path names the tenant, so the tenant
/// header is ignored here.
/// </para>
/// <list type="table">
///   <listheader><term>Presented</term><description>Answer</description></listheader>
///   <item><term>nothing usable</term><description>401 with the plain Bearer challenge (RFC 6750 §3.1)</description></item>
///   <item><term>a malformed or ambiguous Bearer</term><description>401 <c>invalid_request</c>, resolving nothing</description></item>
///   <item><term>a Bearer token that does not hold</term><description>401 <c>invalid_token</c></description></item>
///   <item>
///   <term>an API key, or a Bearer resolving to anything but a person</term>
///   <description>403 <c>forbidden</c>: managing who is in a tenant is a person's act, not an integration's (spec §6.3)</description>
///   </item>
/// </list>
/// </remarks>
internal static class SelfServicePerson
{
    private const string InvalidToken = "invalid_token";

    private const string InvalidRequest = "invalid_request";

    private const string NotAPerson =
        "Only a signed-in person manages tenants. Present the access token sign-in issued as 'Authorization: Bearer "
        + "<token>'; an API key or a token minted for an integration does not reach this route.";

    /// <summary>The person the request's Bearer token names, or the refusal to answer instead.</summary>
    /// <param name="http">The request.</param>
    /// <returns>Exactly one of the person and the refusal.</returns>
    internal static async ValueTask<(UserId? Person, IResult? Refusal)> AdmitAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var services = http.RequestServices;
        var resolvers = services.GetRequiredService<CallerResolvers>();
        var auth = services.GetRequiredService<IOptions<AlvoAuthOptions>>().Value;
        var credential = CallerResolution.Read(http.Request, auth, CredentialSurface.Data, personRegistered: resolvers.HasPerson);
        return credential.Channel switch
        {
            CredentialChannel.Bearer => await ResolveAsync(resolvers.Person(http), credential.Value!, http).ConfigureAwait(false),
            CredentialChannel.ApiKey => (null, ProblemResultFactory.PersonForbidden(NotAPerson)),
            CredentialChannel.MalformedBearer or CredentialChannel.Ambiguous =>
                (null, ProblemResultFactory.BearerUnauthenticated(InvalidRequest)),
            _ => (null, ProblemResultFactory.BearerRequired()),
        };
    }

    private static async ValueTask<(UserId? Person, IResult? Refusal)> ResolveAsync(
        IAlvoContextResolver resolver, string token, HttpContext http)
    {
        var principal = await resolver.ResolveAsync(token, requestedTenant: null, http.RequestAborted).ConfigureAwait(false);
        return principal?.Context switch
        {
            null => (null, ProblemResultFactory.BearerUnauthenticated(InvalidToken)),
            { ActorKind: AlvoActorKind.User } person => (person.User, null),
            _ => (null, ProblemResultFactory.PersonForbidden(NotAPerson)),
        };
    }
}
