using MMLib.Alvo.Internal;
using MMLib.Alvo.Schema;

using System.Globalization;
using System.Text;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// A parsed <c>{{…}}</c> template: alternating literal and placeholder segments, rendered against one
/// <see cref="AlvoEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only transform this build honours.</b> There is no mature .NET JSONata implementation, and
/// a hand-rolled subset would accept the part it implements and silently produce a different payload for the
/// rest — a webhook delivered with a wrong body is indistinguishable from a consumer bug. A raw JSONata
/// expression is therefore refused by name (see <see cref="JsonataSlot"/>) rather than partially evaluated.
/// </para>
/// <para>
/// <b>Validated at apply, rendered at delivery.</b> <see cref="TemplatePlaceholder.TryResolve"/> is the apply-time
/// half and is the only half that can produce a fix suggestion, because at delivery there is nobody to
/// report a refusal to. An author learns that <c>{{new.titel}}</c> is a typo when the descriptor is applied,
/// not when a webhook fires at 3am.
/// </para>
/// <para>
/// <b>A rendered value is never re-scanned.</b> Rendering walks the segments once and appends; it never
/// re-parses its own output, or a row whose own text contained <c>{{…}}</c> would inject a placeholder that
/// no author wrote and no apply-time validation saw.
/// </para>
/// </remarks>
/// <param name="Segments">The template's segments, in source order.</param>
internal sealed record AlvoTemplate(IReadOnlyList<AlvoTemplateSegment> Segments)
{
    /// <summary>The one spelling of a placeholder's opening delimiter; <see cref="JsonataSlot"/> reads it too.</summary>
    internal const string PlaceholderOpen = "{{";

    /// <summary>The one spelling of a placeholder's closing delimiter.</summary>
    internal const string PlaceholderClose = "}}";

    /// <summary>Parses a template, refusing a malformed placeholder rather than shipping it as literal text.</summary>
    /// <param name="source">The template's source text.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> opens a placeholder it does not close, nests one, or leaves one empty.
    /// </exception>
    /// <remarks>
    /// A lone <c>{</c> or <c>}</c> outside a placeholder is literal text, because a template body
    /// legitimately contains one — the strict no-bare-brace rule belongs to <see cref="JsonataSlot"/>, where
    /// a brace is evidence of JSONata, and not here.
    /// </remarks>
    internal static AlvoTemplate Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AlvoTemplate([.. Scan(source)]);
    }

    /// <summary>The inner text of each placeholder, trimmed, in source order.</summary>
    internal IReadOnlyList<string> Placeholders =>
        [.. Segments.Where(segment => segment.IsPlaceholder).Select(segment => segment.Text)];

    /// <summary>Renders this template against one event.</summary>
    /// <param name="event">The event whose row images and attributes the placeholders read.</param>
    internal string Render(AlvoEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var rendered = new StringBuilder();
        foreach (var segment in Segments)
        {
            rendered.Append(RenderSegment(segment, @event));
        }

        return rendered.ToString();
    }

    private static string RenderSegment(AlvoTemplateSegment segment, AlvoEvent @event) =>
        segment.IsPlaceholder
            ? Format(TemplatePlaceholder.ValueOf(segment.Text, @event))
            : segment.Text;

    private static IEnumerable<AlvoTemplateSegment> Scan(string source)
    {
        var cursor = 0;
        while (cursor < source.Length)
        {
            var open = source.IndexOf(PlaceholderOpen, cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                yield return Literal(source[cursor..]);
                break;
            }

            if (open > cursor)
            {
                yield return Literal(source[cursor..open]);
            }

            var inner = InnerTextAt(source, open);
            yield return new AlvoTemplateSegment(inner.Trim(), IsPlaceholder: true);
            cursor = open + PlaceholderOpen.Length + inner.Length + PlaceholderClose.Length;
        }
    }

    private static AlvoTemplateSegment Literal(string text) => new(text, IsPlaceholder: false);

    private static string InnerTextAt(string source, int open)
    {
        var from = open + PlaceholderOpen.Length;
        var close = source.IndexOf(PlaceholderClose, from, StringComparison.Ordinal);
        var inner = close < 0 ? null : source[from..close];

        return IsWellFormed(inner) ? inner! : throw Malformed(source);
    }

    private static bool IsWellFormed(string? inner) =>
        inner is not null && !inner.AsSpan().IsWhiteSpace() && !inner.AsSpan().ContainsAny('{', '}');

    private static ArgumentException Malformed(string source) => new(
        $"'{source}' is not a well-formed template: every {PlaceholderOpen} must be closed by "
        + $"{PlaceholderClose}, and a placeholder's inner text is non-empty and carries no brace. An "
        + "unclosed placeholder would otherwise be delivered to the endpoint as literal text.",
        nameof(source));

    /// <summary>
    /// One value's text form. A timestamp is the framework's own round-trip form and a boolean is the JSON
    /// spelling, so a template can never introduce a second spelling of a value the rest of the framework
    /// already writes one way.
    /// </summary>
    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>One piece of a parsed template: literal text, or a placeholder's trimmed inner text.</summary>
