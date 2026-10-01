using System.Text.RegularExpressions;

namespace MMLib.Alvo.Secrets;

/// <summary>
/// The name one secret is stored and fetched under.
/// </summary>
/// <remarks>
/// <para>
/// <b>A validated type rather than a string</b>, for the reason <c>AlvoOptions.SchemaPrefix</c> is one: the
/// name is interpolated into a configuration key on the way in and used as a primary key on the way out, so
/// at both ends it has to be a validated identifier and never caller-supplied data.
/// </para>
/// <para>
/// <b>A class, not a struct.</b> A <c>readonly record struct</c> has a <c>default</c> instance whose value is
/// <see langword="null"/> — a name nothing validated, reachable without going through <see cref="Parse"/>.
/// There is no such instance of this type.
/// </para>
/// <para>
/// No implicit conversion either way. The point of the type is that a caller has to say where a name came
/// from, and an implicit conversion from <see cref="string"/> is exactly the step that would stop being said.
/// </para>
/// </remarks>
public sealed partial record SecretName
{
    /// <summary>
    /// What a name may be: lower case, starting with a letter, and at most 64 characters of letters,
    /// digits, dots, dashes and underscores.
    /// </summary>
    /// <remarks>
    /// Dots and dashes are admitted because every secret store worth swapping in uses one or the other as its
    /// own separator — Key Vault takes dashes, a mounted file takes dots — and a name Alvo accepts but the
    /// store cannot hold would be a refusal nobody could act on.
    /// </remarks>
    private const string Pattern = "^[a-z][a-z0-9._-]{0,63}$";

    private SecretName(string value) => Value = value;

    /// <summary>Gets the name, exactly as it was parsed.</summary>
    public string Value { get; }

    /// <summary>
    /// Reads a name, or reports that the candidate is not one.
    /// </summary>
    /// <param name="candidate">The text to read.</param>
    /// <param name="name">The name, when the candidate is one.</param>
    /// <returns><see langword="true"/> when <paramref name="candidate"/> is a name.</returns>
    public static bool TryParse(string? candidate, out SecretName? name)
    {
        name = candidate is not null && Allowed().IsMatch(candidate) ? new SecretName(candidate) : null;

        return name is not null;
    }

    /// <summary>
    /// Reads a name, or refuses by name.
    /// </summary>
    /// <param name="candidate">The text to read.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentException"><paramref name="candidate"/> is not a name.</exception>
    public static SecretName Parse(string candidate) =>
        TryParse(candidate, out var name)
            ? name!
            : throw new ArgumentException(
                $"'{candidate}' is not a secret name. A name matches {Pattern} — lower case, starting with a "
                + "letter, and at most 64 characters of letters, digits, dots, dashes and underscores.",
                nameof(candidate));

    /// <inheritdoc/>
    public override string ToString() => Value;

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Allowed();
}
