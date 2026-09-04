using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Every refusal the query-string parser can produce, composed in one place.
/// </summary>
/// <remarks>
/// <para>
/// One catalogue rather than a message at each call site, for the reason
/// <c>AlvoFilter.EnsureWithinLimits</c> gives for being one entry point: a wording invented where it is
/// thrown is a wording nobody compares with its siblings, and here the comparison <em>is</em> the security
/// property — <see cref="UnavailableField"/> must be byte-identical whether the field was undeclared,
/// masked, or merely a mistyped reserved keyword.
/// </para>
/// <para>
/// <b>No producer here interpolates caller-supplied text.</b> The only values that reach a message are
/// server-owned: a configured option's bound, one of the port's own limits, a declared field's <em>type</em>,
/// and the two closed lists (the operators and the reserved keys). See <see cref="AlvoViolation"/> for why.
/// </para>
/// </remarks>
internal static class QueryViolations
{
    /// <summary>The pointer every filter refusal carries. Not the parameter's name — in PostgREST's grammar that <em>is</em> the field name.</summary>
    internal const string FilterPointer = "filter";

    private static readonly string _fieldFix =
        "Name a field this entity declares and your policy lets you read. The reserved query parameters are "
        + $"{ReservedQueryKeys.AsList}.";

    /// <summary>
    /// The one refusal for a field name this caller cannot use — undeclared, masked, or a mistyped reserved
    /// keyword read as a filter — naming neither the field nor which of the three it was.
    /// </summary>
    /// <remarks>
    /// <b>Read from the port, not worded here.</b> This is the message whose sameness across layers <em>is</em>
    /// the confidentiality property, and it was a hand-synced literal in three assemblies before
    /// <see cref="AlvoAuthorizationException.QueryFieldUnavailable"/> existed. The parser refuses before the port
    /// is reached, so the two are never observed side by side and nothing would have caught them drifting.
    /// </remarks>
    internal const string UnavailableFieldMessage = AlvoAuthorizationException.QueryFieldUnavailable;

    /// <summary>The refusal for a field name this caller cannot use.</summary>
    /// <param name="pointer">Which parameter the name appeared in.</param>
    internal static AlvoViolation UnavailableField(string pointer) =>
        new(pointer, "unavailable-field", UnavailableFieldMessage, _fieldFix);

    /// <summary>The refusal for an operator spelling that is not on the allow-list.</summary>
    internal static AlvoViolation UnknownOperator() => new(
        FilterPointer,
        "unknown-operator",
        "A filter term names an operator this API does not implement.",
        $"Use one of: {FilterOperators.AsList}. The term's shape is <field>=<operator>.<value>.");

    /// <summary>The refusal for a filter term whose shape is wrong before any operator is resolved.</summary>
    internal static AlvoViolation MalformedTerm() => new(
        FilterPointer,
        "malformed-filter",
        "A filter term is not in the form <field>=<operator>.<value>.",
        $"Write, for example, year=gte.2020 — or {ReservedQueryKeys.Or}=(color.eq.red,color.eq.blue) for a group.");

    /// <summary>The refusal for an <c>or</c>/<c>and</c> group that is not a balanced, non-empty parenthesised list.</summary>
    internal static AlvoViolation MalformedGroup() => new(
        FilterPointer,
        "malformed-filter-group",
        "A filter group is not a balanced, non-empty parenthesised list of terms.",
        "Write and=(year.gte.2020,year.lte.2024); an unbalanced or empty group has no meaning.");

    /// <summary>The refusal for an <c>in</c> whose operand is not a parenthesised candidate list.</summary>
    internal static AlvoViolation MalformedInList() => new(
        FilterPointer,
        "malformed-in-list",
        "An 'in' filter's operand is not a parenthesised list of candidates.",
        "Write make=in.(skoda,vw); a bare value is not a list.");

    /// <summary>The refusal for an <c>is</c> whose operand is not one of the three SQL <c>IS</c> accepts.</summary>
    internal static AlvoViolation MalformedIsOperand() => new(
        FilterPointer,
        "malformed-is-operand",
        "An 'is' filter's operand is not null, true or false.",
        "SQL's own IS accepts only those three, and true/false only on a boolean field; compare with 'eq' instead.");

    /// <summary>
    /// The refusal for a value the field's own type cannot hold. Names the type, which the caller already
    /// knows the field has — never the value, which is theirs.
    /// </summary>
    /// <param name="field">The declared field the value was compared against.</param>
    internal static AlvoViolation UnrepresentableValue(FieldSchema field) => new(
        FilterPointer,
        "invalid-filter-value",
        "A filter value cannot be held by the type of the field it is compared against.",
        $"That field is declared '{Spelled(field.Type)}'; supply a value of that type, "
        + "without a fractional part where the type is integral.");

