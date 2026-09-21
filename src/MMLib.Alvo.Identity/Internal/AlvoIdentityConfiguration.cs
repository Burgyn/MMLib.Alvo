namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// The bootstrap administrator's configuration vocabulary: the names an operator sets, and the
/// refusal written for each way of getting one wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>The environment spelling, not the colon spelling.</b> An operator sets
/// <c>Alvo__Admin__BootstrapEmail</c>, never <c>Alvo:Admin:BootstrapEmail</c>, and a refusal that
/// quoted the configuration path would name something they cannot type — the same reasoning, and the
/// same shape, as <c>AlvoHostConfiguration</c> in the standalone host.
/// </para>
/// <para>
/// <b>Here rather than in the host, because the host is only one of two distributions.</b> An
/// embedded host calls <c>AddAlvoIdentity</c> directly and never goes through
/// <c>AlvoHostOptionsValidation</c>, so a refusal that lived only there would leave the embedded
/// path with none at all. Task 7's host validation reports these messages; it does not own the check.
/// </para>
/// </remarks>
internal static class AlvoIdentityConfiguration
{
    /// <summary>The environment variable naming the bootstrap administrator.</summary>
    internal const string EmailVariable = "Alvo__Admin__BootstrapEmail";

    /// <summary>The environment variable naming the file the bootstrap password is mounted at.</summary>
    internal const string PasswordFileVariable = "Alvo__Admin__BootstrapPasswordFile";

    /// <summary>The refusal for an address with no secret to go with it.</summary>
    /// <param name="email">The configured address, quoted so the operator sees which half is set.</param>
    /// <returns>The refusal.</returns>
    internal static string NoPasswordFile(string email) => Sentence(
        $"Alvo cannot start: a bootstrap administrator '{email}' is configured with no password file, "
            + "so the account could never be seeded.",
        "  Mount one:  docker run -v ./admin-password:/run/secrets/alvo-admin mmlib/alvo",
        $"  And set:    {PasswordFileVariable}=/run/secrets/alvo-admin",
        $"  Or unset:   {EmailVariable}, for a deployment with no bootstrap administrator.");

    /// <summary>The refusal for a secret with nobody to belong to.</summary>
    /// <param name="passwordFile">The configured path.</param>
    /// <returns>The refusal.</returns>
    internal static string NoEmail(string passwordFile) => Sentence(
        $"Alvo cannot start: a bootstrap password file '{passwordFile}' is configured with no address, "
            + "so nothing names the account it belongs to.",
        $"  Set:        {EmailVariable}=admin@example.com",
        $"  Or unset:   {PasswordFileVariable}, for a deployment with no bootstrap administrator.");

    /// <summary>
    /// The refusal for a mount point with nothing at it — the single most likely way a first
    /// <c>docker run</c> goes wrong.
    /// </summary>
    /// <remarks>
    /// <b>Existence, not only non-emptiness, and it is the same time-of-check/time-of-use trade
    /// <c>AlvoHostOptionsValidation</c> makes for the descriptor.</b> The file is read a moment later,
    /// by the bootstrap; a file deleted in between still fails the way it did before. What this closes
    /// is the path that was never right.
    /// </remarks>
    /// <param name="passwordFile">The path the package was told to read, quoted so the typo is visible.</param>
    /// <returns>The refusal.</returns>
    internal static string NoPasswordFileAt(string passwordFile) => Sentence(
        $"Alvo cannot start: no bootstrap password file at {passwordFile}.",
        $"  Mount one:  docker run -v ./admin-password:{passwordFile} mmlib/alvo",
        $"  Or set:     {PasswordFileVariable} to where the secret is mounted.");

    /// <summary>The refusal for an address nothing could ever sign in with.</summary>
    /// <param name="email">What configuration said, quoted so the typo is visible.</param>
    /// <returns>The refusal.</returns>
    internal static string ImplausibleEmail(string email) => Sentence(
        $"Alvo cannot start: '{email}' is not an address a bootstrap administrator could sign in with.",
        $"  Set:        {EmailVariable}=admin@example.com",
        "  Note:       a bare address only — a display-name form such as "
            + "'Eva <eva@example.com>' is not a sign-in address.");

    /// <summary>A headline an operator can act on, a blank line, and the fixes.</summary>
    /// <param name="headline">What is wrong, naming the offending value.</param>
    /// <param name="fixes">What to change, spelled as the environment variables a container sets.</param>
    /// <returns>The assembled refusal.</returns>
    private static string Sentence(string headline, params string[] fixes) =>
        string.Join(Environment.NewLine, [headline, string.Empty, .. fixes]);
}
