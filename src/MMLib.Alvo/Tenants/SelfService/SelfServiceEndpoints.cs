using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// Maps the self-service routes (spec §6.3) under a sign-in group: <c>{prefix}/tenants…</c>, tagged <see cref="Tag"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the core, mapped by the sign-in package</b> (S3 plan D1): every route needs a person from a session, which exists
/// only where sign-in is registered, and the auth prefix keeps <c>tenants</c> from colliding with an entity of that name
/// under the Data API's prefix. A host that federates its own identity later maps the same routes.
/// </para>
/// <para>
/// <b>Always mapped</b>, whatever the descriptor says: with the feature off a route answers 404 <c>feature-disabled</c>
/// naming the descriptor key that enables it, so an agent that found it in the OpenAPI document learns what to declare.
/// </para>
/// <para>
/// <b>Every route carries the core's CORS filter first, then <c>Cache-Control: no-store</c></b> — each answer is about a
/// person — so a refusal is readable from an allowed origin and never cached; creating a tenant is charged the
/// <see cref="AlvoRateLimit.Create"/> budget after both.
/// </para>
/// </remarks>
internal static class SelfServiceEndpoints
{
    /// <summary>The OpenAPI tag every self-service route carries.</summary>
    internal const string Tag = "tenants";

    /// <summary>The operation id of <c>GET {prefix}/tenants</c>.</summary>
    internal const string ListOperationId = "tenants.list";

    /// <summary>The operation id of <c>POST {prefix}/tenants</c>.</summary>
    internal const string CreateOperationId = "tenants.create";

    /// <summary>The operation id of <c>PATCH {prefix}/tenants/{tenantId}</c>.</summary>
    internal const string RenameOperationId = "tenants.rename";

    private const string Collection = "/tenants";

    private const string Tenant = Collection + "/{tenantId:guid}";

    /// <summary>Maps the self-service routes into <paramref name="group"/>.</summary>
    /// <param name="group">The sign-in group, mapped at the auth prefix.</param>
    /// <param name="cors">The CORS filter each route carries first.</param>
    /// <param name="limits">The per-client budgets; creating a tenant is charged <see cref="AlvoRateLimit.Create"/>.</param>
    internal static void Map(RouteGroupBuilder group, AlvoCors cors, AlvoRateLimits limits)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(cors);
        ArgumentNullException.ThrowIfNull(limits);
        Protect(group.MapGet(Collection, (Func<HttpContext, Task<IResult>>)TenantRoutes.ListAsync), cors, ListOperationId);
        Protect(group.MapPost(Collection, (Func<HttpContext, Task<IResult>>)TenantRoutes.CreateAsync), cors, CreateOperationId)
            .AddEndpointFilter(limits.For(AlvoRateLimit.Create));
        Protect(
            group.MapPatch(Tenant, static (HttpContext http, Guid tenantId) => TenantRoutes.RenameAsync(http, tenantId)),
            cors,
            RenameOperationId);
    }

    /// <summary>CORS first, then <c>no-store</c>; then the tag and the operation id.</summary>
    private static RouteHandlerBuilder Protect(RouteHandlerBuilder route, AlvoCors cors, string operationId) =>
        route.AddEndpointFilter(cors.Filter).AddEndpointFilter(NoStoreResponseFilter.Instance).WithTags(Tag).WithName(operationId);
}
