using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>
/// Deliberately broken hosts, so the invariants can be seen to fail.
/// </summary>
/// <remarks>
/// <para>
/// #26's definition of done asks for exactly this: "deliberately breaking idempotency/default-deny causes a
/// red test". An invariant that has only ever been observed passing says nothing about the code it watches —
/// it could be asserting over an empty set, or against a host it never reached — and this repository has
/// already paid for one gate that was green while measuring nothing.
/// </para>
/// <para>
/// Both saboteurs decorate a service Alvo has already registered, through
/// <c>AlvoApiWorldSetup.ConfigureServicesAfterAlvo</c> — every Alvo registration is a <c>TryAdd</c>, so a
/// replacement registered <em>before</em> <c>AddAlvo</c> would win the slot outright and leave the decorator
/// with nothing to wrap. The rest of the host is production wiring, which is the point: what is sabotaged is
/// one behaviour, not the world the invariant runs in.
/// </para>
/// </remarks>
internal static class Sabotage
{
    /// <summary>A host whose policy engine never denies anything.</summary>
    /// <param name="project">The project whose permissive entity supplies the substituted decision.</param>
    /// <remarks>
    /// <b>It substitutes a real allow rather than forging one, and that is deliberate.</b>
    /// <c>PolicyDecision.Allow</c> is <c>internal</c> to <c>MMLib.Alvo.Abstractions</c> — the type's own
    /// remarks say a denial is the only publicly constructible verdict — so a test that manufactured one
    /// would be reaching past the guarantee the port exists to make. Resolving the <em>permissive</em>
    /// entity's decision and returning it for the denied one needs nothing internal, and it is also why a
    /// generated permissive entity's rules are the literal <c>"true"</c>: a predicate naming one entity's
    /// column, applied to another's rows, would fail for a reason that is not the sabotage.
    /// </remarks>
    internal static AlvoApiWorldSetup PolicyAdmitsEverything(GeneratedProject project) =>
        new(ConfigureServicesAfterAlvo: services =>
            services.Decorate<IPolicyEngine>(inner => new AdmittingPolicyEngine(inner, project.PermissiveEntities[0])));

    /// <summary>A host that accepts an idempotency token and never passes it to the store.</summary>
    /// <remarks>
    /// The token is still parsed and validated at the HTTP tier, so the request looks entirely normal — this
    /// is the regression where a replay quietly writes a second row, which is the one the invariant claims to
    /// catch. Every other member forwards verbatim.
    /// </remarks>
    internal static AlvoApiWorldSetup IdempotencyForgets() =>
        new(ConfigureServicesAfterAlvo: services =>
            services.Decorate<IAlvoData>(inner => new ForgetfulAlvoData(inner)));

    /// <summary>Answers every denial with the decision a permissive entity got.</summary>
    /// <param name="inner">The real engine.</param>
    /// <param name="permissive">An entity whose rules admit every caller.</param>
    private sealed class AdmittingPolicyEngine(IPolicyEngine inner, string permissive) : IPolicyEngine
    {
        public PolicyDecision Resolve(string entity, DataOperation operation, AlvoContext context)
        {
            var decision = inner.Resolve(entity, operation, context);

            return decision.IsDenied ? inner.Resolve(permissive, operation, context) : decision;
        }
    }

    /// <summary>Forwards everything, and drops every idempotency token on the way through.</summary>
    /// <param name="inner">The real store.</param>
    private sealed class ForgetfulAlvoData(IAlvoData inner) : IAlvoData
    {
        public Task<AlvoPage> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default) =>
            inner.QueryAsync(query, context, cancellationToken);

        public Task<AlvoRecord?> GetAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default) =>
            inner.GetAsync(entity, id, context, cancellationToken);

        public Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(entity, values, context, idempotency: null, cancellationToken);

        public Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(entity, id, values, context, precondition, idempotency: null, cancellationToken);

        public Task<AlvoReplaceResult> ReplaceAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(entity, id, values, context, precondition, idempotency: null, cancellationToken);

        public Task DeleteAsync(string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(entity, id, context, precondition, idempotency: null, cancellationToken);

        public Task<AlvoBatchResult> CreateManyAsync(string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
            inner.CreateManyAsync(entity, rows, context, idempotency: null, cancellationToken);

        public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
            inner.UpdateManyAsync(entity, rows, context, idempotency: null, cancellationToken);

        public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default) =>
            inner.DeleteManyAsync(entity, ids, context, idempotency: null, cancellationToken);
    }
}