/// <param name="Text">The literal text, or the placeholder's inner text with no delimiters.</param>
/// <param name="IsPlaceholder">Whether <paramref name="Text"/> names a placeholder rather than being literal text.</param>
internal readonly record struct AlvoTemplateSegment(string Text, bool IsPlaceholder);

/// <summary>
/// What a placeholder may name, and what it resolves to: the apply-time check that refuses an unresolvable
/// placeholder with a fix suggestion, and the delivery-time lookup that reads one value off an event.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Roots"/> is the one authority</b> every refusal message iterates, so a root added later
/// cannot be missing from the message that lists them.
/// </para>
/// <para>
/// <b>Two names the design's own table promises and this build refuses.</b> The addendum lists
/// <c>@user.id</c> and <c>@tenant.id</c> as "the provenance the envelope carries", and the envelope carries
/// only the first: <see cref="AlvoEvent"/> has <c>authid</c> and no tenant attribute at all. So
/// <c>@tenant.id</c> is refused <em>by name</em> — it is a real Alvo CEL context reference, so "unknown
/// root" would misdescribe why it fails. Resolving it from the row's own <c>tenant_id</c> was rejected:
/// <c>@tenant.id</c> asks which tenant the <em>caller</em> was in, and a write made as
/// <c>AlvoContext.System</c> has no tenant while the row it wrote has one — answering a different question
/// with a plausible value is the defect this refusal exists to prevent. <c>@user.roles</c> is refused for
/// the same reason and one more: the envelope carries authentication, never authorization.
/// </para>
/// <para>
/// <b>Where the two halves differ.</b> <see cref="TryResolve"/> sees the schema, so it can tell an
/// undeclared field from a null one; <see cref="ValueOf"/> sees only a row, so it cannot, and a field root
/// resolves to whatever the row holds. That asymmetry is why the field check is apply-time work and is not
/// repeated at delivery.
/// </para>
/// </remarks>
internal static class TemplatePlaceholder
{
    /// <summary>The placeholder roots that resolve, and the list every refusal message names.</summary>
    internal static IReadOnlyList<string> Roots { get; } = [NewRoot, OldRoot, EventRoot, UserRoot];

    /// <summary>
    /// Whether <paramref name="placeholder"/> resolves against <paramref name="entity"/>, and why not when
    /// it does not.
    /// </summary>
    /// <param name="placeholder">A placeholder's inner text, such as <c>new.title</c>.</param>
    /// <param name="entity">The entity the template's events are about, as the applied schema declares it.</param>
    /// <param name="refusal">The refusal, naming the mistake and a fix; <see langword="null"/> on success.</param>
    internal static bool TryResolve(string placeholder, EntitySchema entity, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        ArgumentNullException.ThrowIfNull(entity);

        refusal = TrySplit(placeholder, out var root, out var member)
            ? RefusalFor(placeholder, root, member, entity)
            : NotARootAndMember(placeholder);

        return refusal is null;
    }

    /// <summary>One placeholder's value, read off an event at delivery time.</summary>
    /// <param name="placeholder">A placeholder's inner text, such as <c>new.title</c>.</param>
    /// <param name="event">The event the value is read from.</param>
    /// <exception cref="InvalidOperationException">
    /// The placeholder names a root or an event attribute that does not exist — which apply-time validation
    /// refuses, so reaching it here is an invariant of this path rather than an author's mistake. It throws
    /// rather than rendering an empty string, because a silently empty recipient reads as a broken mail
    /// server.
    /// </exception>
    internal static object? ValueOf(string placeholder, AlvoEvent @event)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        ArgumentNullException.ThrowIfNull(@event);

        if (!TrySplit(placeholder, out var root, out var member))
        {
            throw Unresolvable(placeholder);
        }

