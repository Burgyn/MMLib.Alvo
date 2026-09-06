using System.Reflection;

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
/// Entries leave as features land, and five already have: PR5a removed the three <c>after*</c> hook points it
/// implements, and PR6 removed <c>computed</c> and <c>rollup</c>. PR5b owns the three <c>before*</c> ones,
/// <c>default</c> is still unowned, and soft delete leaves with its own implementation. Shrinking this table is
/// the whole of "implementing" one of them from this layer's point of view — and note that <c>computed</c> and
/// <c>rollup</c> did not leave as a bare deletion: their refusals were <em>replaced</em> by
/// <see cref="RollupResolver"/>'s ladder checks, because a feature that is honoured in general and
/// unresolvable in a particular descriptor still has to be refused at apply.
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
    /// <b>all six now have</b> — the three <c>after*</c> points with PR5a, the three <c>before*</c> points
    /// with PR5b — which is why this table holds no hook entry at all. The shape is kept here as the record
    /// of how they left, one at a time, rather than deleted with the last of them: it is the same move PR2
    /// made for <c>softDelete</c> — refuse the behaviour, keep the declared shape, so the implementing issue
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
    ];

    /// <summary>
    /// A <c>rollup</c>'s optional <c>where</c> filter: the one part of the frozen rollup shape #21 does not
    /// honour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An <see cref="UnhonouredSlot"/> rather than a table entry, for the reason that shape exists.</b> The
    /// two-pass tie does not apply: the raw-JSON validator pass would have to walk into the <c>rollup</c> object
    /// it does not parse to find the key, while the typed pass already holds the parsed declaration and the
    /// field it belongs to. So the detection lives in <c>RollupResolver</c> and only the wording lives here.
    /// </para>
    /// <para>
    /// Refused rather than ignored because ignoring it is the silent case: the aggregate is still maintained,
    /// still transactionally consistent, and computed over <em>every</em> child record rather than the declared
    /// subset. The parent's column then holds a number that is larger than the author asked for, by an amount
    /// only the data knows — indistinguishable from a bug in whatever reads it.
    /// </para>
    /// </remarks>
    internal static UnhonouredSlot RollupWhere { get; } = new(
        "rollup.where",
        "A rollup's 'where' filter is not evaluated yet: the aggregate is still maintained, but it aggregates "
        + "every record of the child entity instead of the subset this filter declares — a stored number that "
        + "is silently wrong rather than absent.",
        "Remove 'where' and aggregate every child record, or move the distinction into the model: a separate "
        + "child entity, or a second rollup once filtered rollups land. A partial implementation is "
        + "deliberately not offered — an aggregate over the wrong row set costs more than this refusal.");

    /// <summary>
    /// A wildcard subscription — <c>entity.orders.*</c> — in either slot the frozen schema types as
    /// <c>$defs/eventPattern</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one entry in this file that refuses something whose absence <em>is</em> observable</b>, which is
    /// <see cref="UnhonouredSubsystems"/>' line for warning rather than refusing. It is refused anyway because
    /// the two halves of "observable" come apart here: the absence is observable <em>today</em> (no automation
    /// rule fires at all, and the author sees that), while the consequence is observable <b>never</b>. The day
    /// automation lands, a wildcard already sitting in a descriptor becomes a fan-out across every tenant with
    /// nobody re-reading the file that declared it — and a delivery that went to the wrong tenant is not an
    /// absence anyone notices. The descriptor is the durable artifact, which is the argument for tolerating one
    /// that runs ahead of the build in general, and the argument against it in exactly this case.
    /// </para>
    /// <para>
    /// <b>Why the wildcard and not the whole pattern.</b> An exact pattern names one entity, so whatever
    /// scoping the rule engine gains later applies to it unchanged; a wildcard is the shape whose meaning
    /// silently widens when a tenant is added, and it is the shape <c>baas-analyza.md:657</c> warns about. The
    /// matcher itself is not built because it cannot be built correctly yet:
    /// <see cref="Events.AlvoEvent"/> carries no tenant attribute, so nothing at delivery could scope a
    /// subscription to the envelope's tenant, and the adversarial cross-tenant fact that ruling requires would
    /// have no tenant on either side of its comparison. Giving the envelope one is issue <b>#153</b>.
    /// </para>
    /// </remarks>
    internal static UnhonouredSlot WildcardSubscription { get; } = new(
        "trigger.event",
        "A wildcard event subscription is not matched yet: no rule fires for it today, and on the build that "
        + "does implement matching it would subscribe to every entity or every operation of every tenant at "
        + "once — a cross-tenant fan-out nobody re-reads this descriptor to catch, because the event envelope "
        + "carries no tenant for a subscription to be scoped by (#153).",
        "Name the entity and the operation exactly — 'entity.orders.created' rather than 'entity.orders.*' — "
        + "and declare one rule per pair. Wildcards are refused rather than accepted-and-ignored because a "
        + "descriptor outlives the build that applied it: accepted today, it becomes the fan-out above on the "
        + "day matching lands, with nothing between it and delivery.");

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

    /// <summary>The action <c>type</c> discriminators this build declares and never runs.</summary>
    /// <remarks>
    /// Declared <b>before</b> <see cref="EveryFixSuggestion"/> deliberately: a static field initializer runs
    /// in declaration order, so a list built above this line would enumerate <see langword="null"/>.
    /// </remarks>
    internal static IReadOnlyList<string> EveryActionType { get; } =
        [FunctionType, HttpCallType, EntityUpdateType];

    /// <summary>
    /// Every fix suggestion this table can produce — the one thing common to every refusal it authors, and
    /// therefore the way to ask whether a refusal came from <em>here</em> at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists so a fact can assert a refusal's <em>reason</em> rather than its type.</b>
    /// <c>DescriptorToSchemaMapperTests.Every_example_marked_not_runnable_really_is_refused</c> asserted only
    /// <c>Should.Throw&lt;InvalidDataException&gt;</c>, so a CEL syntax error in a shipped example stood in
    /// silently for the feature refusal the not-runnable marker claims — and the marker the test exists to
    /// force to shrink would never have shrunk (deviation 76). Matching against this list is what makes the
    /// assertion mean "refused by an unhonoured feature", which is the claim the marker actually makes.
    /// </para>
    /// <para>
    /// <b>The <see cref="UnhonouredSlot"/> half is discovered by reflection, and that is the whole point of
    /// the shape.</b> An enumeration that named <c>RollupWhere</c>, <c>RawJsonata</c> and the rest by hand
    /// would be precisely the hand-copied list this file's own opening remark exists to forbid — and it would
    /// fail in the worse direction: a new slot omitted from it does not break a build, it silently narrows
    /// the assertion above back toward what deviation 76 complained about. Reflection over this type's own
    /// static <see cref="UnhonouredSlot"/> properties makes a new slot join the list by being declared.
    /// </para>
    /// <para>
    /// The two typed tables are enumerated directly because they already are collections, and the three
    /// action refusals are generated rather than declared — so their types come from the same three constants
    /// <see cref="ActionFix"/> switches on, whose <c>default</c> arm throws for an unmapped type.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> EveryFixSuggestion { get; } =
    [
        .. OnAField.Select(feature => feature.Fix),
        .. OnAnEntity.Select(feature => feature.Fix),
        .. EveryDeclaredSlot().Select(slot => slot.Fix),
        .. EveryActionType.Select(ActionFix),
    ];

    /// <summary>Every <see cref="UnhonouredSlot"/> this type declares, found rather than listed.</summary>
    /// <remarks>
    /// A slot declared <em>below</em> <see cref="EveryFixSuggestion"/> reflects as <see langword="null"/>,
    /// because a static initializer runs in declaration order — so that case throws by name instead of
    /// quietly contributing nothing, which would be the silent narrowing this whole shape exists to prevent.
    /// </remarks>
    private static IEnumerable<UnhonouredSlot> EveryDeclaredSlot() =>
        typeof(UnhonouredFeatures)
            .GetProperties(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(property => property.PropertyType == typeof(UnhonouredSlot))
            .Select(property => (UnhonouredSlot?)property.GetValue(obj: null)
                ?? throw new InvalidOperationException(
                    $"Unhonoured slot '{property.Name}' is declared below EveryFixSuggestion, so it "
                    + "initializes after it and would contribute no fix suggestion. Move it above."));

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
