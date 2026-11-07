using System.Text.RegularExpressions;

namespace MMLib.Alvo.Schema;

/// <summary>
/// Describes a <b>sequence</b> field on the applied schema: a number the framework assigns on create, gapless and never
/// reused, counted per tenant and optionally per another field's value and per calendar period in <see cref="TimeZone"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Start"/> is resolved here while the descriptor's <c>start</c> is optional</b>, so every layer below reads
/// one number rather than re-deriving the default.
/// </para>
/// <para>
/// A count is identified by the entity, the field, the tenant, the <see cref="Per"/> value and the
/// <see cref="Period"/> of the create's instant read in <see cref="TimeZone"/>; the first create of a count receives <see cref="Start"/> and every
/// later one the previous number plus one. A rolled-back create returns its number.
/// </para>
/// </remarks>
public sealed partial record SequenceSchema
{
    /// <summary>Gets the field of the same entity whose value starts its own count, or <see langword="null"/>.</summary>
    public string? Per { get; init; }

    /// <summary>Gets the calendar period after which the count restarts, or <see langword="null"/> when it never does.</summary>
    public SequenceResetPeriod? Period { get; init; }

    /// <summary>
    /// Gets the IANA time zone whose calendar date names the <see cref="Period"/> and fills the date tokens of
    /// <see cref="Format"/>, or <see langword="null"/> for UTC.
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>Gets the first number of every count.</summary>
    public long Start { get; init; } = 1;

    /// <summary>
    /// Gets the text a <see cref="FieldType.String"/> field stores — exactly one <c>{n}</c> or <c>{n:width}</c>, plus any
    /// of <c>{yyyy}</c>, <c>{yy}</c>, <c>{MM}</c>, <c>{dd}</c> — or <see langword="null"/> for an integer field.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>Matches one token of a <see cref="Format"/>; its <c>token</c> group names it, <c>width</c> pads a number.</summary>
    [GeneratedRegex(@"\{(?<token>n(?::(?<width>1[0-8]|[1-9]))?|yyyy|yy|MM|dd)\}", RegexOptions.CultureInvariant)]
    internal static partial Regex FormatToken();

    /// <summary>
    /// Whether <paramref name="format"/> is a well-formed <see cref="Format"/>: every brace belongs to a known token, and
    /// exactly one token is a number.
    /// </summary>
    /// <param name="format">The declared format.</param>
    internal static bool IsValidFormat(string format)
    {
        var tokens = FormatToken().Matches(format);
        var bare = FormatToken().Replace(format, string.Empty);

        return bare.IndexOfAny(['{', '}']) < 0 && tokens.Count(IsNumber) == 1;
    }

    /// <summary>
    /// Whether <paramref name="timeZone"/> is an IANA zone id this machine resolves — on Linux and macOS from the tz
    /// database, on Windows through ICU — so a declaration never depends on the zone ids of one operating system.
    /// </summary>
    /// <remarks>
    /// A Windows id resolves on every platform too, and is refused here because it does not name the same zone on a
    /// machine without ICU's mapping; <c>UTC</c> is accepted by name, since Windows reports its own <c>UTC</c> zone as
    /// a Windows id.
    /// </remarks>
    /// <param name="timeZone">The declared zone.</param>
    internal static bool IsKnownTimeZone(string timeZone) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone)
        && (zone.HasIanaId || string.Equals(timeZone, "UTC", StringComparison.Ordinal));

    private static bool IsNumber(Match token) => token.Groups["token"].Value.StartsWith('n');
}

/// <summary>The calendar period after which a <see cref="SequenceSchema"/> restarts its count, read in its <see cref="SequenceSchema.TimeZone"/>.</summary>
/// <remarks>
/// A separate enum from the descriptor's <c>SequencePeriod</c>, for the reason <see cref="RollupOperation"/> is separate
/// from the descriptor's <c>RollupOp</c>: the applied schema is the artifact a storage driver reads and a
/// <c>schema_json</c> column persists, so it must not move when a descriptor-facing enum gains a member. Members are
/// therefore only ever <b>appended</b> — the applied schema round-trips through System.Text.Json's default numeric enum
/// representation, so reordering one would silently re-read every stored schema.
/// </remarks>
public enum SequenceResetPeriod
{
    /// <summary>Restart every calendar year.</summary>
    Year,

    /// <summary>Restart every calendar month.</summary>
    Month,

    /// <summary>Restart every calendar day.</summary>
    Day,
}
