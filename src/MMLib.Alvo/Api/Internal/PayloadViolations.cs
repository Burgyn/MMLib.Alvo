using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Every refusal the write path can produce — the body reader's and the record validator's — composed in
/// one place, exactly as <see cref="QueryViolations"/> is for the query string.
/// </summary>
/// <remarks>
/// <para>
/// One catalogue rather than a message at each call site, for the reason <see cref="QueryViolations"/>
/// gives: a wording invented where it is raised is a wording nobody compares with its siblings. Here the
/// comparison matters twice — <see cref="UnknownField"/> must not read differently from the query parser's
/// equivalent, and no message may name a caller-supplied key or value.
/// </para>
/// <para>
/// <b>What may and may not appear in a message.</b> A field's <em>declared facets</em> are server-owned and
/// safe to name — a configured <c>maxLength</c>, a <c>scale</c>, an enum's declared values, a format's own
/// name — and naming them is the whole of §0 principle 4: an agent that is told the limit fixes the request
/// in one round trip. A caller-supplied key or value is not, ever: echoing it puts attacker-controlled
/// bytes into every log that records the response, and for a key it also answers "does this entity have a
/// field called X" one request at a time. The <see cref="AlvoViolation.Pointer"/> carries the location
/// instead, which is the caller's own text coming back only as a JSON Pointer they authored.
/// </para>
/// </remarks>
internal static class PayloadViolations
{
    /// <summary>
    /// The JSON Pointer for the request body as a whole. RFC 6901 §5 makes the empty string the pointer to
    /// the whole document, so a body-level refusal has a real pointer rather than a made-up sentinel.
    /// </summary>
    internal const string BodyPointer = "";

