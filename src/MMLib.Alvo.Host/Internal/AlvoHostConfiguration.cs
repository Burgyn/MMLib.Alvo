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
