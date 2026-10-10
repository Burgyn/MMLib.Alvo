using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Collections.Frozen;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The applied schema as a request sees it: the entity it addresses and the compiled formats its fields name,
/// read <b>per request</b> from the same registry the policy catalogue is published through — so a descriptor
/// applied at runtime governs the very next request's fields exactly as it already governed its rules (#353).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every generated endpoint used to close over the <see cref="EntitySchema"/> and the
/// <see cref="FormatCatalog"/> its endpoint table was materialised with. The route literal is rightly frozen
/// (a new entity needing a new route is #103's fork), but the <em>field set</em> is not a routing fact, and
/// freezing it split one applied revision in two: the policy catalogue and the data port followed a runtime
/// apply while the body reader, the record validator and the query parser went on judging requests against the
/// schema the process started with. A field added at runtime was refused as <c>unknown-field</c> until a
/// restart; a field removed at runtime was admitted here and refused by the port as a <c>403</c>; a narrowed
/// <c>maxLength</c> or a newly <c>required</c> field was not enforced by anything.
/// </para>
/// <para>
/// <b>The decision and the fields come from one applied revision</b> — see <see cref="Decide{TDecision}"/>. A
/// request whose policy decision was taken against revision N and whose fields were read from revision N+1 is
/// judged by a mask that never saw the field set it is applied to: a field <em>added</em> as <c>readOnly</c>
/// or <c>hidden</c> is absent from N's masks and present in N+1's fields, so the mixed pair would treat it as
/// writable and visible, which neither revision says.
/// </para>
/// <para>
/// <b>Built once per applied revision, not per request.</b> The <see cref="Snapshot"/> is cached against the
/// <see cref="SchemaModel"/> instance it was built from — reference identity, the same signal the EF port's
/// <c>AlvoDataContextFactory</c> keys its model on, because an apply replaces the model wholesale — so a
/// format's <see cref="System.Text.RegularExpressions.Regex"/> is compiled once per revision and a request
/// costs two volatile reads and a dictionary lookup.
/// </para>
/// <para>
/// <b>The two schema guards run once per revision, here.</b> <see cref="ReservedQueryKeys"/> and
/// <see cref="FormatCatalog.Build"/> refuse a schema this API cannot serve. For the revision the endpoint table
/// materialises with, the refusal is <see cref="AlvoEndpointDataSource"/>'s, exactly as before. A revision
/// applied <em>later</em> reached the registry through <c>RuntimeSchemaService</c>, whose validator and mapper
/// refuse both shapes before anything is applied, so a refusal here is reachable only for a schema that bypassed
/// them (a substituted <c>ISchemaRegistry</c>, F7's dynamic registry): the request fails with the guard's
/// <see cref="InvalidOperationException"/> — a 500, fail-closed — rather than being served against fields the
/// guard refused.
/// </para>
/// </remarks>
/// <param name="catalog">The one authority on the applied schema, read live.</param>
internal sealed class AppliedSchemaView(EntityRouteCatalog catalog)
{
    /// <summary>
    /// How many times a decision is retaken because an apply landed while it was being taken, before the request
    /// is failed rather than served against a pair nobody can vouch for.
    /// </summary>
    /// <remarks>
    /// An apply is a database transaction and a decision is microseconds of in-memory work, so a second attempt
    /// virtually always succeeds; the bound exists so a pathological registry cannot spin a request forever.
    /// </remarks>
    private const int MaxAttempts = 8;

    private Snapshot? _last;

    /// <summary>The applied revision as it stands now, with its guards already run.</summary>
    /// <exception cref="InvalidOperationException">The applied schema cannot be served (see the type's remarks).</exception>
    internal Snapshot Current => SnapshotOf(catalog.Schema);