    /// <summary>The refusal for a text value carrying a NUL, which no engine Alvo supports can represent.</summary>
    internal static AlvoViolation UnrepresentableText() => new(
        FilterPointer,
        "invalid-filter-value",
        "A filter value contains a NUL character, which no engine Alvo supports can represent.",
        "Remove the NUL from the value.");

    /// <summary>The refusal for an operator whose meaning the field's type does not admit.</summary>
    /// <param name="operator">The resolved operator.</param>
    /// <param name="field">The declared field it was applied to.</param>
    internal static AlvoViolation UnsupportedOperatorForField(AlvoFilterOperator @operator, FieldSchema field) => new(
        FilterPointer,
        "unsupported-operator-for-field",
        "A filter applies an operator the type of the field it names does not support.",
        $"'{FilterOperators.WireName(@operator)}' cannot be applied to a '{Spelled(field.Type)}' field: "
        + "like/ilike are string pattern matches, and gt/gte/lt/lte need a type this port orders.");

    /// <summary>
    /// The refusal for a filter nested past what the parser will descend into — raised <em>before</em> the
    /// offending subtree exists, which is what keeps a ten-thousand-deep group from being ten thousand stack
    /// frames.
    /// </summary>
    /// <remarks>
    /// Depth, breadth and candidate count get <b>three</b> codes rather than one because they have three
    /// different fixes — flatten, narrow, split — which is exactly the distinction
    /// <see cref="AlvoFilter.EnsureWithinLimits"/> draws in its own messages. It is also what makes the
    /// parser's own caps observable: with one shared code, removing the depth check would still be answered by
    /// the breadth budget under the same code, and no fact could tell.
    /// </remarks>
    internal static AlvoViolation FilterTooDeep() => new(
        FilterPointer,
        "filter-too-deep",
        "The filter nests more deeply than this API parses.",
        $"Nest at most {FilterGroupParser.MaxNesting} groups. Flatten the nesting — an and/or over many terms "
        + "is one level, not one level per term.");

    /// <summary>The refusal for a filter carrying more nodes in total than the port's breadth limit allows.</summary>
    internal static AlvoViolation FilterTooWide() => new(
        FilterPointer,
        "filter-too-wide",
        "The filter carries more terms than this API parses.",
        $"Send at most {AlvoFilter.MaxTerms} terms across the whole query. A filter this wide is a statement "
        + "the engine may refuse outright.");

    /// <summary>
    /// The refusal for too many <c>in</c> candidates — in one list, or across the whole query.
    /// </summary>
    /// <remarks>
    /// <b>The total matters as much as the longest list, and the port only measures the longest.</b>
    /// <see cref="AlvoFilter.MaxInCandidates"/> is per-list, so a filter carrying the maximum number of terms each
    /// with a maximum list is <c>MaxTerms × MaxInCandidates</c> — 256 000 bind parameters in one statement, well
    /// past the 32 766 ceiling <see cref="AlvoFilter.MaxInCandidates"/>' own remarks measured on SQLite, and every
    /// one of them caller-supplied. Capping the total at the same number bounds the statement while still letting
    /// a single list use the whole allowance.
    /// </remarks>
    internal static AlvoViolation TooManyInCandidates() => new(
        FilterPointer,
        "too-many-in-candidates",
        "The query lists more 'in' candidates than this API parses.",
        $"List at most {AlvoFilter.MaxInCandidates} candidates in one filter and across the whole query. Split "
        + "them across requests — every candidate becomes its own bind parameter.");

    /// <summary>
    /// The refusal for a filter the <em>port's</em> own guard rejected, carrying the port's own wording.
    /// </summary>
    /// <remarks>
    /// <b>This code must never reach a caller.</b> The parser's own caps are set strictly inside the port's —
    /// one level of nesting is reserved for the conjunction this layer wraps several parameters in, and that
    /// conjunction is charged against the term budget like any other node — so
    /// <see cref="AlvoFilter.EnsureWithinLimits"/> cannot refuse what the parser produced. It is still called,
    /// because it is the port's rule and calling it is how a later divergence is caught; this violation is what
    /// that divergence would look like, and <c>QueryStringParserPropertyTests</c> asserts a whole generated
    /// corpus never produces it.
    /// </remarks>
    /// <param name="message">The port's own refusal text.</param>
    internal static AlvoViolation FilterBeyondPortLimits(string message) => new(
        FilterPointer,
        "filter-beyond-port-limits",
        message,
        "This is a defect in the API's own accounting rather than a request to fix; report it.");

