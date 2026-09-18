using Microsoft.Extensions.Options;
using System.Net.Mail;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Refuses a misconfigured bootstrap administrator at startup, by name and with the fix —
/// <c>extensibility.md</c> rule 5 for <see cref="AlvoIdentityOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered by the package with <c>ValidateOnStart</c>, so both distributions are covered.</b>
/// The standalone host validates its own options, but an embedded host calls <c>AddAlvoIdentity</c>
/// directly and reaches none of that — a check that lived only in the host would leave the
/// distribution most likely to be misconfigured by hand with no check at all.
/// </para>
/// <para>
/// <b>What it deliberately does not check: the secret itself.</b> Reading the mounted file to judge
/// its contents would put a credential in this type's reach, and in a failure message, for a check
/// the password policy already makes at seeding time. Existence is the operator's mistake; strength
/// is the policy's business.
/// </para>
/// </remarks>
internal sealed class AlvoIdentityOptionsValidation : IValidateOptions<AlvoIdentityOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string[] failures = [.. Failures(options)];

        return failures.Length is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Every refusal this configuration has earned, rather than only the first.
    /// </summary>
    /// <remarks>
    /// A container with two things wrong is one restart per fix if only the first is reported, and an
    /// operator reading a crash loop cannot tell a second failure from the same failure again.
    /// </remarks>
    /// <param name="options">The bound options.</param>
    /// <returns>The refusals, in the order an operator would fix them.</returns>
    private static IEnumerable<string> Failures(AlvoIdentityOptions options)
    {
        var email = Trimmed(options.BootstrapEmail);
        var passwordFile = Trimmed(options.BootstrapPasswordFile);

        if (email is null && passwordFile is null)
        {
            yield break;
        }

        if (Pairing(email, passwordFile) is { } pairing)
        {
            yield return pairing;
        }

        if (email is not null && !IsSignInAddress(email))
        {
            yield return AlvoIdentityConfiguration.ImplausibleEmail(email);
        }

        if (passwordFile is not null && !File.Exists(passwordFile))
        {
            yield return AlvoIdentityConfiguration.NoPasswordFileAt(passwordFile);
        }
    }

    /// <summary>Whether the two halves of one credential were both configured.</summary>
    /// <param name="email">The configured address, already trimmed.</param>
    /// <param name="passwordFile">The configured path, already trimmed.</param>
    /// <returns>The refusal, or <see langword="null"/> when both halves are present.</returns>
    private static string? Pairing(string? email, string? passwordFile) =>
        passwordFile is null ? AlvoIdentityConfiguration.NoPasswordFile(email!)
            : email is null ? AlvoIdentityConfiguration.NoEmail(passwordFile)
            : null;

    /// <summary>
    /// Whether a configured value is an address someone could sign in with.
    /// </summary>
    /// <remarks>
    /// The round-trip against <see cref="MailAddress.Address"/> is what refuses the display-name form
    /// <c>Eva &lt;eva@example.com&gt;</c>, which parses happily and is not a sign-in address — the
    /// account would be seeded under a name nobody would ever type.
    /// </remarks>
    /// <param name="email">The configured address, already trimmed and non-empty.</param>
    /// <returns><see langword="true"/> when the value is a bare address.</returns>
    private static bool IsSignInAddress(string email) =>
        MailAddress.TryCreate(email, out var parsed)
        && string.Equals(parsed.Address, email, StringComparison.Ordinal);

    /// <summary>Reads a configured value, treating whitespace-only as unset.</summary>
    /// <param name="value">The bound value.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when nothing was configured.</returns>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