    /// <summary>
    /// Takes <paramref name="decide"/> and reads <paramref name="entity"/> against <b>one</b> applied revision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seqlock over the registry's single published reference: the schema is read before and after the
    /// decision, and the decision is retaken if an apply landed in between. <c>PolicyCatalogProvider</c> publishes
    /// the rules and the schema as one <see cref="Rules.PolicyCatalog"/> reference and serves
    /// <see cref="ISchemaRegistry.GetSchema"/> off that same reference, so an unchanged schema instance on both
    /// sides means the catalogue the engine read in between carried it too (each apply maps a fresh
    /// <see cref="SchemaModel"/>). With a host-substituted registry the guarantee narrows to "the schema did not
    /// change while the decision was taken", which is all a registry that is not the policy catalogue can offer.
    /// </para>
    /// <para>
    /// <b>A denial is not retried.</b> <paramref name="decide"/> throws <see cref="AlvoAuthorizationException"/>
    /// for one, and it propagates on the first attempt: refusing is correct under either revision.
    /// </para>
    /// </remarks>
    /// <typeparam name="TDecision">One decision, or a tuple of them for a route gated by two operations.</typeparam>
    /// <param name="entity">The entity the route serves.</param>
    /// <param name="decide">Resolves the route's decision(s), throwing on a denial.</param>
    /// <returns>The decision and the entity as the same revision declares it.</returns>
    /// <exception cref="AlvoAuthorizationException">
    /// <paramref name="decide"/> denied, or the revision the decision was taken against no longer declares the
    /// entity — the port's own unmapped-entity answer, so an entity dropped at runtime reads like one never
    /// declared.
    /// </exception>
    internal (TDecision Decision, AppliedEntity Entity) Decide<TDecision>(string entity, Func<TDecision> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var before = catalog.Schema;
            var decision = decide();
            if (ReferenceEquals(before, catalog.Schema))
            {
                return (decision, SnapshotOf(before).Entity(entity));
            }
        }

        throw new InvalidOperationException(
            $"The applied schema changed during each of {MaxAttempts} attempts to resolve one request's policy "
            + "decision, so no single revision can be named for it. The request was refused rather than judged "
            + "by a decision and a field set from two different revisions.");
    }

    /// <summary>The snapshot for <paramref name="schema"/>, built on first sight of that instance.</summary>
    /// <remarks>
    /// Two requests seeing a new revision at once may both build it; both results are equal and either may win
    /// the publication, so no lock is taken. The reference is published with <c>Volatile.Write</c> only after it
    /// is fully constructed.
    /// </remarks>
    private Snapshot SnapshotOf(SchemaModel schema)
    {
        if (Volatile.Read(ref _last) is { } last && ReferenceEquals(last.Schema, schema))
        {
            return last;
        }

        var built = Snapshot.Of(schema);
        Volatile.Write(ref _last, built);
        return built;
    }

    /// <summary>One applied revision, with everything a request derives from it computed once.</summary>
    /// <param name="Schema">The applied model this snapshot was built from — the cache key, by reference.</param>
    /// <param name="Entities">The entities the revision declares, by name.</param>
    /// <param name="Formats">Every format the revision's fields name, compiled.</param>
    internal sealed record Snapshot(
        SchemaModel Schema, FrozenDictionary<string, EntitySchema> Entities, FormatCatalog Formats)
    {
        /// <summary>Runs the schema guards and compiles the formats of one revision.</summary>
        /// <param name="schema">The applied model.</param>
        /// <exception cref="InvalidOperationException">A guard refused the schema.</exception>
        internal static Snapshot Of(SchemaModel schema)
        {
            ReservedQueryKeys.EnsureNoneIsShadowed(schema.Entities);

            return new Snapshot(
                schema,
                schema.Entities.ToFrozenDictionary(entity => entity.Name, StringComparer.Ordinal),
                FormatCatalog.Build(schema.Entities));
        }

        /// <summary>The entity as this revision declares it.</summary>
        /// <param name="name">The entity's name.</param>
        /// <exception cref="AlvoAuthorizationException">This revision does not declare it.</exception>
        internal AppliedEntity Entity(string name) =>
            Entities.TryGetValue(name, out var entity)
                ? new AppliedEntity(entity, Formats)
                : throw new AlvoAuthorizationException(UnmappedEntity);
    }

    /// <summary>
    /// The refusal for an entity the decision's revision does not declare — word for word the port's
    /// (<c>AlvoDataContext.UnmappedEntityMessage</c>), so where the refusal was raised is not observable.
    /// </summary>
    private const string UnmappedEntity = "The operation was not authorized.";
}

/// <summary>One entity as one applied revision declares it, with that revision's compiled formats.</summary>
/// <param name="Schema">The entity's declared shape.</param>
/// <param name="Formats">The revision's compiled field formats.</param>
internal sealed record AppliedEntity(EntitySchema Schema, FormatCatalog Formats);
