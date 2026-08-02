using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Host.Internal;

namespace MMLib.Alvo.Host;

/// <summary>
/// The standalone host's composition, as two methods so a test can start the real pipeline over a
/// <c>TestServer</c> instead of re-assembling an approximation of it.
/// </summary>
/// <remarks>
/// <c>Program.cs</c> is deliberately three lines: everything worth a test lives here.
/// <see cref="CreateBuilder"/> registers, <see cref="BuildAsync"/> applies and maps — the two seams
/// <c>docs/architecture/extensibility.md</c> rule 10 keeps orthogonal, in the one order that works
/// (<c>MapAlvoDataApi</c> reads route literals off the applied schema).
/// </remarks>
public static class AlvoHost
{
    /// <summary>The configuration section the host's own options are bound from.</summary>
    public const string ConfigurationSection = "Alvo";

    /// <summary>The route a container's liveness probe calls.</summary>
    public const string LivenessPath = "/health/live";

    /// <summary>The OpenAPI document's name, and therefore its version segment.</summary>
    public const string OpenApiDocumentName = "v1";

    /// <summary>Where the OpenAPI document is served.</summary>
    public const string OpenApiDocumentPath = "/openapi/v1.json";

    /// <summary>Where the interactive documentation is served.</summary>
    public const string ScalarPath = "/scalar";

    private const string AuthSection = $"{ConfigurationSection}:Auth";
    private const string ApiSection = $"{ConfigurationSection}:Api";
    private const string ConnectionName = "Alvo";

    /// <summary>
    /// Registers everything the standalone host needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="configureConfiguration"/> runs <em>before</em> Alvo is registered, because
    /// <c>AddAlvo</c>'s callback is eager: the descriptor path and the driver are read here, so a caller with
    /// its own configuration source has to contribute it before that read. A container passes nothing (the
    /// environment is already a source); a test passes its own collection.
    /// </para>
    /// <para>
    /// The docs registration comes <em>before</em> <c>AddAlvo</c> for a different reason: registration order is
    /// document-transformer order, and Alvo's transformer appends to <c>info.description</c> rather than
    /// replacing it, so the host's own <c>info</c> has to be written first. The docs <em>routes</em> map after
    /// the Data API's — see <see cref="BuildAsync"/>. The two orderings are opposite and both deliberate.
    /// </para>
    /// </remarks>
    /// <param name="args">The process arguments, bound as a configuration source by ASP.NET Core.</param>
    /// <param name="configureConfiguration">Adds configuration sources before Alvo is registered.</param>
    /// <returns>The builder, for a caller that wants to add logging or a test server.</returns>
    public static WebApplicationBuilder CreateBuilder(
        string[] args, Action<IConfigurationBuilder>? configureConfiguration = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configureConfiguration?.Invoke(builder.Configuration);

        var options = HostOptions(builder.Configuration);

        builder.Services.Configure<AlvoHostOptions>(builder.Configuration.GetSection(ConfigurationSection));
        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection(AuthSection));

