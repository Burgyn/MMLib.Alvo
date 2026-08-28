using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// Turns a descriptor's <c>field.rollup</c> into the applied schema's <see cref="RollupSchema"/>, and refuses
/// at <b>apply</b> every rollup whose relationship cannot be resolved — the enforcement half of the
/// computed/rollup/hook ladder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enforced here rather than documented, because every refusal below is otherwise a silently wrong stored
/// number.</b> A rollup nobody can resolve does not fail loudly at write time: it either aggregates over the
/// wrong foreign key, or over no rows at all, and the parent's column then holds a number that looks like data.
/// That is the exact outcome <c>UnhonouredFeatures</c> refused the whole feature for before #21, so shipping
/// the feature without these checks would have traded a loud refusal for the quiet failure it was protecting
/// against.
/// </para>
/// <para>
/// <b>It is a type with the whole descriptor in hand, not a method on the field.</b> Resolving <c>via</c>
/// needs the <em>child</em> entity's fields, which the per-entity mapping pass cannot see; and the resolution
/// has to be the same walk as the check, or the mapper would refuse an ambiguity the write path then resolves
/// its own way. <see cref="Resolve"/> therefore returns the resolved key rather than merely validating one.
/// </para>
/// </remarks>
/// <param name="descriptor">The descriptor being applied.</param>
internal sealed class RollupResolver(AlvoDescriptor descriptor)
{
    private readonly IReadOnlyDictionary<string, EntityDescriptor> _entities =
        descriptor.Entities ?? new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal);

    /// <summary>Whether the project turns row-level tenancy on, which is what an entity's tenancy defaults from.</summary>
    private readonly bool _tenancyEnabled = descriptor.Tenancy?.Enabled == true;

    /// <summary>
    /// The applied-schema rollup for one field, or <see langword="null"/> when the field declares none.
    /// </summary>
    /// <param name="parent">The entity the rollup field belongs to.</param>
    /// <param name="declaring">
    /// The parent's own descriptor — needed for its tenancy, which cannot be looked up by name without
    /// assuming this pass and the descriptor's entity dictionary agree about the key.
    /// </param>
    /// <param name="fieldName">The rollup field's name.</param>
    /// <param name="field">The field's descriptor.</param>
    /// <exception cref="InvalidDataException">The declaration cannot be honoured as written.</exception>
    internal RollupSchema? Resolve(
        string parent, EntityDescriptor declaring, string fieldName, FieldDescriptor field)
    {
        EnsureNotAlsoComputed(parent, fieldName, field);

        if (field.Rollup is not { } rollup)
        {
            return null;
        }

        var child = ChildEntity(parent, fieldName, rollup);
        EnsureChildIsPhysical(parent, fieldName, rollup, child);
        EnsureNoFilter(parent, fieldName, rollup);
        EnsureTenancyDoesNotCross(parent, declaring, fieldName, rollup, child);
        EnsureAggregatedFieldIsResolvable(parent, fieldName, rollup, child);

        return new RollupSchema
        {
            From = rollup.From,
            Op = MapOperation(rollup.Op),
            Field = rollup.Op == RollupOp.Count ? null : rollup.Field,
            Via = ResolveVia(parent, fieldName, rollup, child),
        };
    }

    /// <summary>
    /// A field may be <c>computed</c> or a <c>rollup</c>, never both. The two disagree about <em>who owns the
    /// value</em>: a generated column is maintained by the engine, which refuses every write to it, while a
    /// rollup is maintained by this framework, which writes it. Declaring both makes one of them a lie about a
    /// stored number, and it is unknowable from the descriptor which one an author meant.
    /// </summary>
    /// <remarks>
    /// Checked before <c>rollup</c> is read at all, so the message names the collision rather than whatever the
    /// rollup resolution happened to fail on next.
    /// </remarks>
    private static void EnsureNotAlsoComputed(string parent, string fieldName, FieldDescriptor field)
    {
        if (field.Computed is not null && field.Rollup is not null)
        {
            throw new InvalidDataException(
                $"Field '{parent}.{fieldName}' declares both 'computed' and 'rollup'. A computed field is a "
                + "stored generated column the database maintains and refuses every write to; a rollup is "
                + "maintained by Alvo inside the child write's transaction. Only one of them can own the "
                + "value, so the other would be a declaration about a number nothing honours. Keep 'computed' "
                + "for arithmetic over this row, or 'rollup' for an aggregate over related records — and to "
                + "combine them, put the rollup on its own field and reference it from a second, computed "
                + "one, which is what the schema's own 'gross_total = net_total + vat_total' example does.");
        }
    }

    /// <summary>
    /// A <c>rollup.where</c> is refused rather than ignored, because ignoring it aggregates <b>every</b> child
    /// record instead of the declared subset — a stored number that looks like data and is not, which is the
    /// same failure mode the whole feature was refused for.
    /// </summary>
    /// <remarks>
    /// Refused here rather than tabulated in <see cref="UnhonouredFeatures"/> for the reason
    /// <see cref="UnhonouredSlot"/> exists: the raw-JSON validator pass cannot ask "is this a rollup whose
    /// filter I would drop" without walking the rollup object it does not parse, while this pass already holds
    /// the parsed declaration and its exact location. The <em>wording</em> still lives in one place.
    /// </remarks>
    private static void EnsureNoFilter(string parent, string fieldName, Rollup rollup)
    {
        if (rollup.Where is not null)
        {
            throw new InvalidDataException(
                $"Field '{parent}.{fieldName}' declares a 'rollup.where' filter. {UnhonouredFeatures.RollupWhere.Consequence} "
                + UnhonouredFeatures.RollupWhere.Fix);
        }
    }

    /// <summary>
    /// A rollup whose parent and child <b>disagree about tenancy</b> is refused, because there is no tenant
    /// the aggregate could be narrowed to that is honest for both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a cross-tenant refusal, not a tidiness rule, and both crossing shapes are reachable from a
    /// descriptor the JSON Schema accepts.</b> A <c>scoped</c> parent with a <c>global</c> child aggregates one
    /// shared child set into every tenant's parent row — every tenant's number is computed from rows no tenant
    /// owns. A <c>global</c> parent with a <c>scoped</c> child is worse and is the reason this is refused rather
    /// than documented: every tenant's children aggregate into <em>one</em> globally readable row, so a
    /// <c>count</c> discloses how many rows other tenants hold and a <c>sum</c> discloses their values. That is
    /// a cross-tenant read oracle of the same class as the unique-index one (#137), and it contradicts the
    /// premise that Alvo's app-side rules are as safe as native row-level security.
    /// </para>
    /// <para>
    /// <b>Refused here rather than repaired below.</b> The write path narrows both statements of the recompute
    /// by <c>tenant_id</c> when the pair is scoped (see <c>RollupRecompute</c>), which is what keeps a scoped
    /// rollup inside one tenant — but that predicate only exists when <em>both</em> sides carry the column.
    /// Inventing a value for the side that does not have one would be inventing an answer to "whose rows is
    /// this number about", and the descriptor is the only place that question can be answered.
    /// </para>
    /// <para>
    /// The comparison is "is this side scoped", not equality of the resolved mode: a project that leaves
    /// tenancy off resolves an entity's tenancy to <see langword="null"/>, which carries no <c>tenant_id</c>
    /// and is therefore the same thing as <c>global</c> for every question here. Refusing
    /// <see langword="null"/>-versus-<c>global</c> would reject a legal descriptor over a distinction with no
    /// physical consequence.
    /// </para>
    /// </remarks>
    private void EnsureTenancyDoesNotCross(
        string parent, EntityDescriptor declaring, string fieldName, Rollup rollup, EntityDescriptor child)
    {
        var parentTenancy = Tenancy(declaring);
        var childTenancy = Tenancy(child);

        if (IsScoped(parentTenancy) == IsScoped(childTenancy))
        {
            return;
        }

        throw new InvalidDataException(
            $"Field '{parent}.{fieldName}' rolls up '{rollup.From}', but the two entities disagree about "
            + $"tenancy: '{parent}' is {Describe(parentTenancy)} and '{rollup.From}' is "
            + $"{Describe(childTenancy)}. A rollup aggregates the child rows of one tenant into that same "
            + "tenant's parent row, and only a pair that agrees can be narrowed by 'tenant_id' — a scoped "
            + "child aggregated into a global parent would put every tenant's rows into one globally readable "
            + "number, which discloses their row count and their values, and a global child aggregated into a "
            + "scoped parent would compute every tenant's number from rows no tenant owns. Give both entities "
            + $"the same tenancy: make '{rollup.From}' {Describe(parentTenancy)}, or '{parent}' "
            + $"{Describe(childTenancy)}.");
    }

    /// <summary>One entity's resolved tenancy, from the mapper's own defaulting rule.</summary>
    private TenancyMode? Tenancy(EntityDescriptor entity) =>
        DescriptorToSchemaMapper.ResolveTenancy(entity.Tenancy, _tenancyEnabled);

    /// <summary>Whether an entity carries a <c>tenant_id</c> at all — the only property the refusal is about.</summary>
    private static bool IsScoped(TenancyMode? tenancy) => tenancy == TenancyMode.Scoped;

    /// <summary>One tenancy mode, as the refusal names it.</summary>
    private static string Describe(TenancyMode? tenancy) =>
        IsScoped(tenancy) ? "scoped" : "global";

    /// <summary>
    /// The child entity the rollup aggregates over, refused when the descriptor declares no such entity.
    /// </summary>
    /// <remarks>
    /// A missing entity is refused with the same fail-fast-at-apply discipline as a <c>ref</c> to one, and for
    /// a sharper reason: an unresolvable <c>from</c> leaves the parent's column with no maintainer at all, and
    /// nothing at write time would ever look for one.
    /// </remarks>
    private EntityDescriptor ChildEntity(string parent, string fieldName, Rollup rollup) =>
        _entities.TryGetValue(rollup.From, out var child)
            ? child
            : throw new InvalidDataException(
                $"Field '{parent}.{fieldName}' rolls up from '{rollup.From}', which this descriptor does not "
                + "declare as an entity. Declared entities: "
                + $"{string.Join(", ", _entities.Keys.Order(StringComparer.Ordinal))}.");

    /// <summary>
    /// A rollup aggregates a child the applied schema contains, so a <c>storage: "dynamic"</c> child is
    /// refused rather than resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DescriptorToSchemaMapper.Map</c> keeps only physical entities, while this resolver reads the
    /// <b>whole</b> descriptor — it has to, because resolving <c>via</c> needs the child's fields, which
    /// the per-entity pass cannot see. A dynamic child therefore resolves cleanly and then never reaches
    /// the model, leaving the parent's column with no entity writer to maintain it: the same
    /// stored-number-nothing-maintains outcome an unresolvable <c>from</c> is refused for, arrived at by a
    /// different route.
    /// </para>
    /// <para>
    /// <b>The condition is <see cref="DescriptorToSchemaMapper.IsPhysical"/> itself, called and not
    /// restated, and that is deliberate.</b> The reason this pair is refused is not that the child says
    /// <c>dynamic</c> — it is that the child does not reach the applied schema, and <c>Map</c>'s filter is
    /// what decides that. Keyed on the reason, the refusal lifts itself the day F7's dynamic driver can
    /// drive a recompute and the filter admits such a child; keyed on the declaration it would have had to
    /// be remembered and deleted by hand. Recorded as Dev-15 in the design.
    /// </para>
    /// </remarks>
    private static void EnsureChildIsPhysical(string parent, string fieldName, Rollup rollup, EntityDescriptor child)
    {
        if (DescriptorToSchemaMapper.IsPhysical(child))
        {
            return;
        }

        throw new InvalidDataException(
            $"Field '{parent}.{fieldName}' rolls up from '{rollup.From}', which declares "
            + "'storage': 'dynamic'. A dynamic entity is not part of the applied schema, so nothing would "
            + $"ever maintain '{parent}.{fieldName}' and it would read as a number while being none. Roll "
            + $"up from a physical entity, or drop the rollup until '{rollup.From}' has one.");
    }

    /// <summary>
    /// The child's foreign-key field pointing back to <paramref name="parent"/> — the descriptor's own
    /// <c>via</c> when it names one, and otherwise the child's single <c>ref</c> to this parent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three refusals, and the ambiguous one is the interesting case.</b> A child with two references to the
    /// same parent — the frozen schema's own <c>follows.follower</c> / <c>follows.followee</c> example — has no
    /// defensible default: picking either produces a plausible number over the wrong relationship, and
    /// declaration order is not a decision an author made. A child with <em>no</em> reference is the design's
    /// stated ladder rule ("a rollup whose <c>from</c> entity does not reference this one is refused"), and a
    /// <c>via</c> that is not such a reference is the typo version of the same thing.
    /// </para>
    /// <para>
    /// This returns the key rather than validating it, so the apply-time check and the value every layer below
    /// reads are the same walk. Two walks is how the mapper comes to accept a rollup the write path then
    /// aggregates over a different column.
    /// </para>
    /// </remarks>
    private static string ResolveVia(string parent, string fieldName, Rollup rollup, EntityDescriptor child)
    {
        var candidates = ReferencesTo(parent, child);

        if (rollup.Via is { } via)
        {
            return candidates.Contains(via, StringComparer.Ordinal)
                ? via
                : throw new InvalidDataException(
                    $"Field '{parent}.{fieldName}' rolls up '{rollup.From}' via '{via}', which is not a "
                    + $"reference from '{rollup.From}' to '{parent}'. "
                    + Alternatives(rollup.From, parent, candidates));
        }

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidDataException(
                $"Field '{parent}.{fieldName}' rolls up from '{rollup.From}', but '{rollup.From}' does not "
                + $"reference '{parent}': a rollup aggregates the records of a child that points back at this "
                + "entity, and there is no foreign key to follow. Add a 'ref' field on "
                + $"'{rollup.From}' with \"entity\": \"{parent}\", or roll up from the entity that does."),
            _ => throw new InvalidDataException(
                $"Field '{parent}.{fieldName}' rolls up from '{rollup.From}', which references '{parent}' more "
                + "than once, so which relationship to aggregate over is ambiguous. Name it with "
                + $"'rollup.via'. {Alternatives(rollup.From, parent, candidates)}"),
        };
    }

    /// <summary>The child's <c>ref</c> fields that target <paramref name="parent"/>, in declaration order.</summary>
    private static IReadOnlyList<string> ReferencesTo(string parent, EntityDescriptor child) =>
        [.. (child.Fields ?? new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal))
            .Where(candidate => string.Equals(candidate.Value.Entity, parent, StringComparison.Ordinal))
            .Select(candidate => candidate.Key)];

    /// <summary>The references an author could have meant, for a refusal that is actionable rather than correct.</summary>
    private static string Alternatives(string child, string parent, IReadOnlyList<string> candidates) =>
        candidates.Count == 0
            ? $"'{child}' declares no reference to '{parent}' at all."
            : $"References from '{child}' to '{parent}': "
                + $"{string.Join(", ", candidates.Order(StringComparer.Ordinal))}.";

    /// <summary>
    /// The aggregated child field: required for every operation but <c>count</c>, and required to <em>exist</em>
    /// on the child.
    /// </summary>
    /// <remarks>
    /// The frozen schema already makes <c>field</c> conditionally required, so the first half is the guard an
    /// embedded host that never runs the JSON Schema still passes through. The second half the schema cannot
    /// express at all: a typo'd child field name would render an aggregate over a column that does not exist,
    /// and the first write to a child would fail with the engine naming a column the author did not write.
    /// </remarks>
    private static void EnsureAggregatedFieldIsResolvable(
        string parent, string fieldName, Rollup rollup, EntityDescriptor child)
    {
        if (rollup.Op == RollupOp.Count)
        {
            return;
        }

        if (rollup.Field is not { } aggregated)
        {
            throw new InvalidDataException(
                $"Field '{parent}.{fieldName}' declares a '{rollup.Op.ToString().ToLowerInvariant()}' rollup "
                + "with no 'field'. Only 'count' aggregates records rather than values; name the child field "
                + "to aggregate, or use \"op\": \"count\".");
        }

        if (child.Fields?.ContainsKey(aggregated) != true)
        {
            throw new InvalidDataException(
                $"Field '{parent}.{fieldName}' aggregates '{rollup.From}.{aggregated}', which '{rollup.From}' "
                + $"does not declare. Declared fields on '{rollup.From}': "
                + $"{string.Join(", ", (child.Fields?.Keys ?? []).Order(StringComparer.Ordinal))}.");
        }
    }

    /// <summary>
    /// The applied schema's operation for the descriptor's. Exhaustive by construction: an unmapped member
    /// throws rather than defaulting to <see cref="RollupOperation.Sum"/>, because a defaulted aggregate is a
    /// wrong number nothing reports.
    /// </summary>
    private static RollupOperation MapOperation(RollupOp op) => op switch
    {
        RollupOp.Sum => RollupOperation.Sum,
        RollupOp.Count => RollupOperation.Count,
        RollupOp.Avg => RollupOperation.Avg,
        RollupOp.Min => RollupOperation.Min,
        RollupOp.Max => RollupOperation.Max,
        _ => throw new ArgumentOutOfRangeException(
            nameof(op), op, "Unmapped rollup operation; map it to a RollupOperation here."),
    };
}
