using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Host.Internal;

/// <summary>
/// The standalone host's configuration vocabulary: the names an operator sets, the comparison that reads
/// them, and the refusal written for each way of getting one wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because the same refusal is raised from two moments.</b> The driver has to be chosen while
/// the container is still being <em>built</em> (<see cref="AlvoDatabaseSelector"/>), and every option value is
/// validated again on the built container (<see cref="AlvoHostOptionsValidation"/>). Two moments, one wording:
/// an operator must not be able to tell which of them refused, and a reworded fix must not be able to reach
/// only one of them. Task 3 made the <c>Alvo__Schema__*</c> spellings <see langword="const"/> members for the
/// same reason; this is that pattern for the host's own keys.
/// </para>
/// <para>
/// <b>The environment spelling, not the colon spelling.</b> A container operator sets
/// <c>Alvo__DescriptorPath</c>, never <c>Alvo:DescriptorPath</c>, and a refusal that quoted the configuration
/// path rather than the variable would name something they cannot type. <see langword="internal"/> throughout,
/// so nothing here appears in the host's public surface; the facts keep their own literals on purpose, so a
/// rename of a constant cannot silently rename the wire contract.
/// </para>
/// </remarks>
internal static class AlvoHostConfiguration
{
    /// <summary>The environment variable naming the mounted descriptor.</summary>
    internal const string DescriptorPathVariable = "Alvo__DescriptorPath";

    /// <summary>The environment variable naming the database driver.</summary>
    internal const string ProviderVariable = "Alvo__Database__Provider";

    /// <summary>The environment variable carrying the database connection string.</summary>
    internal const string ConnectionStringVariable = "ConnectionStrings__Alvo";

    /// <summary>The <c>ConnectionStrings</c> entry the host resolves its database from.</summary>
    internal const string ConnectionName = "Alvo";

    /// <summary>The environment variable naming the file holding the bootstrap administrator's password.</summary>
    internal const string BootstrapPasswordFileVariable = "Alvo__Admin__BootstrapPasswordFile";

    /// <summary>The variable an operator reaches for instead, and which this host refuses.</summary>
    internal const string BootstrapPasswordVariable = "Alvo__Admin__BootstrapPassword";

    /// <summary>Whether a configured provider name is the known one, however it was capitalized.</summary>
    /// <param name="configured">What configuration said.</param>
    /// <param name="known">The driver name to compare against.</param>
    internal static bool Is(string? configured, string known) =>
        string.Equals(configured, known, StringComparison.OrdinalIgnoreCase);

    /// <summary>The refusal for a host with no descriptor path configured at all.</summary>
    internal static string NoDescriptorPathConfigured() => Sentence(
        "Alvo cannot start: no project descriptor path is configured.",
        $"  Set:        {DescriptorPathVariable}=/path/to/descriptor.json");

    /// <summary>The refusal #132 is about: a mount point with nothing at it.</summary>
    /// <param name="path">The path the host was told to read, quoted so the typo is visible.</param>
    internal static string NoDescriptorAt(string path) => Sentence(
        $"Alvo cannot start: no project descriptor at {path}.",
        "  Mount one:  docker run -v ./project.alvo.json:/alvo/descriptor.json mmlib/alvo",
        $"  Or set:     {DescriptorPathVariable}=/path/to/descriptor.json");

    /// <summary>The refusal for a driver name this host does not ship.</summary>
    /// <param name="configured">The name configuration asked for.</param>
    internal static string UnknownProvider(string? configured) => Sentence(
        $"Alvo cannot start: '{configured}' is not a database provider this host can register.",
        $"  Set:        {ProviderVariable}={AlvoHostDatabaseOptions.Sqlite} (the default)",
        $"  Or:         {ProviderVariable}={AlvoHostDatabaseOptions.PostgreSql}");

    /// <summary>
    /// The refusal for the one misconfiguration that would otherwise lose every row: PostgreSQL selected with
    /// nowhere to reach it, which must never fall back to a container-local file.
    /// </summary>
    internal static string NoPostgreSqlConnectionString() => Sentence(
        $"Alvo cannot start: the {AlvoHostDatabaseOptions.PostgreSql} provider is selected and no connection "
            + "string is configured.",
        $"  Set:        {ConnectionStringVariable}=Host=db;Database=alvo;Username=alvo;Password=...",
        $"  Or:         {ProviderVariable}={AlvoHostDatabaseOptions.Sqlite} to use the container-local file.");