        return root switch
        {
            NewRoot => @event.Data.Record?[member],
            OldRoot => @event.Data.OldRecord?[member],
            EventRoot => EventAttribute(placeholder, member, @event),
            UserRoot when member == IdMember => @event.AuthId,
            _ => throw Unresolvable(placeholder),
        };
    }

    private const string NewRoot = "new";
    private const string OldRoot = "old";
    private const string EventRoot = "event";
    private const string UserRoot = "@user";
    private const string TenantRoot = "@tenant";
    private const char RootSeparator = '.';

    private const string IdMember = "id";
    private const string TypeMember = "type";
    private const string TimeMember = "time";
    private const string SubjectMember = "subject";

    private static IReadOnlyList<string> EventMembers { get; } = [IdMember, TypeMember, TimeMember, SubjectMember];

    private static string AvailableRoots => $"Available roots: {string.Join(", ", Roots)}.";

    private static bool TrySplit(string placeholder, out string root, out string member)
    {
        var separator = placeholder.IndexOf(RootSeparator);
        root = separator > 0 ? placeholder[..separator] : string.Empty;
        member = separator > 0 ? placeholder[(separator + 1)..] : string.Empty;

        return member.Length > 0;
    }

    private static string? RefusalFor(string placeholder, string root, string member, EntitySchema entity) =>
        root switch
        {
            NewRoot or OldRoot => UnknownField(placeholder, member, entity),
            EventRoot => UnknownEventAttribute(placeholder, member),
            UserRoot => UnknownUserMember(placeholder, member),
            TenantRoot => TenantIsNotOnTheEnvelope(placeholder),
            _ => UnknownRoot(placeholder),
        };

    private static object? EventAttribute(string placeholder, string member, AlvoEvent @event) => member switch
    {
        IdMember => @event.Id,
        TypeMember => @event.Type,
        TimeMember => @event.Time,
        SubjectMember => @event.Subject,
        _ => throw Unresolvable(placeholder),
    };

    private static string? UnknownField(string placeholder, string field, EntitySchema entity)
    {
        var declared = DeclaredNames(entity);

        return declared.Contains(field)
            ? null
            : $"{Quoted(placeholder)} names '{field}', which entity '{entity.Name}' does not declare. "
                + FieldSuggestion(field, declared);
    }

    /// <summary>
    /// The declared fields <em>plus</em> the columns the framework manages for this entity's traits, because
    /// an author writing <c>{{new.created_at}}</c> has named a column that really exists — and asking
    /// <see cref="AlvoManagedColumns"/> rather than carrying a name list is what keeps this in step with the
    /// mapper that injects them.
    /// </summary>
    private static HashSet<string> DeclaredNames(EntitySchema entity)
    {
        var names = new HashSet<string>(entity.Fields.Select(field => field.Name), StringComparer.Ordinal);
        names.UnionWith(AlvoManagedColumns.For(entity));

        return names;
    }

    private static string FieldSuggestion(string field, HashSet<string> declared)
    {
        var closest = NameSuggestion.Closest(field, declared);

        return closest is not null
            ? $"Did you mean '{closest}'?"
            : $"Known fields: {string.Join(", ", declared.Order(StringComparer.Ordinal))}.";
    }

    private static string? UnknownEventAttribute(string placeholder, string member) =>
        EventMembers.Contains(member)
            ? null
            : $"{Quoted(placeholder)} names no attribute of the event itself. The attributes a template can "
                + $"read are: {string.Join(", ", EventMembers.Select(EventPlaceholder))}.";

    private static string EventPlaceholder(string member) => $"{EventRoot}{RootSeparator}{member}";

    private static string? UnknownUserMember(string placeholder, string member) =>
        member == IdMember
            ? null
            : $"{Quoted(placeholder)} cannot be resolved: an event carries only the id of the credential "
                + $"that acted, so '{UserRoot}{RootSeparator}{IdMember}' is the one '{UserRoot}' member a "
                + "template can read. For a recipient, use a field on the record such as "
                + $"{Quoted($"{NewRoot}{RootSeparator}owner_email")}; an identity claim Alvo does not yet "
                + "carry is tracked in issue #37.";

    private static string TenantIsNotOnTheEnvelope(string placeholder) =>
        $"{Quoted(placeholder)} cannot be resolved: the event envelope carries no tenant attribute, so "
        + "nothing at delivery time knows which tenant the caller was in. On a tenant-scoped entity, use the "
        + $"row's own {Quoted($"{NewRoot}{RootSeparator}{AlvoManagedColumns.TenantId}")} instead — it answers "
        + "which tenant the row belongs to, which is a different question and the only one the envelope can "
        + "answer.";

    private static string UnknownRoot(string placeholder) =>
        $"{Quoted(placeholder)} names no root Alvo can resolve. {AvailableRoots}";

    private static string NotARootAndMember(string placeholder) =>
        $"{Quoted(placeholder)} is not a placeholder Alvo can resolve: a placeholder is a root and a member, "
        + $"such as {Quoted($"{NewRoot}{RootSeparator}title")}. {AvailableRoots}";

    private static string Quoted(string placeholder) =>
        $"'{AlvoTemplate.PlaceholderOpen}{placeholder}{AlvoTemplate.PlaceholderClose}'";

    private static InvalidOperationException Unresolvable(string placeholder) => new(
        $"{Quoted(placeholder)} cannot be resolved at delivery time. A template is validated when the "
        + "descriptor is applied, so reaching this point means a template was rendered that apply-time "
        + "validation would have refused — an invariant of this path rather than an author's mistake. It "
        + "throws rather than rendering an empty string, because a silently empty recipient or body is the "
        + "misattribution that validation exists to prevent.");
}
