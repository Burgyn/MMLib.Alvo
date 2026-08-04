using MMLib.Alvo.Migrations;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The code-first apply, as the one public operation a host performs on a built container.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator itself (<c>SchemaMigrationRunner</c>) is deliberately <see langword="internal"/>: it
/// takes six collaborators, and publishing it would freeze that constructor as a contract. What a host
/// genuinely needs is one verb — <em>bring the configured descriptor up</em> — so that is what is public.
/// </para>
/// <para>
/// <b>No host has to call it, and the ordering obligation it used to carry is gone.</b> Alvo's own boot
/// service loads, applies and primes before the server binds, and <c>MapAlvoDataApi</c> reads its
/// entity-name literals when the endpoint table first materialises — so mapping before anything is applied is
/// now the ordinary shape. What remains for this verb is the callers that <em>are</em> an explicit apply: the
/// CLI and the Management API.
/// </para>
/// <para>
/// <b>A refusal is a return value, not an exception.</b> A plan that is destructive while
/// <c>AllowDestructive</c> is <see langword="false"/> comes back with <c>Applied == false</c> and no throw,
/// because a caller that asked for a dry run wants to read the plan rather than catch it. A caller that
/// wants a running backend wants the opposite and must say so: call
/// <see cref="MigrationResult.EnsureApplied"/> on what this returns. Discarding the result is how a caller
/// ends up believing a refused descriptor was applied — <c>Applied == false</c> leaves the policy catalog
/// unprimed, and an unprimed catalog denies every operation (fail-closed).
/// </para>
/// <para>
/// A new verb in <c>docs/architecture/extensibility.md</c>'s taxonomy: <c>Apply{Thing}</c> is a runtime
/// operation on a built provider, not a registration, so none of <c>Use</c>/<c>Add</c>/<c>Enable</c>/<c>From</c>
/// fits it. It takes <see cref="IServiceProvider"/> rather than <c>IHost</c> so a plain console host, a
/// scope and a <c>WebApplication</c> all reach it through the same member.
/// </para>
/// </remarks>
public static class AlvoDescriptorApplyExtensions
{
    /// <summary>Applies the configured project descriptor, creating or migrating the schema it declares.</summary>
    /// <param name="services">A built service provider Alvo was registered in.</param>
    /// <param name="options">How to apply — destructive changes, dry run, audit provenance. Defaults to <see cref="MigrationOptions"/>'s own defaults.</param>
    /// <param name="ct">Cancels the apply.</param>
    /// <returns>
    /// What was planned and whether it was applied. Call <see cref="MigrationResult.EnsureApplied"/> on it
    /// unless the caller is a dry run: a refused destructive plan returns rather than throws.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Alvo is not registered in <paramref name="services"/>.</exception>
    public static Task<MigrationResult> ApplyAlvoDescriptorAsync(
        this IServiceProvider services,
        MigrationOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var runner = services.GetService<SchemaMigrationRunner>()
            ?? throw new InvalidOperationException(NotRegistered);

        return runner.RunAsync(options ?? new MigrationOptions(), ct);
    }

    /// <summary>
    /// Why this message is crafted rather than left to <c>GetRequiredService</c>: the default names
    /// <c>SchemaMigrationRunner</c>, which is <see langword="internal"/>. A host author reading it is told to
    /// register a type they cannot reference, which is the opposite of the structured-error-with-a-fix rule
    /// (§0 principle 4) and of the fail-fast wording <c>UseSqlite</c> already sets a precedent for.
    /// </summary>
    /// <remarks>
    /// Unreachable from inside this repository — every in-repo caller registers Alvo first — so the fact that
    /// pins it constructs a provider deliberately without <c>AddAlvo</c>. That is the only way this message is
    /// reachable at all, and an unpinned message is one an edit can silently make useless.
    /// </remarks>
    private const string NotRegistered =
        "Alvo is not registered in this service provider, so there is no descriptor to apply. Call " +
        "services.AddAlvo(...) — with a database provider and a descriptor source — before building the " +
        "provider you pass here.";
}
