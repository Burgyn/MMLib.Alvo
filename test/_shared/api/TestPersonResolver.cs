using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A person resolver a world registers under the master plan's fixed key, standing in for the Bearer resolver a
/// later slice ships: it admits the tokens it was given and refuses every other, and records every call.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Key"/> is spelled as a literal, on purpose.</b> Reading <c>CallerResolution.PersonResolverKey</c>
/// would make a renamed key pass here while every host registering under the published one stopped working; the
/// literal pins the key the master plan fixed.
/// </para>
/// <para>
/// <b><see cref="Calls"/> is the witness that matters</b> for the refusal facts: a request Alvo must answer before
/// any resolver runs (ambiguous, malformed) or must never hand to this resolver (Management, a host's own scheme)
/// is proved by an empty queue, not by its status alone.
/// </para>
/// </remarks>
internal sealed class TestPersonResolver : IAlvoContextResolver
{
    /// <summary>The key a person resolver is registered under.</summary>
    internal const string Key = "alvo-person";

    private readonly Dictionary<string, AlvoPrincipal> _people;

    /// <summary>Creates a resolver that admits exactly <paramref name="people"/>.</summary>
    /// <param name="people">Each token this resolver admits, and the principal it resolves to.</param>
    internal TestPersonResolver(params (string Token, AlvoPrincipal Principal)[] people) =>
        _people = people.ToDictionary(person => person.Token, person => person.Principal, StringComparer.Ordinal);

    /// <summary>Every call, in order: the token as presented and the tenant the request asked for.</summary>
    internal ConcurrentQueue<(string Presented, string? Tenant)> Calls { get; } = new();

    /// <summary>Registers this instance as the person resolver.</summary>
    /// <param name="services">The host's services.</param>
    internal void Register(IServiceCollection services) =>
        services.AddKeyedSingleton<IAlvoContextResolver>(Key, this);

    /// <inheritdoc/>
    public ValueTask<AlvoPrincipal?> ResolveAsync(
        string? presentedKey, string? requestedTenant, CancellationToken cancellationToken)
    {
        Calls.Enqueue((presentedKey ?? string.Empty, requestedTenant));
        return ValueTask.FromResult(
            presentedKey is not null && _people.TryGetValue(presentedKey, out var person) ? person : null);
    }

    /// <summary>A signed-in person: a fresh user, <c>authenticated</c>, and the given tenant roles in the given tenant.</summary>
    /// <remarks>
    /// The scopes are <c>*:read</c> and <c>*:write</c> because the scope gate denies an empty scope set and the
    /// person principal's own scopes are a later slice's call (the plan's D8).
    /// </remarks>
    /// <param name="keyId">The principal's key id, as an audit row would carry it.</param>
    /// <param name="tenant">The tenant the person acts in, or <see langword="null"/> for none.</param>
    /// <param name="tenantRoles">The person's tenant roles there.</param>
    internal static AlvoPrincipal Person(string keyId, Guid? tenant, params string[] tenantRoles) => new()
    {
        KeyId = keyId,
        Scopes = new HashSet<ApiKeyScope> { Scope("*:read"), Scope("*:write") },
        Context = new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated },
            TenantRoles = RoleCatalog.Create(tenantRoles).Resolve(tenantRoles),
            Tenant = tenant is { } id ? new TenantId(id) : null,
            ActorKind = AlvoActorKind.User,
        },
    };

    private static ApiKeyScope Scope(string text) =>
        ApiKeyScope.TryParse(text, out var scope)
            ? scope
            : throw new ArgumentException($"'{text}' is not a scope.", nameof(text));
}
