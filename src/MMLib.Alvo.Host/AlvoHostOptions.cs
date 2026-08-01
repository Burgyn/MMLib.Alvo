namespace MMLib.Alvo.Host;

/// <summary>The standalone host's own configuration, bound from the <c>Alvo</c> section.</summary>
/// <remarks>
/// Distinct from <c>AlvoOptions</c> and <c>AlvoApiOptions</c> on purpose: those are the framework's options,
/// which an embedded host configures too. These are decisions only a <em>container</em> makes — where the
/// mounted descriptor is, which driver to register, what path base it is served under, whether to serve a
/// docs UI. Bound from <c>Alvo:*</c>, so the container's environment form is the standard .NET
/// <c>Alvo__DescriptorPath</c> / <c>Alvo__Database__Provider</c>.
/// </remarks>
public sealed class AlvoHostOptions
{
    /// <summary>Gets or sets the project descriptor's path (default <c>/alvo/descriptor.json</c>, the image's mount point).</summary>
    public string DescriptorPath { get; set; } = "/alvo/descriptor.json";

    /// <summary>Gets or sets which database driver to register and how to reach it.</summary>
    public AlvoHostDatabaseOptions Database { get; set; } = new();

    /// <summary>Gets or sets the path base the host is served under, for a deployment behind a reverse proxy that does not rewrite (default none).</summary>
    public string? PathBase { get; set; }

    /// <summary>Gets or sets whether a reverse proxy's <c>X-Forwarded-*</c> headers are trusted.</summary>
    public AlvoHostForwardedHeadersOptions ForwardedHeaders { get; set; } = new();

    /// <summary>Gets or sets whether the interactive API documentation is served.</summary>
    public AlvoHostDocsOptions Docs { get; set; } = new();
}

/// <summary>Whether the standalone host trusts a reverse proxy's forwarded headers.</summary>
public sealed class AlvoHostForwardedHeadersOptions
{
    /// <summary>Gets or sets whether <c>X-Forwarded-For</c>, <c>-Proto</c>, <c>-Host</c> and <c>-Prefix</c> are honoured (default <see langword="false"/>).</summary>
    /// <remarks>
    /// <b>Off by default, and that is a security decision rather than a conservative default.</b>
    /// <c>X-Forwarded-Prefix</c> decides the URL the host advertises in a 201's <c>Location</c>, so honouring it
    /// from an untrusted caller lets that caller choose where a client is sent next. Turning it on also clears
    /// <c>KnownIPNetworks</c> and <c>KnownProxies</c>, because a container cannot know its proxy's address — which
    /// is exactly why the switch is explicit: it says "something in front of me strips these", and only an
    /// operator knows that.
    /// </remarks>
    public bool Enabled { get; set; }
}

/// <summary>Which database the standalone host registers, and how to reach it.</summary>
public sealed class AlvoHostDatabaseOptions
{
    /// <summary>The SQLite driver's configuration value.</summary>
    public const string Sqlite = "sqlite";

    /// <summary>The PostgreSQL driver's configuration value.</summary>
    public const string PostgreSql = "postgresql";

    /// <summary>Gets or sets the driver to register — <see cref="Sqlite"/> or <see cref="PostgreSql"/>.</summary>
    /// <remarks>
    /// SQLite is the default because the deployment acceptance criterion is a working backend with
    /// <em>no</em> configuration at all; PostgreSQL is what <c>docker-compose.yml</c> selects.
    /// </remarks>
    public string Provider { get; set; } = Sqlite;

    /// <summary>
    /// Gets or sets the connection string used when <see cref="Provider"/> is <see cref="Sqlite"/> and
    /// <c>ConnectionStrings:Alvo</c> is not set.
    /// </summary>
    /// <remarks>
    /// The one place a database location is defaulted, and only for SQLite: a PostgreSQL host with no
    /// connection string must fail rather than silently write to a container-local file that vanishes with
    /// the container.
    /// </remarks>
    public string SqliteConnectionString { get; set; } = "Data Source=/alvo/data/alvo.db";
}

/// <summary>Whether the standalone host serves interactive API documentation.</summary>
public sealed class AlvoHostDocsOptions
{
    /// <summary>Gets or sets whether the docs UI and the OpenAPI document are served (default <see langword="true"/>).</summary>
    /// <remarks>
    /// On by default because the document <em>is</em> the contract an agent reads (§0 principle 4), and
    /// because the design already commits to the declared, non-hidden schema shape being public
    /// (deviation 27). A deployment that disagrees turns it off with one setting.
    /// </remarks>
    public bool Enabled { get; set; } = true;
}
