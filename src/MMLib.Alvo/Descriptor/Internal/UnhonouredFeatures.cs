namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// <b>The one authority on what the frozen descriptor schema declares and this build does not honour.</b>
/// Every refusal of an unimplemented feature — the mapper's exception, the validator's structured error, and
/// the facts that hold both — reads this table and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a table and not a list of <c>if</c> blocks.</b> The features were four hand-written copies —
/// <c>DescriptorToSchemaMapper</c>, <c>DescriptorValidator</c>, and a theory list in each of two test
/// files — with nothing tying them, so adding a fifth feature to one side left the others silently
/// accepting it. That is the identical defect the built-in formats had one commit earlier (two lists of
/// format names with no tie, where deleting a pattern left the name accepted and validating nothing), and
/// it was reintroduced by the commit that fixed it. One table is the fix in both places.
/// </para>
/// <para>
/// <b>Each entry carries both ways of detecting the feature</b>, and that is the tie rather than a
/// convenience. The mapper walks a typed <see cref="FieldDescriptor"/>/<see cref="EntityDescriptor"/>; the
/// validator walks raw <see cref="System.Text.Json.JsonElement"/> before anything is parsed. Two passes,
/// two representations — so an entry states its <see cref="UnhonouredFeature{T}.Path"/> (for the JSON pass)
/// <em>and</em> its predicate (for the typed pass), and a new feature cannot be added to one pass without
/// the other, because the record will not construct without both.
/// </para>
/// <para>
/// <b>Every entry is an <c>Error</c>, and <c>Warning</c> was never a real middle path.</b> The mapper
/// throws for each of these whatever severity the validator reports, so a warning would describe a
/// tolerance the apply path does not have.
/// </para>
/// <para>
/// <b>Each entry names what silently happens instead, not "unsupported".</b> An author told "not supported
/// yet" removes the key and moves on; one told the field is therefore unconstrained, or the column
/// permanently null, knows what they just lost. The fix suggestion then names the alternative, because a
/// refusal with no alternative sends an agent hunting for a flag to flip.
/// </para>
/// <para>
/// Entries leave as features land: PR5 removes the hook points it implements, PR6 owns <c>computed</c>,
/// <c>rollup</c> and <c>default</c>, and soft delete leaves with its own implementation. Shrinking this
/// table is the whole of "implementing" one of them from this layer's point of view.
/// </para>
/// </remarks>
internal static class UnhonouredFeatures
{
    /// <summary>
    /// Every field-level feature the schema declares and this build drops. Ordered widest consequence
    /// first, so a field declaring several is refused by the one that costs the most.
    /// </summary>
    internal static IReadOnlyList<UnhonouredFeature<FieldDescriptor>> OnAField { get; } =
    [
        new(
            "computed",
            field => field.Computed is not null,
            "Computed fields are not supported yet: the expression is never evaluated, so the column stays null.",
            "Remove 'computed' or track the CEL→SQL compiler in #21."),
        new(
            "rollup",
            field => field.Rollup is not null,
            "Rollups are not supported yet: nothing maintains the aggregate, so the column reads as "
            + "permanently null while looking like data.",
            "Remove 'rollup' and compute the aggregate in a query for now; rollups are deferred past F3."),
        new(
            "validation",
            field => field.Validation is not null,
            "Field 'validation' is not evaluated yet, so a value the expression forbids is accepted — the "
            + "field is not constrained at all.",
            "Remove 'validation'. Enforce the rule in a before-hook once #22 lands, or express it with a facet "
            + "the API does validate — 'maxLength', 'precision'/'scale', enum 'values' or a 'format'."),
        new(
            "default",
            field => field.Default is not null,
            "Field 'default' is not honoured yet: no column default is emitted and the value is dropped "
            + "before any writer sees it, so the field is simply null — and on a 'required' field that is an "
            + "INSERT of NULL into a NOT NULL column.",
            "Remove 'default' and send the value explicitly on create. Refused rather than ignored because a "
            + "silently absent default is a wrong stored value, which costs more than sending the field."),
    ];

    /// <summary>
    /// Every entity-level feature the schema declares and this build drops — including
    /// <c>softDelete</c>, and one entry per <b>hook point</b> rather than one for the whole
    /// <c>hooks</c> block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>softDelete</c> is in the table, not beside it.</b> It was a fifth <c>if</c> in the validator and
    /// a separate guard in the mapper, which made the table an exception rather than the rule. Its
    /// justification for being tabulated is the field features' justification: one fact, one place.
    /// </para>
    /// <para>
    /// <b>Per hook point, so PR5 can shrink this incrementally.</b> Refusing the <c>hooks</c> block as a whole
    /// would force PR5 into an all-or-nothing switch — it could not ship <c>beforeCreate</c> while
    /// <c>afterDelete</c> is still unimplemented without either lying about the rest or leaving the whole
    /// block refused. Six entries let each one leave on the day it starts working. It is the same move PR2
    /// made for <c>softDelete</c>: refuse the behaviour, keep the declared shape, so the implementing issue
    /// inherits a shape rather than designing one.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<UnhonouredFeature<EntityDescriptor>> OnAnEntity { get; } =
    [
        new(
            "softDelete",
            entity => entity.SoftDelete == true,
            "Soft delete is not supported yet: a delete would remove the row outright and reads would not "
            + "exclude it, which is irrecoverable data loss where the schema promises recoverability.",
            "Remove 'softDelete' or track the soft-delete implementation issue. A flag written as false is "
            + "not a declaration and maps normally."),
        .. HookPoints(),
    ];