        if (options.ForwardedHeaders.Enabled)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(ConfigureForwardedHeaders);
        }

        builder.Services.AddHealthChecks();
        builder.Services.AddAlvoProblemDetails();

        if (options.Docs.Enabled)
        {
            builder.Services.AddAlvoHostDocs();
        }

        builder.Services.AddAlvo(alvo => Configure(alvo, options, builder.Configuration));

        return builder;
    }

    /// <summary>
    /// Builds the application, applies the mounted descriptor, and maps the generated Data API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UseExceptionHandler</c> is first because a middleware only sees what runs after it: in standalone
    /// mode Alvo <em>is</em> the pipeline, so an unhandled failure that got past this line would be answered
    /// by the framework with an RFC 9110 status-code URI in <c>type</c> (#119).
    /// </para>
    /// <para>
    /// The two that follow both decide the request's <c>PathBase</c>, which is the URL a 201's <c>Location</c>
    /// advertises (#121). Neither is followed by an explicit <c>UseRouting</c>: <c>UsePathBaseMiddleware</c>
    /// re-runs matching over the rewritten path itself, and <c>UseForwardedHeaders</c> leaves <c>Path</c>
    /// alone. See <c>docs/architecture/host.md</c>, "Behind a reverse proxy".
    /// </para>
    /// <para>
    /// The docs routes map <em>last</em>, after <c>MapAlvoDataApi</c>: the document is generated from the
    /// endpoints actually mapped, so a document route registered before the Data API's would describe an empty
    /// API.
    /// </para>
    /// <para>
    /// Every <c>ValidateOnStart</c> registration runs <em>before</em> the apply, and that ordering is the
    /// difference between a recoverable mistake and an unbootable deployment — see
    /// <see cref="ValidateOptions"/>.
    /// </para>
    /// <para>
    /// The apply's result is <em>checked</em>, not discarded. A refused destructive plan is a return value
    /// rather than an exception (<c>MigrationResult.EnsureApplied</c>'s remarks say why), and an unchecked
    /// one leaves the policy catalog unprimed: the host would then map zero routes, answer liveness, report
    /// healthy, and 404 every <c>/api/*</c> call — an ordinary GitOps edit that drops a field, on the next
    /// restart. Availability silently zero is worse than a container that fails to start, so the guard turns
    /// it back into the failed start <c>MapAlvoLiveness</c>'s remarks already claim.
    /// </para>
    /// </remarks>
    /// <param name="builder">The builder <see cref="CreateBuilder"/> returned.</param>
    /// <param name="ct">Cancels the descriptor apply.</param>
    /// <returns>The started-but-not-yet-running application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">
    /// A registration that asked to be validated at startup refused its configuration — a misspelled dev-key
    /// scope, say. Raised <em>before</em> the descriptor is applied, so a misconfigured deployment leaves the
    /// database exactly as it found it.
    /// </exception>
    /// <exception cref="Migrations.DestructiveChangeNotAllowedException">
    /// The mounted descriptor's plan was refused as destructive. The host applies with
    /// <c>AllowDestructive: false</c> and offers no setting to change that, so this is how a descriptor
    /// that would drop a column or a table fails the start instead of losing data.
    /// </exception>
    public static async Task<WebApplication> BuildAsync(
        WebApplicationBuilder builder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = builder.Build();

        try
        {
            return await ComposeAsync(app, ct).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Everything <see cref="BuildAsync"/> does to an application it has already built, so the one thing that
    /// can fail has exactly one owner responsible for disposing it.
    /// </summary>
    /// <remarks>
    /// <b>Split out because a refused start used to leak the whole application.</b>
    /// <c>WebApplicationBuilder.Build()</c> creates a full service provider; if the apply or
    /// <c>EnsureApplied</c> then threw, nothing disposed it, and the store's connection pool kept the
    /// database file open for the rest of the process. That leak was visible in this repository's own suite
    /// long before it was named — the host fixture's database cleanup swallowed an <c>IOException</c>
    /// "tolerating a file a refused start still holds open" — and in a container it is the difference between
    /// a clean non-zero exit and a process holding a socket and a file while the orchestrator restarts it.
    /// </remarks>
    /// <param name="app">The application <see cref="BuildAsync"/> built.</param>
    /// <param name="ct">Cancels the descriptor apply.</param>
    private static async Task<WebApplication> ComposeAsync(WebApplication app, CancellationToken ct)
    {
        var options = app.Services.GetRequiredService<IOptions<AlvoHostOptions>>().Value;

        app.UseExceptionHandler();

        if (options.ForwardedHeaders.Enabled)
        {
            app.UseForwardedHeaders();
        }

        if (options.PathBase is { Length: > 0 } pathBase)
        {
            app.UsePathBase(pathBase);
        }

        app.MapAlvoLiveness();

        ValidateOptions(app.Services);

        var migration = await app.Services.ApplyAlvoDescriptorAsync(ct: ct).ConfigureAwait(false);
        migration.EnsureApplied();

        app.MapAlvoDataApi();

        if (options.Docs.Enabled)
        {
            app.MapAlvoHostDocs();
        }

        return app;
    }

    /// <summary>
    /// Runs every <c>ValidateOnStart</c> registration in the container, before anything touches the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ordering, not tidiness.</b> <c>ValidateOnStart</c> runs from <c>app.StartAsync()</c>, which is
    /// after <see cref="BuildAsync"/> has already applied the descriptor — nothing on the apply path resolves
    /// any of the validated option types. So a single mistyped
    /// <c>Alvo__Auth__DevKeys__0__Scopes__0</c> committed the migration against the production database and
    /// <em>then</em> crash-looped, and rolling the deployment back did not recover: the previous descriptor is
    /// destructive relative to the schema the failed start had already written, so
    /// <c>MigrationResult.EnsureApplied</c> refuses that start too. Validating first turns that into an
    /// ordinary failed start with nothing changed.
    /// </para>
    /// <para>
    /// <b><see cref="IStartupValidator"/> rather than resolving <c>IOptions&lt;AlvoAuthOptions&gt;</c>.</b> It
    /// is the same seam the host itself uses at start, so it runs <em>every</em> registration — auth's today,
    /// and whatever the next option type registers — and this cannot silently stop covering one. It is
    /// resolved with <c>GetService</c> because a composition that registered no <c>ValidateOnStart</c> at all
    /// has no such service, and re-running it at start costs one more pass over stateless validators.
    /// </para>
    /// </remarks>
    /// <param name="services">The built application's services.</param>
    private static void ValidateOptions(IServiceProvider services) =>
        services.GetService<IStartupValidator>()?.Validate();

    /// <summary>
    /// The flags the host honours when — and only when — <see cref="AlvoHostForwardedHeadersOptions.Enabled"/>
    /// says something in front of it sets them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registered only when the switch is on, and that is the security half of the switch rather than
    /// tidiness.</b> ASP.NET Core registers a <c>ForwardedHeadersStartupFilter</c> of its own whenever
    /// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> — the standard container recipe, and a variable an
    /// operator may well set without knowing Alvo has a switch of its own. That filter calls
    /// <c>UseForwardedHeaders</c> against the <em>same</em> <see cref="ForwardedHeadersOptions"/> instance
    /// this configures, so a version of this that always ran would hand the framework's filter Alvo's
    /// permissive flags with both known-address lists cleared while
    /// <see cref="AlvoHostForwardedHeadersOptions.Enabled"/> was still <see langword="false"/> — any internet
    /// client could then set <c>X-Forwarded-Prefix</c> and choose the URL a 201 advertises, which is exactly
    /// what that option's remarks promise the switch prevents.
    /// </para>
    /// <para>
    /// The known-address lists are cleared because a container cannot know its proxy's address, and their
    /// defaults (IPv6 loopback) would drop every header a sidecar or an ingress sends.
    /// <c>KnownIPNetworks</c> rather than <c>KnownNetworks</c>: the latter is obsolete as of .NET 10
    /// (<c>ASPDEPR005</c>), and this repository builds warnings as errors.
    /// </para>
    /// </remarks>
    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost
            | ForwardedHeaders.XForwardedPrefix;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }

    private static void Configure(
        IAlvoBuilder alvo, AlvoHostOptions options, ConfigurationManager configuration)
    {
        AlvoDatabaseSelector.Select(alvo, options.Database, ConnectionString(configuration));
        alvo.FromDescriptor(options.DescriptorPath)
            .AddDataApi(api => configuration.GetSection(ApiSection).Bind(api));
    }

    private static string? ConnectionString(ConfigurationManager configuration) =>
        configuration.GetConnectionString(ConnectionName) is { } configured
        && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : null;

    /// <summary>
    /// Reads the host's options a second time, beside the <c>Configure&lt;AlvoHostOptions&gt;</c> registration.
    /// </summary>
    /// <remarks>
    /// Deliberate rather than an oversight: the driver has to be chosen while the container is still being
    /// <em>built</em>, and <c>IOptions&lt;T&gt;</c> is only resolvable after. The registration exists for the
    /// pieces that read the same options off the built container. One binder, one section, two moments — not
    /// two spellings.
    /// </remarks>
    private static AlvoHostOptions HostOptions(ConfigurationManager configuration) =>
        configuration.GetSection(ConfigurationSection).Get<AlvoHostOptions>() ?? new AlvoHostOptions();
}
