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
/// Entries leave as features land, and three already have: PR5a removed the three <c>after*</c> hook points
/// it implements, PR5b owns the three <c>before*</c> ones, PR6 owns <c>computed</c>, <c>rollup</c> and
/// <c>default</c>, and soft delete leaves with its own implementation. Shrinking this table is the whole of
/// "implementing" one of them from this layer's point of view.
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
    /// <b>Per hook point, so PR5 can shrink this incrementally — and it has.</b> Refusing the <c>hooks</c>
    /// block as a whole would have forced PR5 into an all-or-nothing switch: it could not ship
    /// <c>afterUpdate</c> while <c>beforeUpdate</c> is still unimplemented without either lying about the rest
    /// or leaving the whole block refused. Six entries let each one leave on the day it starts working, and
    /// three of them did. It is the same move PR2 made for <c>softDelete</c>: refuse the behaviour, keep the
    /// declared shape, so the implementing issue inherits a shape rather than designing one.
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
    /// <b>Three entries left when the after-hooks landed, and that is the per-hook-point shape paying for
    /// itself.</b> PR5a compiles <c>afterCreate</c>/<c>afterUpdate</c>/<c>afterDelete</c> into the policy
    /// catalog and dispatches them from the outbox, so their entries are gone; the three <c>before*</c>
    /// points stay, because a before-hook runs <em>in the write transaction</em> and nothing in this build
    /// does. No author of a <c>before*</c> hook sees a changed message — which is exactly what "each one is
    /// lifted the day it starts working" was written to buy.
    /// </para>
    /// <para>
    /// The consequence is worded per <em>phase</em> because the two lose different things. A
    /// <c>before*</c> hook may reject or mutate <em>in the write transaction</em>, so dropping it means a
    /// write the author believes is being vetted or patched is neither: the clearest case in this repo's own
    /// examples is <c>simple-tasks</c>' <c>beforeUpdate</c>, which sets <c>completed_at</c> when a task is
    /// marked done — refuse the hook and <c>completed_at</c> is a permanently null column, the very same
    /// silent-wrong-value outcome <c>rollup</c> is refused for. An author should not meet that surprise
    /// twice, so it is named here rather than discovered.
    /// </para>
    /// </remarks>
    private static IEnumerable<UnhonouredFeature<EntityDescriptor>> HookPoints() =>
    [
        Hook("beforeCreate", "create", hooks => hooks.BeforeCreate, InTransaction),
        Hook("beforeUpdate", "update", hooks => hooks.BeforeUpdate, InTransaction),
        Hook("beforeDelete", "delete", hooks => hooks.BeforeDelete, InTransaction),
    ];

    private const string InTransaction =
        "a before-hook runs inside the write transaction and may reject or mutate the payload, so the write "
        + "is neither vetted nor patched — a field the hook was meant to set stays permanently null, exactly "
        + "as an unmaintained 'rollup' column does";

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

    /// <summary>
    /// A raw JSONata expression in any <c>$defs/jsonata</c>-typed slot: refused, never partially evaluated.
    /// </summary>
    /// <remarks>
    /// There is no mature .NET JSONata implementation, and a hand-rolled subset would accept the part it
    /// implements and silently produce a different payload for the rest — <c>$merge</c>, <c>$map</c>,
    /// <c>^(…)</c> ordering, predicate contexts, <c>$$</c> root scope. That is the <c>default</c> case, not the
    /// <c>webhooks</c> case: the action still runs, so nothing looks broken, and the body is not the one the
    /// author declared.
    /// </remarks>
    internal static UnhonouredSlot RawJsonata { get; } = new(
        "JSONata",
        "JSONata transformations are not evaluated yet: the action still runs, but with Alvo's canonical event "
        + "envelope as its body instead of the transformation declared here — a delivery that succeeded "
        + "carrying data you did not declare, which is indistinguishable from a bug in the consumer.",
        "Use a '{{...}}' template instead (e.g. \"{{new.title}}\"), which this build does render, or remove the "
        + "transformation and accept the canonical envelope. A partial JSONata implementation is deliberately "
        + "not offered: silently producing a different payload for the part it does not implement costs more "
        + "than this refusal. Tracked in #149.");

    /// <summary>
    /// An <c>email</c> action's <c>data</c> slot: compiled and validated, and then read by nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refused because it was a dead slot, which is worse than an unimplemented one.</b> The compiler parsed
    /// it, resolved every placeholder against the entity's schema and stored the result, and the executor renders
    /// only <c>to</c>, <c>subject</c> and <c>body</c> — there is no <c>data.*</c> placeholder root for a subject
    /// or a body to reach it with. So an author following the schema's own doc comment got a clean apply and a
    /// silently discarded value.
    /// </para>
    /// <para>
    /// That is the identical failure mode <see cref="RawJsonata"/> is refused for — the action still runs and the
    /// message is not the one the author declared — at an implementation rate of zero. Adding a <c>data.*</c>
    /// root instead would be new placeholder surface, which belongs to the PR that has a use for it.
    /// </para>
    /// </remarks>
    internal static UnhonouredSlot EmailData { get; } = new(
        "email.data",
        "An 'email' action's 'data' is not rendered: it is validated when the descriptor is applied and then "
        + "read by nothing, so the mail goes out with the referenced template's own subject and body and the "
        + "values declared here are silently dropped — a message that was delivered without the data you "
        + "declared, which is indistinguishable from a template bug.",
        "Move the values into the template's 'subject'/'body' as '{{...}}' placeholders over 'new'/'old'/"
        + "'event'/'@user.id', which this build does render, or remove 'data'. A 'data.*' placeholder root is "
        + "new surface and lands with the PR that reads it.");

    /// <summary>
    /// A message template whose body lives in a bundle file, refused for the hook that would have rendered it.
    /// </summary>
    /// <remarks>
    /// Refused per <em>reference</em> rather than per declaration: a template nothing references keeps its
    /// <see cref="UnhonouredSubsystems"/> warning, because its absence is observable and it costs nothing. A
    /// template an after-hook <em>does</em> reference is the silent case — nothing in this build reads a file
    /// out of a descriptor bundle, so the mail would go out with an empty body.
    /// </remarks>
    internal static UnhonouredSlot TemplateBodyFile { get; } = new(
        "bodyFile",
        "A template's 'bodyFile' is not read yet: nothing in this build resolves a path inside a descriptor "
        + "bundle, so an after-hook rendering this template would send a message with an empty body rather "
        + "than fail.",
        "Move the body inline into the template's 'body' — it takes the same '{{...}}' placeholders — or stop "
        + "referencing this template from an after-hook until bundle files are read.");

    /// <summary>The refusal for one action type the frozen <c>$defs/action</c> declares and this build never runs.</summary>
    /// <param name="type">The action's <c>type</c> discriminator, exactly as the schema spells it.</param>
    internal static UnhonouredSlot UnhonouredAction(string type) => new(
        type,
        $"The '{type}' action is declared in the schema but not implemented in this build, so this hook "
        + $"{ActionConsequence(type)}.",
        ActionFix(type));

    /// <summary>What specifically does not happen for one unimplemented action type.</summary>
    /// <remarks>
    /// One arm per action rather than one sentence with the type interpolated in: the three lose different
    /// things, and "not implemented" tells an author nothing they can weigh.
    /// </remarks>
    /// <param name="type">The action's <c>type</c> discriminator.</param>
    private static string ActionConsequence(string type) => type switch
    {
        FunctionType => "invokes nothing — no function declared under 'functions' runs, on this hook or on "
            + "any trigger or schedule it declares elsewhere",
        HttpCallType => "makes no request: the URL is never called, and 'headersSecretRef' is never read, so "
            + "a receiver you believe is being notified is not",
        EntityUpdateType => "writes nothing — no record is written or patched on the target entity, and no "
            + "event is emitted for the write that did not happen",
        _ => throw new ArgumentOutOfRangeException(
            nameof(type), type, "Unmapped action type; name what specifically does not happen for it here."),
    };

    /// <summary>The alternative for one unimplemented action type, and where it is tracked.</summary>
    /// <param name="type">The action's <c>type</c> discriminator.</param>
    private static string ActionFix(string type) => type switch
    {
        FunctionType => "Use a 'webhook' or 'email' action, which this build does run. Custom functions are "
            + "an F4 concern and the schema freezes their shape ahead of the implementation.",
        HttpCallType => "Declare the target under 'webhooks.endpoints' and use a 'webhook' action instead — "
            + "that is the managed path, and it is the one this build delivers on.",
        EntityUpdateType => "Perform the follow-up write through the Data API for now. 'entity.update' lands "
            + "with automation, where the causation chain it creates can be bounded.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(type), type, "Unmapped action type; name the alternative for it here."),
    };

    private const string FunctionType = "function";
    private const string HttpCallType = "http.call";
    private const string EntityUpdateType = "entity.update";
}

/// <summary>
/// One feature this build does not honour that is detected by a <b>compiler</b> rather than by a
/// descriptor-shape predicate, so it carries the words without carrying a path.
/// </summary>
/// <remarks>
/// <see cref="UnhonouredFeature{T}"/>'s two-pass tie does not apply. The raw-JSON pass cannot ask whether a
/// string is a well-formed <c>{{…}}</c> template without reimplementing the classifier over
/// <see cref="System.Text.Json.JsonElement"/>, and it cannot ask which template a hook references either; the
/// typed pass already knows the exact JSON Pointer of the slot it is looking at. So the <em>detection</em>
/// lives where the action is compiled and only the <em>wording</em> lives here — which is what keeps one
/// authority for the words, exactly as the other two shapes do.
/// </remarks>
/// <param name="Feature">The feature's name, for a reader grouping refusals by what they are about.</param>
/// <param name="Consequence">What silently happens instead, concretely — never the word "unsupported" alone.</param>
/// <param name="Fix">What to do instead, and where the feature is tracked.</param>
internal sealed record UnhonouredSlot(string Feature, string Consequence, string Fix);

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
