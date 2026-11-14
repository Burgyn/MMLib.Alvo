using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Rules;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>One admitted self-service request: the person, the applied self-service policy, and the registry.</summary>
/// <remarks>
/// <para>
/// <b>The order of the gates is the route contract</b> (S3 plan Task 7): the person first, so a stranger learns nothing
/// about the descriptor; then the feature (<c>feature-disabled</c>); then, for a route with a body, the JSON content type
/// (415) and the body itself (422 <c>validation</c>); only then the registry.
/// </para>
/// <para>
/// <b>Services are resolved from the request</b>, never bound as handler parameters, so the routes' published operations
/// carry no inferred body or query parameter, and the policy is read once per request — the descriptor applied when the
/// request arrived decides it.
/// </para>
/// </remarks>
/// <param name="Person">The signed-in person.</param>
/// <param name="Policy">The applied self-service policy.</param>
/// <param name="Store">The tenant registry.</param>
/// <param name="Http">The request.</param>
internal sealed record SelfServiceCall(UserId Person, SelfServicePolicy Policy, ITenantStore Store, HttpContext Http)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.General);

    /// <summary>Gets the token that cancels the request.</summary>
    internal CancellationToken Ct => Http.RequestAborted;

    /// <summary>Gets the instant the request acts at, from the host's clock.</summary>
    internal DateTimeOffset Now => Http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

    /// <summary>Admits the request's person and checks the route's feature, or answers the refusal instead.</summary>
    /// <param name="http">The request.</param>
    /// <param name="feature">The feature check: the <c>feature-disabled</c> answer, or <see langword="null"/> when it is on.</param>
    internal static async ValueTask<(SelfServiceCall? Call, IResult? Refusal)> EnterAsync(
        HttpContext http, Func<SelfServicePolicy, IResult?> feature)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(feature);
        var (person, refusal) = await SelfServicePerson.AdmitAsync(http).ConfigureAwait(false);
        if (refusal is not null)
        {
            return (null, refusal);
        }

        var services = http.RequestServices;
        var policy = services.GetRequiredService<IPolicyCatalogProvider>().Current?.SelfService ?? SelfServicePolicy.None;
        return feature(policy) is { } disabled
            ? (null, disabled)
            : (new SelfServiceCall(person!.Value, policy, services.GetRequiredService<ITenantStore>(), http), null);
    }

    /// <summary>
    /// The person's own membership of <paramref name="tenant"/> when the applied descriptor honours it (S3 plan D5), else
    /// <see langword="null"/> — which every tenant-scoped route answers as <see cref="SelfServiceProblems.NoSuchTenant"/>, so
    /// a membership that counts on no Data API request (a personal one while <c>personalTenant</c> is off) is managed by
    /// no route either.
    /// </summary>
    /// <param name="tenant">The tenant the route names.</param>
    internal async Task<StoredMembership?> MembershipAsync(TenantId tenant) =>
        await Store.FindMembershipAsync(Person, tenant, Ct).ConfigureAwait(false) is { } own
        && MembershipRules.Honours(own, Policy)
            ? own
            : null;

    /// <summary>Reads the request's JSON body with <paramref name="reader"/>, or answers 415 or 422 instead.</summary>
    /// <typeparam name="T">What the body holds.</typeparam>
    /// <param name="reader">Reads the body under the Data API's bounds.</param>
    internal async Task<(T? Value, IResult? Refusal)> ReadAsync<T>(
        Func<HttpRequest, AlvoApiOptions, CancellationToken, Task<(T? Value, IReadOnlyList<AlvoViolation> Violations)>> reader)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (JsonContentType.Refuse(Http.Request) is { } unsupported)
        {
            return (null, unsupported);
        }

        var options = Http.RequestServices.GetRequiredService<IOptions<AlvoApiOptions>>().Value;
        var (value, violations) = await reader(Http.Request, options, Ct).ConfigureAwait(false);
        return value is null ? (null, ProblemResultFactory.Validation(violations)) : (value, null);
    }

    /// <summary>
    /// <paramref name="body"/> as <c>application/json</c>, written with this type's own serializer options so a host's
    /// naming policy cannot rename a member a client reads (the <c>UserResponse</c> precedent).
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <param name="status">The status code.</param>
    internal static IResult Json(object body, int status = StatusCodes.Status200OK) =>
        Results.Json(body, _json, contentType: "application/json", statusCode: status);
}
