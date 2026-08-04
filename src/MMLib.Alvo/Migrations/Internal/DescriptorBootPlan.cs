using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations.Internal;

/// <summary>
/// Everything a boot decides from the descriptor alone: the loaded JSON, the descriptor it parses to,
/// the schema it maps to, and the compiled policy for it.
/// </summary>
/// <param name="Descriptor">The parsed descriptor.</param>
/// <param name="Desired">The schema <paramref name="Descriptor"/> maps to — what the database should hold.</param>
/// <param name="DescriptorJson">The descriptor exactly as it was loaded, for the applied snapshot to record.</param>
/// <param name="Catalog">
/// The compiled policy catalog. Built here, before any caller touches a database, so an uncompilable rule
/// set rejects the boot rather than being discovered after the schema is already durable.
/// </param>
internal sealed record BootPlan(
    AlvoDescriptor Descriptor, SchemaModel Desired, string DescriptorJson, PolicyCatalog Catalog);

/// <summary>
/// Stage 0 of the boot sequence: load the descriptor, validate it, map it, compile its policy, and run
/// the checks that decide whether the mapped schema can be served at all — <b>with no database
/// access</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The absence of a database is the contract, not an implementation detail.</b> Loading, validating,
/// mapping and compiling are pure CPU plus one descriptor read; they are what routes and authorization
/// depend on, and they are safe on every replica simultaneously. Only the stages after this one diff
/// against the applied snapshot or run DDL. Separating them is what lets a host ask to <em>serve</em> an
/// already-migrated database without also asking to migrate it — see the design's stage table
/// (<c>docs/superpowers/specs/2026-08-02-startup-lifecycle-and-config-dx-design.md</c>). So this type
/// takes no migrator, no store and no introspector, and a fact that gives it none is the proof.
/// </para>
/// <para>
/// <b>The reserved-name and format checks live here rather than at route mapping.</b> They used to run
/// inside <c>MapAlvoDataApi</c>, which is a start-time failure only for as long as mapping is eager.
/// Once routes materialise lazily, from the first request that builds the matcher, a check left there
/// would be demoted from "the host refuses to start" to "the first request 500s" — silently, with a
/// green suite. Running them at stage 0 is what keeps the refusal a start-time one. The same calls
/// remain in <c>AlvoEndpointDataSource</c>, where they now run on first enumeration, as the belt for a
/// schema that reaches routing from somewhere other than this descriptor (a host's own
/// <c>ISchemaRegistry</c>, F7's dynamic entities) — a different input from
/// <see cref="BootPlan.Desired"/> and therefore not the same check.
/// </para>
/// </remarks>
internal sealed class DescriptorBootPlan
{
    /// <summary>
    /// The refusal a host that attached a driver and forgot the descriptor source reads.
    /// </summary>
    /// <remarks>
    /// <b>A sentence naming the call, not a dependency-injection failure naming this class.</b> Taking the
    /// source as a required constructor dependency turned "you forgot <c>FromDescriptor</c>" into
    /// <c>Unable to resolve service for type 'IDescriptorSource' while attempting to activate
    /// 'DescriptorBootPlan'</c> — two <see langword="internal"/> types a host author cannot see, and no call
    /// they can make. It is <em>optional</em> rather than validated at registration because
    /// <c>AddAlvo</c> plus a driver, with the schema applied by the caller, is a supported composition: the
    /// descriptor becomes required exactly here, when something asks for a boot plan.
    /// </remarks>
    internal const string NoDescriptorSourceFix =
        "Call FromDescriptor(\"project.alvo.json\") inside AddAlvo(...), or register an IDescriptorSource of "
        + "your own.";

    internal const string NoDescriptorSourceMessage =
        "Alvo cannot start: no project descriptor source is configured. " + NoDescriptorSourceFix;

    private readonly IDescriptorSource? _source;
    private readonly IDescriptorValidator _validator;
    private readonly ICelCompiler _compiler;
    private readonly ILogger<DescriptorBootPlan> _logger;

