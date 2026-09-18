using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Turns an HTTP request into a resolved caller and gates it against the presented key's scopes,
/// before the endpoint delegate touches <c>IAlvoData</c>.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IEndpointFilter"/> and not middleware: a filter is attached to <em>one</em> endpoint,
/// so it knows which entity and which <see cref="DataOperation"/> it is guarding without reading the
/// route back out of the request, and it cannot accidentally be ordered after routing has already
/// dispatched. Middleware would have to re-derive both from the path, which is the catch-all design
/// this feature refuses.
/// </para>
/// <para>
/// <b>Three outcomes, three different diagnoses, and the distinction is the contract.</b>
/// </para>
/// <list type="bullet">
///   <item>
///   <b>No key at all → anonymous, never 401.</b> Alvo has a real <see cref="Role.Anon"/> and every
///   policy is default-deny, so a caller with no credential is a caller whose policy happens to permit
///   nothing — the policy engine inside the port answers, and an entity whose rules do admit
///   <c>anon</c> (public reference data) keeps working. 401 would make that impossible and would send
///   an agent to fix a credential when the answer is a rule. <b>No principal is published for such a
///   caller</b>: an anonymous caller has no key, so there is nothing for an
///   <see cref="AlvoPrincipal"/> to describe, and inventing one with a sentinel <c>KeyId</c> would make
///   every later reader of <see cref="IAlvoContextAccessor"/> need to know the convention.
///   <see cref="AlvoContext.Anonymous"/> already says it.
///   </item>
///   <item>
///   <b>A key that was presented and cannot be used → 401.</b> Unknown, revoked, expired, malformed,
///   or a requested tenant the key was not issued for: <see cref="IAlvoContextResolver"/> returns
///   <see langword="null"/> for all of them, deliberately indistinguishably. The caller believes they
///   hold a credential and do not, and that is a different fix from a policy denial.
///   </item>
///   <item>
///   <b>A resolved key whose scopes exclude the operation → 403, before any row is touched.</b> The
///   refusal names neither the entity nor the row, so it cannot answer "does this exist" for a caller
///   whose scopes keep them out.
///   </item>
/// </list>
/// <para>
/// <b>The scope gate runs only for a presented key.</b> <see cref="ScopeGate"/>'s rule is that an
/// empty scope set denies everything — correct for a key, since a scopeless key would be the
/// all-powerful <c>service_role</c> anti-pattern renamed — but an anonymous caller has no key and
/// therefore no scopes to narrow, so applying it there would deny every anonymous request
/// unconditionally and make a descriptor's <c>anon</c> rules unreachable. Nothing is weakened by
/// skipping it: for an anonymous caller the policy is the whole answer, and the policy is default-deny.
/// </para>
/// <para>
/// <b>The tenant header is inert for an anonymous caller.</b> A requested tenant is only ever honoured
/// as confirmation of the tenant the key itself was issued for (<see cref="TenantResolver"/>), so with
/// no key there is nothing to confirm it against; it is not read, rather than trusted.
/// <see cref="AlvoContext.Anonymous"/> carries no tenant, so every tenant-scoped entity denies.
/// </para>
/// </remarks>
internal sealed class AlvoContextFilter : IEndpointFilter
{
    private readonly string _entity;
    private readonly DataOperation _operation;
    private readonly IAlvoContextResolver _resolver;
    private readonly IAlvoContextAccessor _accessor;
    private readonly ScopeGate _scopeGate;
    private readonly IOptions<AlvoAuthOptions> _authOptions;

    internal AlvoContextFilter(
        string entity,
        DataOperation operation,
        IAlvoContextResolver resolver,
        IAlvoContextAccessor accessor,
        ScopeGate scopeGate,
        IOptions<AlvoAuthOptions> authOptions)
    {
        _entity = entity;
        _operation = operation;
        _resolver = resolver;
        _accessor = accessor;
        _scopeGate = scopeGate;
        _authOptions = authOptions;
    }

    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var options = _authOptions.Value;
        var request = context.HttpContext.Request;
        var presentedKey = CallerResolution.Presented(request, options.HeaderName);
        if (presentedKey is null)
        {
            // No credential, so no principal: there is no key to describe. The endpoint reads
            // AlvoContext.Anonymous from the accessor's absence, and the policy decides.
            return await Publishing(principal: null, context, next).ConfigureAwait(false);
        }

        var principal = await CallerResolution.ResolveAsync(_resolver, presentedKey, request, options)
            .ConfigureAwait(false);
        if (principal is null)
        {
            return ProblemResultFactory.Unauthenticated(options.HeaderName);
        }

        return _scopeGate.Allows(principal, _entity, _operation)
            ? await Publishing(principal, context, next).ConfigureAwait(false)
            : ProblemResultFactory.ScopeRefused();
    }

    /// <summary>Publishes the caller for the endpoint delegate and takes it away again.</summary>
    /// <remarks>
    /// The mechanics are <see cref="CallerResolution.PublishingAsync"/>'s, which is also what the Management
    /// API's own filter calls: a caller left published on a thread that is later reused is one defect, so it
    /// has one implementation.
    /// </remarks>
    /// <param name="principal">The resolved caller, or <see langword="null"/> for an anonymous one.</param>
    /// <param name="context">The invocation being filtered.</param>
    /// <param name="next">The rest of the pipeline.</param>
    private ValueTask<object?> Publishing(
        AlvoPrincipal? principal, EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        CallerResolution.PublishingAsync(_accessor, principal, context, next);
}

/// <summary>
/// Builds one <see cref="AlvoContextFilter"/> per mapped endpoint, from the singletons the auth feature
/// registered.
/// </summary>
/// <remarks>
/// A factory rather than a DI-resolved filter type: the filter needs the entity and the
/// <see cref="DataOperation"/> its endpoint stands for, which are mapping-time facts and not services.
/// Resolving the auth services here — once, at mapping time — also keeps the filter free of a
/// per-request service lookup.
/// </remarks>
/// <param name="resolver">Resolves a presented credential into a principal.</param>
/// <param name="accessor">Publishes the resolved caller for the endpoint delegate.</param>
/// <param name="scopeGate">Gates an operation against the presented key's scopes.</param>
/// <param name="authOptions">Carries the header names a credential and a requested tenant are read from.</param>
internal sealed class AlvoContextFilterFactory(
    IAlvoContextResolver resolver,
    IAlvoContextAccessor accessor,
    ScopeGate scopeGate,
    IOptions<AlvoAuthOptions> authOptions)
{
    /// <summary>The filter guarding one entity's one operation.</summary>
    /// <param name="entity">The entity the endpoint targets.</param>
    /// <param name="operation">The operation the endpoint performs.</param>
    internal AlvoContextFilter For(string entity, DataOperation operation) =>
        new(entity, operation, resolver, accessor, scopeGate, authOptions);
}
