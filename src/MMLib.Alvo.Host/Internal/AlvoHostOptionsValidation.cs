using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Host.Internal;

/// <summary>
/// Refuses a misconfigured container at startup, by name and with the fix — <c>extensibility.md</c> rule 5 for
/// <see cref="AlvoHostOptions"/>, and the acceptance criterion A:91 ("validate provider configuration at
/// startup, fail fast with an actionable message, not at first use").
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered with <c>ValidateOnStart</c>, and the ordering is the whole point.</b>
/// <c>Host.StartAsync</c> runs every such registration <em>before</em> the first
/// <c>IHostedLifecycleService.StartingAsync</c>, which is where Alvo's boot runs its DDL. So a typo in a
/// mount path or a driver name fails the start with the database exactly as it was found, rather than after a
/// migration has been committed against it — and that is not tidiness: the previous descriptor is destructive
/// relative to the schema a half-finished start already wrote, so rolling the deployment back would not
/// recover.
/// </para>
/// <para>
/// <b>Every check the driver would otherwise make lazily.</b> The PostgreSQL driver refuses a missing
/// connection string too, but only when something first resolves a store — inside the boot, after the system
/// schema has been touched. Asking here moves the same refusal ahead of it and gives it the container's own
/// spelling of the key.
/// </para>
/// <para>
/// <b><see cref="IConfiguration"/> rather than a second options type</b>, because the connection string is not
/// part of <see cref="AlvoHostOptions"/>: it is the standard <c>ConnectionStrings:Alvo</c> entry, which is the
/// one thing about the host's database that is spelled the way every other .NET application spells it.
/// </para>
/// </remarks>
/// <param name="configuration">The host's configuration, for the <c>ConnectionStrings</c> entry.</param>
internal sealed class AlvoHostOptionsValidation(IConfiguration configuration) : IValidateOptions<AlvoHostOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string[] failures = [.. Failures(options)];

        return failures.Length is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Every refusal this configuration has earned, rather than only the first.
    /// </summary>
    /// <remarks>
    /// A container with two things wrong is one restart per fix if only the first is reported, and an operator
    /// reading a crash loop cannot tell a second failure from the same failure again.
    /// </remarks>
    /// <param name="options">The bound host options.</param>
    private IEnumerable<string> Failures(AlvoHostOptions options)
    {
        if (Descriptor(options.DescriptorPath) is { } descriptor)
        {
            yield return descriptor;
        }

        if (Database(options.Database) is { } database)
        {
            yield return database;
        }
    }

    /// <summary>
    /// Whether the mounted descriptor is where the host was told it is — #132's actual subject.
    /// </summary>
    /// <remarks>
    /// <b>Existence, not only non-emptiness, and it is a deliberate time-of-check/time-of-use trade.</b> The
    /// file is read a moment later, in stage 0, so a file deleted in between still fails the start the old way.
    /// That window is microseconds wide and needs someone to unmount a volume mid-boot; the case this closes —
    /// a path that was never right — is the single most likely way a first <c>docker run</c> goes wrong, and
    /// leaving it to the reader produced an unhandled <see cref="FileNotFoundException"/> and a
    /// SIGSEGV-shaped exit code.
    /// </remarks>
    /// <param name="path">The configured descriptor path.</param>
    private static string? Descriptor(string path) =>
        string.IsNullOrWhiteSpace(path) ? AlvoHostConfiguration.NoDescriptorPathConfigured()
            : File.Exists(path) ? null
            : AlvoHostConfiguration.NoDescriptorAt(path);

    /// <summary>
    /// Whether the named driver exists and can be reached.
    /// </summary>
    /// <remarks>
    /// The unknown-name arm is reached only by a host that configured
    /// <see cref="AlvoHostOptions"/> without going through <see cref="AlvoHost.CreateBuilder"/> — the driver is
    /// selected there, from the same names, and refuses first. It is checked anyway because leaving one property
    /// of a validated options type unvalidated is how a later composition slips past, and it is pinned by a fact
    /// against this class rather than through a host that cannot reach it.
    /// </remarks>
    /// <param name="database">The bound database options.</param>
    private string? Database(AlvoHostDatabaseOptions database) =>
        AlvoHostConfiguration.Is(database.Provider, AlvoHostDatabaseOptions.Sqlite) ? null
            : AlvoHostConfiguration.Is(database.Provider, AlvoHostDatabaseOptions.PostgreSql) ? PostgreSql()
            : AlvoHostConfiguration.UnknownProvider(database.Provider);

    /// <summary>
    /// Whether PostgreSQL has somewhere to connect. Never defaulted, because a PostgreSQL host that quietly
    /// wrote to a container-local file would lose every row with the container.
    /// </summary>
    private string? PostgreSql() =>
        string.IsNullOrWhiteSpace(configuration.GetConnectionString(AlvoHostConfiguration.ConnectionName))
            ? AlvoHostConfiguration.NoPostgreSqlConnectionString()
            : null;
}