    /// <summary>The refusal for a mounted secret that is present and says nothing.</summary>
    /// <remarks>
    /// The one bootstrap check that is genuinely host-specific rather than a duplicate of
    /// <c>AlvoIdentityOptionsValidation</c>'s own: the package deliberately never reads the secret
    /// file's contents (a credential in a validator's reach, and in a failure message, is the wrong
    /// trade for what existence already catches), but a container that mounted an empty file would
    /// otherwise seed an administrator nobody could ever sign in as, and fail-fast is the whole point
    /// of #132.
    /// </remarks>
    /// <param name="path">The mounted file, quoted so the operator knows which mount to check.</param>
    internal static string EmptyBootstrapPassword(string path) => Sentence(
        $"Alvo cannot start: the bootstrap password file {path} is empty.",
        "  Write the administrator's password into it, with no surrounding quotes.");

    /// <summary>The refusal for a mounted secret this process is not allowed to open.</summary>
    /// <remarks>
    /// <b>A refusal rather than the stack trace #132 is about.</b> The image runs as
    /// <c>USER $APP_UID</c>, so the ordinary hardening choice — a root-owned <c>0400</c> secret, which is
    /// also what Kubernetes' <c>defaultMode</c> produces without an <c>fsGroup</c> — makes
    /// <see cref="File.Exists(string)"/> true and the read throw. That is a misconfiguration an operator
    /// can fix, so it is owed the same sentence and the same exit code as every other one.
    /// </remarks>
    /// <param name="path">The mounted file, quoted so the operator knows which mount to check.</param>
    /// <param name="reason">
    /// What the operating system said. Never the file's contents: only the two failures raised by
    /// <em>opening</em> it are reported this way, and neither has read anything.
    /// </param>
    internal static string UnreadableBootstrapPassword(string path, string reason) => Sentence(
        $"Alvo cannot start: the bootstrap password file {path} cannot be read ({reason}).",
        "  The image runs as a non-root user, so a root-owned 0400 secret is unreadable inside it.",
        "  Mount it readable by the container's user (Kubernetes: fsGroup; docker: --user), or set",
        $"              {BootstrapPasswordFileVariable} to a path the container can read.");

    /// <summary>
    /// The refusal for a password supplied as configuration rather than as a mounted file.
    /// </summary>
    /// <remarks>
    /// An environment variable is readable from a process listing, a crash dump and
    /// <c>docker inspect</c>; a mounted secret file is not. That is the entire reason the option is a
    /// path, so accepting the value would quietly undo it.
    /// <see cref="MMLib.Alvo.Identity.AlvoIdentityOptions"/> has no property for this key at all — the
    /// package cannot refuse a setting it never binds — so this refusal is the host's alone, over the
    /// raw <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> entry.
    /// </remarks>
    internal static string BootstrapPasswordInConfiguration() => Sentence(
        $"Alvo cannot start: {BootstrapPasswordVariable} is set. Alvo never reads a password from "
            + "configuration, because an environment variable is readable from a process listing and a "
            + "crash dump.",
        $"  Unset:      {BootstrapPasswordVariable}",
        $"  And set:    {BootstrapPasswordFileVariable}=/run/secrets/alvo-admin-password");

    /// <summary>
    /// Turns one refusal into the exception the host raises for <em>every</em> bad option value, whichever
    /// moment found it.
    /// </summary>
    /// <remarks>
    /// <see cref="OptionsValidationException"/> rather than a type of Alvo's own, because that is what
    /// <c>ValidateOnStart</c> raises for the very same options object a moment later, and one type is what lets
    /// <see cref="AlvoHostExit"/> present both identically. The type is not what an operator reads — the
    /// message is — but it is what a host author catches, and two types for one condition would be two.
    /// </remarks>
    /// <param name="failure">The refusal, complete enough to print on its own.</param>
    internal static OptionsValidationException Refuse(string failure) =>
        new(Options.DefaultName, typeof(AlvoHostOptions), [failure]);

    /// <summary>A headline an operator can act on, a blank line, and the fixes — the shape #132 asks for.</summary>
    /// <param name="headline">What is wrong, naming the offending value.</param>
    /// <param name="fixes">What to change, spelled as the environment variables a container sets.</param>
    private static string Sentence(string headline, params string[] fixes) =>
        string.Join(Environment.NewLine, [headline, string.Empty, .. fixes]);
}