    /// <summary>One refusal per hook point, each naming the operation it would have run on.</summary>
    /// <remarks>
    /// <para>
    /// The consequence is worded per <em>phase</em> because the two lose different things. A
    /// <c>before*</c> hook may reject or mutate <em>in the write transaction</em>, so dropping it means a
    /// write the author believes is being vetted or patched is neither: the clearest case in this repo's own
    /// examples is <c>simple-tasks</c>' <c>beforeUpdate</c>, which sets <c>completed_at</c> when a task is
    /// marked done — refuse the hook and <c>completed_at</c> is a permanently null column, the very same
    /// silent-wrong-value outcome <c>rollup</c> is refused for. An author should not meet that surprise
    /// twice, so it is named here rather than discovered.
    /// </para>
    /// <para>
    /// An <c>after*</c> hook is post-commit from the outbox, so dropping it loses an effect the row's own
    /// state does not record — a notification that never goes out, a downstream system never told.
    /// </para>
    /// </remarks>
    private static IEnumerable<UnhonouredFeature<EntityDescriptor>> HookPoints() =>
    [
        Hook("beforeCreate", "create", hooks => hooks.BeforeCreate, InTransaction),
        Hook("beforeUpdate", "update", hooks => hooks.BeforeUpdate, InTransaction),
        Hook("beforeDelete", "delete", hooks => hooks.BeforeDelete, InTransaction),
        Hook("afterCreate", "create", hooks => hooks.AfterCreate, PostCommit),
        Hook("afterUpdate", "update", hooks => hooks.AfterUpdate, PostCommit),
        Hook("afterDelete", "delete", hooks => hooks.AfterDelete, PostCommit),
    ];

    private const string InTransaction =
        "a before-hook runs inside the write transaction and may reject or mutate the payload, so the write "
        + "is neither vetted nor patched — a field the hook was meant to set stays permanently null, exactly "
        + "as an unmaintained 'rollup' column does";

    private const string PostCommit =
        "an after-hook runs post-commit from the outbox, so the effect simply never happens — and nothing in "
        + "the row's own state records that it did not";

    /// <summary>Builds one hook point's entry, so the six differ only in the words that should differ.</summary>
    /// <param name="point">The hook point's key under <c>hooks</c>.</param>
    /// <param name="operation">The operation it would run on.</param>
    /// <param name="declared">Reads that point's list off the entity's hooks.</param>
    /// <param name="consequence">What dropping a hook of this phase costs.</param>
    private static UnhonouredFeature<EntityDescriptor> Hook<T>(
        string point,
        string operation,
        Func<EntityHooks, IReadOnlyList<T>?> declared,
        string consequence) => new(
        $"hooks/{point}",
        entity => entity.Hooks is { } hooks && declared(hooks) is { Count: > 0 },
        $"Lifecycle hooks are not supported yet, so the '{point}' hooks on this entity never run and every "
        + $"{operation} proceeds as if they were not declared: {consequence}.",
        $"Remove the '{point}' hooks, or track the hooks pipeline in #22. Refusing per hook point rather than "
        + "per 'hooks' block is deliberate: each one is lifted the day it starts working, so declaring the "
        + $"others costs you nothing once '{point}' lands.");
}

/// <summary>
/// One feature the frozen schema declares and this build does not honour, stated once for both passes that
/// refuse it.
/// </summary>
/// <typeparam name="T">The descriptor the typed pass inspects — a field's or an entity's.</typeparam>
/// <param name="Path">
/// The key, relative to the field or entity, as a JSON Pointer path with no leading slash — so a nested
/// declaration is <c>hooks/beforeUpdate</c>. Both the raw-JSON detection and the error's own pointer are
/// built from it, which is what keeps the reported path and the checked key from drifting.
/// </param>
/// <param name="IsDeclaredBy">
/// Whether a parsed descriptor declares it. Paired with <paramref name="Path"/> in one entry on purpose:
/// the JSON pass and the typed pass are two representations of one question, and a table that carried only
/// the key would let the typed pass fall behind silently.
/// </param>
/// <param name="Consequence">What silently happens instead, concretely — never the word "unsupported" alone.</param>
/// <param name="Fix">What to do instead, and where the feature is tracked.</param>
internal sealed record UnhonouredFeature<T>(
    string Path,
    Func<T, bool> IsDeclaredBy,
    string Consequence,
    string Fix);