    /// <summary>The refusal for a page size that is not a positive integer within the configured maximum.</summary>
    /// <param name="maxPageSize">The configured maximum.</param>
    internal static AlvoViolation InvalidPageSize(int maxPageSize) => new(
        ReservedQueryKeys.Limit,
        "invalid-page-size",
        "The requested page size is not a whole number between 1 and the maximum this API allows.",
        $"Ask for between 1 and {maxPageSize} rows. A larger request is refused rather than quietly "
        + "reduced, because a clamped page makes a client's own paging arithmetic wrong.");

    /// <summary>The refusal for an offset that is not a non-negative integer.</summary>
    internal static AlvoViolation InvalidOffset() => new(
        ReservedQueryKeys.Offset,
        "invalid-offset",
        "The requested offset is not a whole number of zero or more rows.",
        "Send offset=0 or higher — or, better, page with the 'after' cursor, which does not shift under "
        + "concurrent writes.");

    /// <summary>
    /// The refusal for a cursor no page could have issued — empty, or longer than this API passes through.
    /// </summary>
    /// <remarks>
    /// One refusal for both, because a cursor is opaque: this layer cannot say <em>why</em> a cursor is wrong
    /// beyond "no page minted that", and distinguishing the two would start describing the encoding to a caller
    /// who is contractually not supposed to know it.
    /// </remarks>
    /// <param name="maxLength">The longest cursor this API passes through.</param>
    internal static AlvoViolation InvalidCursor(int maxLength) => new(
        ReservedQueryKeys.After,
        "invalid-cursor",
        "The cursor is empty or longer than any page could have issued.",
        $"Send the 'next' value a previous page returned — at most {maxLength} characters — or omit 'after' for "
        + "the first page.");

    /// <summary>The refusal for a query anchoring one window two ways, carrying the port's own wording.</summary>
    /// <param name="message">The port's own refusal text.</param>
    internal static AlvoViolation ConflictingPagingWindow(string message) => new(
        ReservedQueryKeys.After,
        "conflicting-paging",
        message,
        "Send either 'after' or 'offset'.");

    /// <summary>The refusal for a sort key whose direction or null placement is not one of the four spellings.</summary>
    internal static AlvoViolation MalformedOrder() => new(
        ReservedQueryKeys.Order,
        "malformed-order",
        "A sort key carries a modifier that is not a direction or a null placement.",
        "Write order=year.desc.nullsfirst — the modifiers are asc, desc, nullsfirst and nullslast, in that "
        + "order, and each at most once.");

    /// <summary>
    /// The refusal for a sort key named twice. A repeated key can never change the order — the first
    /// occurrence already decides it — so it is a mistake rather than a request, and admitting it would let a
    /// caller make the server compose an unbounded <c>ORDER BY</c>.
    /// </summary>
    internal static AlvoViolation RepeatedSortKey() => new(
        ReservedQueryKeys.Order,
        "repeated-sort-key",
        "A sort key names the same field more than once.",
        "Name each field at most once; a later occurrence of a field cannot change the order the first gave it.");

    /// <summary>The refusal for a projection that names nothing.</summary>
    internal static AlvoViolation EmptySelect() => new(
        ReservedQueryKeys.Select,
        "malformed-select",
        "The projection names no fields.",
        "Write select=make,model — or omit 'select' entirely for every readable field.");

    /// <summary>
    /// The refusal for a projection entry that is neither <c>field</c> nor <c>alias:field</c>, or whose
    /// alias is not shaped like a field name.
    /// </summary>
    /// <remarks>
    /// <b>An alias must match the field-name grammar</b> (<c>^[a-z][a-z0-9_]{0,62}$</c>) and must not be one
    /// of the reserved names. A deliberate narrowing of PostgREST, which admits an arbitrary alias: an alias
    /// is a field name <em>in the response</em>, so an agent reading the body should not have to tell a real
    /// field from caller-supplied text, and an unbounded alias is caller-controlled bytes in a response key
    /// for no gain. The reserved-name half is consistency rather than necessity — an alias is never a query
    /// key and creates no ambiguity — but a response key no descriptor is allowed to declare should not be
    /// reachable by renaming.
    /// </remarks>
    internal static AlvoViolation MalformedSelectAlias() => new(
        ReservedQueryKeys.Select,
        "malformed-select-alias",
        "A projection entry is not a field name or an 'alias:field' pair.",
        "Write select=make or select=label:make; an alias is lower snake_case, starts with a letter, is at "
        + $"most 63 characters, and is none of {ReservedQueryKeys.AsList}.");