    /// <summary>The JSON Pointer (RFC 6901) to one top-level field of the request body.</summary>
    /// <remarks>
    /// The escaping is RFC 6901 §3's, and it is not decorative: a field named <c>a/b</c> would otherwise
    /// produce a pointer naming a nested member that does not exist, and a consumer resolving it would
    /// silently attribute the violation to the wrong place. A descriptor's field grammar does not admit
    /// either character today, so this is protection against the pointer becoming wrong later rather than
    /// against a name in circulation now.
    /// </remarks>
    /// <param name="field">The field name, as the payload spelled it.</param>
    internal static string PointerTo(string field) =>
        "/" + field.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// The field name a pointer produced by <see cref="PointerTo"/> names, or <see langword="null"/> for
    /// <see cref="BodyPointer"/> — a body-level refusal names no field.
    /// </summary>
    /// <remarks>
    /// The exact inverse of <see cref="PointerTo"/>, and beside it for that reason: the escaping and the
    /// unescaping are one decision, and a caller reading a pointer back with its own <c>[1..]</c> would
    /// silently return <c>a~1b</c> where the field is <c>a/b</c>. The order matters — <c>~1</c> before
    /// <c>~0</c>, per RFC 6901 §4 — or a field named <c>a~1b</c> would decode as <c>a/b</c>.
    /// </remarks>
    /// <param name="pointer">A pointer from a violation this catalogue produced.</param>
    internal static string? FieldOf(string pointer) => pointer.Length == 0
        ? null
        : pointer[1..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>The refusal for a body that is not a JSON object of field names to values.</summary>
    internal static AlvoViolation NotAnObject() => new(
        BodyPointer,
        "not-an-object",
        "The request body must be a JSON object mapping field names to values.",
        "Send {\"field\":value,…}. An array, a scalar or an absent body names no field to write.");

    /// <summary>The refusal for a body that is not well-formed JSON at all.</summary>
    internal static AlvoViolation MalformedJson() => new(
        BodyPointer,
        "malformed-json",
        "The request body is not well-formed JSON.",
        "Check for an unterminated string, a trailing comma, or a truncated body.");

    /// <summary>The refusal for a body past the configured byte bound.</summary>
    /// <param name="maxBytes">The configured maximum.</param>
    internal static AlvoViolation TooLarge(int maxBytes) => new(
        BodyPointer,
        "body-too-large",
        string.Create(
            CultureInfo.InvariantCulture,
            $"The request body is larger than {maxBytes} bytes, the configured maximum."),
        "Send a smaller body. A write payload is a flat map of the entity's declared fields.");

    /// <summary>The refusal for a body nested past the configured depth bound.</summary>
    /// <param name="maxDepth">The configured maximum.</param>
    internal static AlvoViolation TooDeep(int maxDepth) => new(
        BodyPointer,
        "body-too-deep",
        string.Create(
            CultureInfo.InvariantCulture,
            $"The request body nests deeper than {maxDepth} levels, the configured maximum."),
        "Flatten the body. Only a 'json' field's own value legitimately nests.");

    /// <summary>The refusal for a body carrying more property names, at any depth, than the bound allows.</summary>
    /// <param name="maxKeys">The configured maximum.</param>
    internal static AlvoViolation TooManyKeys(int maxKeys) => new(
        BodyPointer,
        "body-too-many-fields",
        string.Create(
            CultureInfo.InvariantCulture,
            $"The request body carries more than {maxKeys} fields, the configured maximum."),
        "Send only the fields you are changing; the bound counts property names at every depth.");

    /// <summary>The refusal for a body that uses one property name twice inside the same object.</summary>
    /// <remarks>
    /// <para>
    /// <b>Alvo's own wording, because the alternative was System.Text.Json's.</b> A duplicate name survives
    /// <c>JsonNode.Parse</c> — a <c>JsonObject</c>'s backing dictionary materialises lazily — and threw
    /// <c>ArgumentException("An item with the same key has already been added. Key: …")</c> at the first
    /// enumeration, which <see cref="ProblemResultFactory.GuardAsync"/> rendered as this same 422 with a .NET
    /// dictionary message as its <c>detail</c>. Worse, that message ends in the caller's own key, so it broke
    /// this catalogue's one rule about what a message may carry — and
    /// <see cref="ProblemResultFactory.WithoutArgumentDetail"/> stripped the <c>(Parameter '…')</c> marker the
    /// suite screens for, so nothing caught it.
    /// </para>
    /// <para>
    /// <b>Refused rather than resolved.</b> RFC 8259 §4 says names SHOULD be unique and leaves behaviour
    /// undefined when they are not, so first-wins and last-wins are both a guess about what the caller meant —
    /// and a guess that silently discards a value the caller sent is the silent-drop this API refuses
    /// everywhere else (see <see cref="ReadOnly"/>).
    /// </para>
    /// <para>
    /// <b>Body-level, so it names no key.</b> It is decided by the same forward-only scan that enforces the
    /// depth and key bounds, at every depth — so there is no single top-level field to point at, and the
    /// pointer is <see cref="BodyPointer"/> exactly as the other structural refusals' are.
    /// </para>
    /// </remarks>
    internal static AlvoViolation DuplicateField() => new(
        BodyPointer,
        "duplicate-field",
        "The request body uses the same property name twice inside one object.",
        "Send each property once. A repeated name has no defined meaning, so it is refused rather than "
        + "resolved to the first or the last value — at every depth, not only the top level.");

    /// <summary>The refusal for a key the entity does not declare.</summary>
    /// <remarks>
    /// <para>
    /// <b>The declared, non-hidden schema shape is public, and this wording is not trying to hide it.</b>
    /// Alvo maps route literals from the applied schema, so an undeclared entity already answers 404 where a
    /// declared one answers 403 — entity existence is disclosed before authorization, by design — and the
    /// generated OpenAPI document publishes the declared, non-hidden field list to anyone who can read it. A framework cannot
    /// both publish its schema shape and treat that shape as confidential. What is confidential is
    /// <em>data</em>.
    /// </para>
    /// <para>
    /// <b>The one carve-out — a <c>hidden</c> field's name — does not apply on this path.</b> <c>hidden</c>
    /// restricts reading, so a hidden field is legitimately writable and is simply accepted; there is
    /// nothing here to tell apart from an unknown name.
    /// </para>
    /// <para>
    /// <b>The pointer names the key; the message still does not.</b> A JSON Pointer is a <em>location in the
    /// request the caller authored</em> — it says "this key of yours is the problem", which they already knew
    /// they sent, and it makes no claim about which fields the entity has. Without it, a caller sending five
    /// keys learns only that one of them is wrong. The message is a different thing: it is prose that gets
    /// logged and re-rendered, so it stays free of caller-supplied text.
    /// </para>
    /// <para>
    /// Naming the key is also what stops this violation from being read as a statement about the
    /// <em>document</em>. Against <see cref="BodyPointer"/> it was exactly that: the reader inferred "the body
    /// did not bind" from an empty pointer, so a single unrecognised key discarded every other violation in
    /// the response — see <c>JsonPayloadReader.Payload</c>.
    /// </para>
    /// </remarks>
    /// <param name="key">The key the payload carried, as the caller spelled it.</param>
    internal static AlvoViolation UnknownField(string key) => new(
        PointerTo(key),
        "unknown-field",
        "The request body names a field that is not writable on this entity. Send only the fields the "
        + "entity declares.",
        "Remove the field, or check its spelling against the entity's declared fields.");

    /// <summary>
    /// The refusal for a value the field's declared type cannot hold. Names the type — which the caller
    /// already knows the field has — never the value, which is theirs.
    /// </summary>
    /// <param name="field">The declared field the value was supplied for.</param>
    internal static AlvoViolation UnrepresentableValue(FieldSchema field) => new(
        PointerTo(field.Name),
        "invalid-value",
        $"A value cannot be held by the type of the field it was supplied for ('{Spelled(field.Type)}').",
        $"Supply a JSON value a '{Spelled(field.Type)}' field accepts — a quoted string for a text, uuid, "
        + "date or enum field, an unquoted number for an integer or decimal, true/false for a boolean.");

    /// <summary>The refusal for a required field the payload omits or nulls.</summary>
    internal static AlvoViolation Required(FieldSchema field) => new(
        PointerTo(field.Name),
        "required",
        "A field the entity declares required is missing or null.",
        "Supply a value for it. A create must carry every required field; a partial update may omit any "
        + "field it is not changing, but may not null a required one.");

    /// <summary>The refusal for a string longer than the field's declared <c>maxLength</c>.</summary>
    /// <param name="field">The declared field, whose own bound the message names.</param>
    internal static AlvoViolation MaxLength(FieldSchema field) => new(
        PointerTo(field.Name),
        "max-length",
        string.Create(
            CultureInfo.InvariantCulture,
            $"A value is longer than the {field.MaxLength} characters the field declares."),
        string.Create(
            CultureInfo.InvariantCulture,
            $"Shorten it to at most {field.MaxLength} characters. {MaxLengthUnitNote}"));

    /// <summary>
    /// The unit <c>maxLength</c> is measured in, said in the refusal itself.
    /// </summary>
    /// <remarks>
    /// "Characters" is the word the descriptor uses and it is ambiguous enough to have produced a bug
    /// (#123), so the message that asks a caller to shorten a value says which unit it is counting. Code
    /// points is the unit PostgreSQL's <c>varchar(n)</c> and JSON Schema's own <c>maxLength</c> keyword
    /// both use, so on the shipped drivers the refusal, the column and the published document all mean the
    /// same number; a dialect whose column counts otherwise owes its own answer (#175).
    /// </remarks>
    private const string MaxLengthUnitNote =
        "Length is counted in Unicode code points rather than UTF-16 units, so a character outside the "
        + "Basic Multilingual Plane counts once and not twice. The bound is the column's own width, so a "
        + "longer value cannot be stored.";

    /// <summary>The refusal for a decimal carrying more fractional digits than the field's <c>scale</c>.</summary>
    /// <param name="field">The declared field, whose own scale the message names.</param>
    internal static AlvoViolation Scale(FieldSchema field) => new(
        PointerTo(field.Name),
        "scale",
        string.Create(
            CultureInfo.InvariantCulture,
            $"A value carries more fractional digits than the {field.Scale} the field declares."),
        string.Create(
            CultureInfo.InvariantCulture,
            $"Round it to {field.Scale} decimal places before sending it. Refused rather than rounded here, because a silently rounded amount is a number the caller never agreed to."));

    /// <summary>The refusal for a decimal needing more integral digits than the field's <c>precision</c> leaves.</summary>
    /// <param name="field">The declared field, whose own precision and scale the message names.</param>
    internal static AlvoViolation Precision(FieldSchema field) => new(
        PointerTo(field.Name),
        "precision",
        string.Create(
            CultureInfo.InvariantCulture,
            $"A value needs more digits than the field's declared precision of {field.Precision} allows."),
        string.Create(
            CultureInfo.InvariantCulture,
            $"The field stores {field.Precision} digits in total with {field.Scale} of them fractional, so "
            + $"at most {field.Precision - field.Scale} digits may precede the decimal point."));

    /// <summary>
    /// The refusal for a value outside an enum's declared values, <b>listing them</b> — they are the
    /// descriptor author's own, not the caller's, and an agent that is told the set fixes the request once.
    /// </summary>
    /// <param name="field">The declared enum field.</param>
    internal static AlvoViolation EnumValue(FieldSchema field) => new(
        PointerTo(field.Name),
        "enum-value",
        "A value is not one of the values the enum field declares.",
        $"Use one of: {string.Join(", ", field.EnumValues ?? [])}.");

    /// <summary>
    /// The refusal for a value failing its field's declared format, <b>naming the format</b> rather than
    /// its pattern.
    /// </summary>
    /// <remarks>
    /// The name is what the descriptor author chose and what the generated OpenAPI document publishes, so
    /// it is the term the caller can look up. The pattern is deliberately not echoed: a regular expression
    /// is a poor fix suggestion, and a named format's pattern is the descriptor's business rather than a
    /// per-request disclosure.
    /// </remarks>
    /// <param name="field">The declared field, whose format name the message carries.</param>
    internal static AlvoViolation Format(FieldSchema field) => new(
        PointerTo(field.Name),
        "format",
        $"A value does not match the '{field.Format}' format the field declares.",
        $"Send a value in the '{field.Format}' format. The generated OpenAPI document carries its pattern "
        + "and description.");

    /// <summary>
    /// The refusal for a format check that <b>did not finish</b>: the value was never judged, so the caller is
    /// not told it was wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own code, because it is not the caller's mistake.</b> <c>FormatCatalog.MatchTimeout</c> bounds how
    /// long one value may be matched, and exceeding it means the pattern could not be evaluated — which happens
    /// for a catastrophic pattern driven by a hostile value <em>and</em> for a perfectly ordinary value on a
    /// machine loaded enough to lose the bound to scheduling. Rendering both as <c>format</c> did the second
    /// caller real harm: a valid <c>email</c> was refused as malformed once in nine suite runs, and the advice
    /// was to correct a value that was already correct.
    /// </para>
    /// <para>
    /// <b>Still a refusal, and that half is unchanged.</b> "I could not decide" must not become "it passed", and
    /// it must not become a 500 either. The fix suggestion is therefore a retry rather than an edit — the one
    /// action that can succeed when nothing about the value was wrong.
    /// </para>
    /// <para>
    /// The pattern is not echoed, for the reason <see cref="Format"/> does not echo it.
    /// </para>
    /// </remarks>
    /// <param name="field">The declared field whose format could not be evaluated.</param>
    internal static AlvoViolation FormatNotEvaluated(FieldSchema field) => new(
        PointerTo(field.Name),
        "format-not-evaluated",
        $"The '{field.Format}' format the field declares could not be evaluated in time, so this value was "
        + "neither accepted nor found invalid.",
        "Retry the request. If it keeps happening, the declared format's pattern is too expensive to evaluate "
        + "for values of this length and the descriptor's author has to simplify it — the value itself may be "
        + "perfectly valid.");

    /// <summary>
    /// The refusal for a write to a field the caller's policy marks read-only — <b>422, not a silent
    /// drop</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silently ignoring the field is the one answer that must not happen: a caller who sent a value and
    /// received a 200 believes it was stored. The design says it plainly — "a write to a <c>readOnly</c>
    /// field is rejected with 422 rather than silently ignored — for an agent, a silent drop is worse than
    /// an error".
    /// </para>
    /// <para>
    /// <b>The port raises 403 for the same write, and both are correct.</b> Validation runs before the
    /// port, so an HTTP caller gets the actionable 422 with a fix; the port's
    /// <c>AlvoAuthorizationException</c> remains the backstop for a caller reaching <c>IAlvoData</c>
    /// directly. Naming both here is what stops a later refactor swapping them without noticing.
    /// </para>
    /// <para>
    /// This is a <em>read-only</em> field, not a hidden one. <c>hidden</c> restricts reading and
    /// <c>readOnly</c> restricts writing, so a hidden field is legitimately writable and gets no violation
    /// at all — see <c>ValidationTests</c>' fact for the write that must stay accepted.
    /// </para>
    /// </remarks>
    /// <param name="field">The declared field the caller's policy froze.</param>
    internal static AlvoViolation ReadOnly(FieldSchema field) => new(
        PointerTo(field.Name),
        "read-only-field",
        "The request writes a field this caller may read but not change.",
        "Remove the field from the request body. It is read-only for your roles, so no value you send can "
        + "be stored — which is why this is refused rather than ignored.");

    /// <summary>
    /// The refusal for a <b>create</b> whose caller cannot satisfy it: the entity declares the field
    /// required, and this caller's own <c>readOnly</c> mask froze it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A third answer, because the other two are both dishonest here.</b> Telling this caller
    /// <c>required</c> sends them to supply a field no value of theirs can be stored in; telling them
    /// <c>read-only-field</c> names a write they did not attempt. The create is impossible <em>for them</em>,
    /// and that is the only sentence that is true of the request.
    /// </para>
    /// <para>
    /// <b>Reachable only through an expression-valued <c>readOnly</c>.</b> A field declaring the static pair
    /// <c>required: true</c> + <c>readOnly: true</c> is refused when the descriptor is applied
    /// (<c>DescriptorValidator</c>), because then <em>no</em> caller could ever create the row and the author
    /// can still fix it. What survives to here is the per-caller case: satisfiable for one role, impossible
    /// for another, which no apply-time check can decide.
    /// </para>
    /// <para>
    /// <b>Refused rather than let through with the field absent</b>, which is the shape #124 first proposed.
    /// A required field is a <c>NOT NULL</c> column, so omitting it moves the failure into the engine and
    /// turns an actionable 422 into a 500 — a worse answer to the same impossible request.
    /// </para>
    /// </remarks>
    /// <param name="field">The declared field this caller's policy froze on a create.</param>
    internal static AlvoViolation ReadOnlyRequired(FieldSchema field) => new(
        PointerTo(field.Name),
        "read-only-required-field",
        "The entity declares this field required, and it is read-only for this caller — so no create of "
        + "this entity can succeed with these roles.",
        "Ask for a role that may write the field, or have the descriptor give it a value the caller does "
        + "not supply — a 'computed' expression, or a 'default'. Omitting it is not an option: the field "
        + "is NOT NULL.");

    /// <summary>
    /// The refusal for a reference naming a row the caller cannot resolve — because it does not exist,
    /// <b>or</b> because their own policy does not let them see it. One violation for both.
    /// </summary>
    /// <remarks>
    /// The two must be indistinguishable, and it is the same rule <c>IAlvoData</c> states for
    /// <see cref="Data.IAlvoData.GetAsync"/>: a row that exists but that the caller's <c>USING</c>
    /// predicate excludes must read exactly like a row that was never there. Told apart, a create endpoint
    /// becomes a cross-tenant existence oracle wearing a 201/422 shape — one request per candidate id,
    /// answered without ever reading a byte of the row.
    /// </remarks>
    /// <param name="field">The declared reference field.</param>
    internal static AlvoViolation UnresolvedReference(FieldSchema field) => new(
        PointerTo(field.Name),
        "unresolved-reference",
        "A reference names a row that could not be resolved.",
        "Reference a row of the target entity that exists and that you can read. A row you cannot read is "
        + "indistinguishable from one that does not exist, deliberately.");

    /// <summary>
    /// A field type as the descriptor spells it, so a message names the type the author wrote rather than
    /// the CLR name of an enum member.
    /// </summary>
    private static string Spelled(FieldType type) =>
#pragma warning disable CA1308 // The descriptor's own spelling of a field type is lower-case by definition.
        type.ToString().ToLowerInvariant();
#pragma warning restore CA1308
}