    /// <summary>Initializes a new instance of the <see cref="DescriptorBootPlan"/> class.</summary>
    /// <param name="source">
    /// Where the descriptor is read from, or <see langword="null"/> for a composition that attached none —
    /// refused by name on the first <see cref="LoadAsync"/> rather than at activation.
    /// </param>
    /// <param name="validator">The JSON-Schema and semantic validation the descriptor must pass.</param>
    /// <param name="compiler">The CEL compiler every rule and field flag is compiled through.</param>
    /// <param name="logger">Writes the declared-but-unhonoured warning.</param>
    public DescriptorBootPlan(
        IDescriptorSource? source,
        IDescriptorValidator validator,
        ICelCompiler compiler,
        ILogger<DescriptorBootPlan> logger)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _validator = validator;
        _compiler = compiler;
        _logger = logger;
    }

    /// <summary>Runs stage 0 and returns what every later stage reads.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The descriptor, the schema it wants, and its compiled policy.</returns>
    /// <exception cref="DescriptorValidationException">
    /// The descriptor failed validation, or one of its rules, tenant scopes or field flags failed to compile.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The mapped schema cannot be served: a field shadows a reserved query-string key, or a declared format
    /// is not a regular expression.
    /// </exception>
    /// <exception cref="AlvoStartupRefusedException">
    /// No descriptor source was attached — see <see cref="NoDescriptorSourceMessage"/>.
    /// </exception>
    internal async Task<BootPlan> LoadAsync(CancellationToken ct)
    {
        var source = _source
            ?? throw new AlvoStartupRefusedException(NoDescriptorSourceMessage, NoDescriptorSourceFix);

        var descriptorJson = await source.LoadAsync(ct).ConfigureAwait(false);
        EnsureValid(descriptorJson);

        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        var desired = DescriptorToSchemaMapper.Map(descriptor);

        WarnAboutUnhonouredBlocks(descriptor);
        EnsureServable(desired);

        return new BootPlan(descriptor, desired, descriptorJson, PolicyCatalog.Build(descriptor, desired, _compiler));
    }

    private void EnsureValid(string descriptorJson)
    {
        var validation = _validator.Validate(descriptorJson);
        if (!validation.IsValid)
        {
            throw new DescriptorValidationException(validation);
        }
    }

    /// <summary>
    /// Warns about every block this build honours nowhere, on <b>every</b> boot — the unchanged restart
    /// included.
    /// </summary>
    /// <remarks>
    /// Emitted here rather than on the branches that write something: this is the last point at which the
    /// descriptor is known to be appliable (the mapper refuses every unhonoured <em>feature</em> above), and
    /// every later stage runs through it — including the empty-plan no-op, which is the ordinary case on a
    /// restart. Warning only on a genuine apply would tell an author about their unhonoured blocks exactly
    /// once, on the deploy where they are least surprised by them, and never again.
    /// </remarks>
    private void WarnAboutUnhonouredBlocks(AlvoDescriptor descriptor)
        => UnhonouredSubsystems.Warn(_logger, descriptor);

    /// <summary>
    /// Refuses a mapped schema the Data API could not serve: a field whose name a query-string key reserves,
    /// or a declared format whose pattern is not a regular expression.
    /// </summary>
    /// <remarks>
    /// The compiled <see cref="FormatCatalog"/> is discarded, because the refusal is what is wanted here and
    /// the catalogue the endpoints match against is built from the applied schema at the moment the route
    /// literals are fixed. Both checks are reachable only for a descriptor that skipped
    /// <c>DescriptorValidator</c> (a replaceable port) or <c>DescriptorToSchemaMapper</c>'s own pattern
    /// check — which is precisely the case a belt exists for, and the case a lazily mapped route table would
    /// otherwise turn into a request-time 500.
    /// </remarks>
    private static void EnsureServable(SchemaModel desired)
    {
        ReservedQueryKeys.EnsureNoneIsShadowed(desired.Entities);
        _ = FormatCatalog.Build(desired.Entities);
    }
}