    /// <summary>
    /// The refusal for one response key claimed twice — by two different fields, or by an alias onto a name
    /// the framework owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two sources for one key</b> is a request with no correct answer, and answering with either would
    /// silently drop a field the caller asked for. A repeated <em>identical</em> entry is not this
    /// condition: it dedupes, as it always has, because a repeat claims nothing new.
    /// </para>
    /// <para>
    /// <b>An alias onto a framework-owned name</b> — <c>select=id:make</c>, <c>select=tenant_id:make</c> —
    /// is refused for a different reason, and the reason is worth stating correctly because an earlier
    /// draft of this remark got it wrong. It is <em>not</em> that two values would arrive under one key:
    /// <c>DataApiPage.Render</c> emits only the projection's own keys, so the port's real <c>id</c> is
    /// dropped and nothing collides. It is that the response would carry a key that reads as a framework
    /// column and is not one — the same outcome <see cref="MalformedSelectAlias"/>'s reserved-name check
    /// exists to prevent, and for the same reason: a key no descriptor is allowed to declare should not be
    /// reachable by renaming. Tested against <em>every</em> managed name rather than the ones this entity
    /// happens to carry, because a global entity has no <c>tenant_id</c> and a response key called
    /// <c>tenant_id</c> would still read as one.
    /// </para>
    /// <para>
    /// <b>What this deliberately does not refuse:</b> an alias onto another <em>declared</em> field's name
    /// — <c>select=year:make</c> answers <c>{"year": "skoda"}</c> where the published schema declares
    /// <c>year</c> an integer. That is inherent to PostgREST-style aliasing, which Alvo adopts rather than
    /// narrows here: the caller chose both halves, the value is one they may read, and refusing it would
    /// make the alias useless for the renaming it exists for. Recorded so the asymmetry with the managed
    /// names reads as a decision.
    /// </para>
    /// </remarks>
    internal static AlvoViolation CollidingProjectionKey() => new(
        ReservedQueryKeys.Select,
        "colliding-projection-key",
        "Two projected fields would answer under the same response key.",
        "Give each projected field its own key, and do not alias onto a framework-managed column's name.");

    /// <summary>The refusal for a projection naming more distinct keys than the caller can read fields.</summary>
    /// <remarks>
    /// <para>
    /// <b>The bound aliases make necessary.</b> Before them the projection was self-bounding: every entry
    /// resolved to a declared field and duplicates collapsed, so a response could never carry more keys than
    /// the entity has fields. An alias can name one column under arbitrarily many keys, leaving only the
    /// transport's URL limit in the way — the "a bound the caller controls" shape
    /// <see cref="AlvoFilter.MaxTerms"/> exists to close on the filter side.
    /// </para>
    /// <para>
    /// <b>Derived rather than chosen</b>, so it needs no judgement call, no configuration knob and no
    /// per-engine measurement: a response with more keys than the caller has readable fields is a
    /// duplication request, not a read. Charged per newly claimed <em>distinct</em> key, which is what keeps
    /// a repeated entry deduping instead of counting.
    /// </para>
    /// <para>
    /// <b>The caller's readable count, not the entity's declared one</b> — a deliberate change from the
    /// design's first shape, which said <c>entity.Fields.Count</c>. That number, published here in the fix
    /// suggestion, would have told a caller who hit the bound how many fields the entity declares, while an
    /// unprojected list already tells them how many they can read: the difference is exactly the number of
    /// fields hidden from them. An alias makes that cheap to ask for, because one readable field mints
    /// unlimited distinct keys. The readable count is both tighter and silent.
    /// </para>
    /// </remarks>
    /// <param name="maxKeys">How many fields this caller can read.</param>
    internal static AlvoViolation ProjectionTooWide(int maxKeys) => new(
        ReservedQueryKeys.Select,
        "projection-too-wide",
        "The projection names more keys than there are fields to read.",
        $"Name at most {maxKeys} distinct keys; aliasing one field under many keys returns the same value "
        + "repeatedly.");

    /// <summary>The refusal for a parameter sent more than once, which anchors one setting two ways.</summary>
    /// <param name="pointer">The parameter that was repeated.</param>
    internal static AlvoViolation RepeatedParameter(string pointer) => new(
        pointer,
        "repeated-parameter",
        "A reserved query parameter was sent more than once.",
        $"Send each of {ReservedQueryKeys.AsList} at most once; answering with one of them would silently "
        + "resolve an ambiguous request.");

    /// <summary>
    /// A field type as the descriptor spells it, so a fix suggestion names the type the author wrote rather
    /// than the CLR name of an enum member.
    /// </summary>
    private static string Spelled(FieldType type) =>
#pragma warning disable CA1308 // The descriptor's own spelling of a field type is lower-case by definition.
        type.ToString().ToLowerInvariant();
#pragma warning restore CA1308
}
