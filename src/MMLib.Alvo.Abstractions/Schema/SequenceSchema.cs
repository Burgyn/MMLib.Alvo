using System.Text.RegularExpressions;

namespace MMLib.Alvo.Schema;

/// <summary>
/// Describes a <b>sequence</b> field on the applied schema: a number the framework assigns on create, gapless and never
/// reused, counted per tenant and optionally per another field's value and per UTC calendar period.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Start"/> is resolved here while the descriptor's <c>start</c> is optional</b>, so every layer below reads
/// one number rather than re-deriving the default.
/// </para>
/// <para>
/// A count is identified by the entity, the field, the tenant, the <see cref="Per"/> value and the
/// <see cref="Period"/> of the create's UTC instant; the first create of a count receives <see cref="Start"/> and every
/// later one the previous number plus one. A rolled-back create returns its number.
/// </para>
/// </remarks>
public sealed partial record SequenceSchema
{
    /// <summary>Gets the field of the same entity whose value starts its own count, or <see langword="null"/>.</summary>
    public string? Per { get; init; }

    /// <summary>Gets the UTC calendar period after which the count restarts, or <see langword="null"/> when it never does.</summary>
    public SequenceResetPeriod? Period { get; init; }

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

    private static bool IsNumber(Match token) => token.Groups["token"].Value.StartsWith('n');
}

/// <summary>The UTC calendar period after which a <see cref="SequenceSchema"/> restarts its count.</summary>
/// <remarks>
/// A separate enum from the descriptor's <c>SequencePeriod</c>, for the reason <see cref="RollupOperation"/> is separate
/// from the descriptor's <c>RollupOp</c>: the applied schema is the artifact a storage driver reads and a
/// <c>schema_json</c> column persists, so it must not move when a descriptor-facing enum gains a member. Members are
/// therefore only ever <b>appended</b> — the applied schema round-trips through System.Text.Json's default numeric enum
/// representation, so reordering one would silently re-read every stored schema.
/// </remarks>
public enum SequenceResetPeriod
{
    /// <summary>Restart every UTC calendar year.</summary>
    Year,

    /// <summary>Restart every UTC calendar month.</summary>
    Month,

    /// <summary>Restart every UTC calendar day.</summary>
    Day,
}
