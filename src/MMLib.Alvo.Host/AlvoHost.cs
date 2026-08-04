using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Host.Internal;

namespace MMLib.Alvo.Host;

/// <summary>
/// The standalone host's composition, as two methods so a test can start the real pipeline over a
/// <c>TestServer</c> instead of re-assembling an approximation of it.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.cs</c> is deliberately one line: everything worth a test lives here.
/// <see cref="CreateBuilder"/> registers, <see cref="BuildAsync"/> maps — the two seams
/// <c>docs/architecture/extensibility.md</c> rule 10 keeps orthogonal — and <see cref="RunAsync(string[])"/> is the
/// process itself: the three of them together, plus the refusal an operator reads and the exit code they get.
/// </para>
/// <para>
/// <b>Nothing here applies the descriptor.</b> The boot owns that, from the host lifecycle, before the server
/// binds — so the host composes a pipeline and never sequences a database against a route table. The
/// standalone host is consequently the same shape as an embedded one: <c>AddAlvo</c>, then <c>MapAlvo</c>.
/// </para>
/// </remarks>
public static class AlvoHost
{
    /// <summary>The configuration section the host's own options are bound from.</summary>
    public const string ConfigurationSection = "Alvo";

    /// <summary>The route a container's liveness probe calls.</summary>
    /// <remarks>
    /// The route itself is the core's, and this forwards to <see cref="AlvoHealth.LivenessPath"/> so the two
    /// cannot drift. <see cref="AlvoHealth.ReadinessPath"/> is deliberately <em>not</em> mirrored here: readiness
    /// is a framework signal an embedded host reads too, and a second spelling of it in the standalone host
    /// would be one more place for a probe path to go stale.
    /// </remarks>
    public const string LivenessPath = AlvoHealth.LivenessPath;

    /// <summary>The OpenAPI document's name, and therefore its version segment.</summary>
    public const string OpenApiDocumentName = "v1";

    /// <summary>Where the OpenAPI document is served.</summary>
    public const string OpenApiDocumentPath = "/openapi/v1.json";

    /// <summary>Where the interactive documentation is served.</summary>
    public const string ScalarPath = "/scalar";

    private const string AuthSection = $"{ConfigurationSection}:Auth";
    private const string ApiSection = $"{ConfigurationSection}:Api";

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
    /// replacing it, so the host's own <c>info</c> has to be written first. This is the docs ordering that is
    /// load-bearing; the one their <em>routes</em> map in is not — see <see cref="BuildAsync"/>.
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

        AddHostOptions(builder);
        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection(AuthSection));

        if (options.ForwardedHeaders.Enabled)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(ConfigureForwardedHeaders);
        }

        builder.Services.AddAlvoProblemDetails();

        if (options.Docs.Enabled)
        {
            builder.Services.AddAlvoHostDocs();
        }

        builder.Services.AddAlvo(alvo => Configure(alvo, options, builder.Configuration));

        return builder;
    }

    /// <summary>
    /// Registers, builds, runs, and answers with the process's exit code — the whole of the container's
    /// <c>Program.cs</c>, in one place a test can call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A misconfigured container reads a sentence and exits deliberately (#132).</b> A mis-typed descriptor
    /// mount used to end in an unhandled <see cref="FileNotFoundException"/> and an exit code shaped like a
    /// segmentation fault; it now prints the path and the fix and exits
    /// <c>78</c> (<c>EX_CONFIG</c>). Refusing to start is unchanged and deliberate — see
    /// <c>docs/architecture/host.md</c> — because a container that reported healthy with no schema would be
    /// strictly worse. Only <see cref="AlvoHostExit.IsConfigurationFailure"/>'s two shapes are caught; anything
    /// else still propagates, so a genuine defect keeps the runtime's own report and crash dump.
    /// </para>
    /// <para>
    /// The work is the overload below, over the builder <see cref="CreateBuilder"/> makes; this method is that
    /// one composition and nothing else, so the entry point a container runs and the entry point a fact runs
    /// cannot come apart.
    /// </para>
    /// </remarks>
    /// <param name="args">The process arguments.</param>
    /// <returns>The exit code: <c>0</c> on a clean shutdown, <c>78</c> for a configuration the host refused.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(() => CreateBuilder(args));

    /// <summary>
    /// The process, over a builder somebody else assembles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A factory rather than a built builder, so registration stays inside the <c>try</c>.</b>
    /// <see cref="CreateBuilder"/> chooses the driver while the container is still being assembled and refuses
    /// an unknown name there, before any of this runs; that refusal is owed the same printed sentence and the
    /// same exit code as one raised at start, and calling the factory here is what keeps it one <c>catch</c>
    /// rather than two.
    /// </para>
    /// <para>
    /// <b>The <c>await using</c> states ownership rather than fixing a leak, and the distinction was
    /// measured.</b> On .NET 10, <c>WebApplication.RunAsync</c> forwards to
    /// <c>HostingAbstractionsHostExtensions.RunAsync</c>, whose <c>finally</c> already disposes the application
    /// when <c>StartAsync</c> throws — measured here, unlike <c>app.StartAsync()</c>, which does not. That is an
    /// implementation detail of an extension method with no API-level guarantee, and a refused boot leaking the
    /// container would keep the database file open for the rest of the process, which is the regression
    /// <see cref="Compose"/> exists to have fixed once already on the build side. Saying who owns the
    /// application is cheaper than depending on the framework continuing not to need it said.
    /// </para>
    /// <para>
    /// <b>This is also where a refused <em>boot</em> is disposed, and since the apply moved into the host
    /// lifecycle it is the only place that can be.</b> <see cref="BuildAsync"/> returns before anything starts,
    /// so the application a drifted descriptor refuses is one this method already owns — which is why
    /// <c>A_refused_restart_disposes_the_application_it_had_already_built</c> is written against this overload
    /// rather than against a fixture that starts an application nobody owns.
    /// </para>
    /// </remarks>
    /// <param name="build">Assembles the builder to run — <see cref="CreateBuilder"/>, or a test's own.</param>
    /// <returns>The exit code: <c>0</c> on a clean shutdown, <c>78</c> for a configuration the host refused.</returns>
    internal static async Task<int> RunAsync(Func<WebApplicationBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        try
        {
            var app = await BuildAsync(build()).ConfigureAwait(false);

            await using (app.ConfigureAwait(false))
            {
                await app.RunAsync().ConfigureAwait(false);
            }

            return AlvoHostExit.Success;
        }
        catch (Exception refusal) when (AlvoHostExit.IsConfigurationFailure(refusal))
        {
            await Console.Error.WriteLineAsync(AlvoHostExit.Describe(refusal)).ConfigureAwait(false);

            return AlvoHostExit.ConfigurationFailure;
        }
    }

    /// <summary>
    /// Builds the application and maps Alvo's HTTP surface onto it.
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
    /// <b>There is no apply here and no ordering left to get wrong.</b> <c>MapAlvo</c> maps the probes and the
    /// Data API, whose routes materialise from the schema Alvo's boot primed before the server bound — so the
    /// host neither runs DDL nor has to map after it. What used to guard that sequence, and is now the boot's,
    /// is the <em>whole</em> reason a refused descriptor is a failed start: see
    /// <c>MMLib.Alvo.Migrations.AlvoStartupRefusedException</c> and <c>RunAsync</c>'s exit code.
    /// </para>
    /// <para>
    /// The docs routes still map after <c>MapAlvo</c>, but only because that is the order they read in. The
    /// document is generated per request by enumerating the endpoint data sources, so nothing about its content
    /// depends on when its route was registered. The ordering that <em>is</em> load-bearing runs the other way
    /// and lives in <see cref="CreateBuilder"/>: registration order is document-transformer order.
    /// </para>
    /// </remarks>
    /// <param name="builder">The builder <see cref="CreateBuilder"/> returned.</param>
    /// <returns>The built-but-not-yet-started application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">
    /// The host's own options were refused — a mount path with no descriptor at it, an unknown driver name, a
    /// PostgreSQL host with no connection string. Reading <c>IOptions&lt;AlvoHostOptions&gt;</c> is what runs
    /// <see cref="AlvoHostOptionsValidation"/>, and composition reads it as its first act, so a misconfigured
    /// deployment is refused here, before anything is started and therefore before any DDL.
    /// <see cref="RunAsync(string[])"/> turns it into a printed refusal and a deliberate exit code. Refusals raised by
    /// <em>starting</em> the application — the boot's own, and every other <c>ValidateOnStart</c> registration
    /// — surface from <c>StartAsync</c> instead.
    /// </exception>
    public static async Task<WebApplication> BuildAsync(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = builder.Build();

        try
        {
            return Compose(app);
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
    /// <b>Split out because a refused composition would otherwise leak the whole application.</b>
    /// <c>WebApplicationBuilder.Build()</c> creates a full service provider, and reading
    /// <c>IOptions&lt;AlvoHostOptions&gt;</c> below is what runs <see cref="AlvoHostOptionsValidation"/> — so a
    /// container with a mistyped mount path throws from the first line of this method, with a live service
    /// provider behind it. Nothing disposing it would keep the store's connection pool, and the database file,
    /// open for the rest of the process. That leak was visible in this repository's own suite long before it was
    /// named — the host fixture's database cleanup swallowed an <c>IOException</c> "tolerating a file a refused
    /// start still holds open" — and in a container it is the difference between a clean non-zero exit and a
    /// process holding a socket and a file while the orchestrator restarts it. A refusal raised by
    /// <em>starting</em> the application is <see cref="RunAsync(string[])"/>'s to own, not this method's, and it owns it
    /// with <c>await using</c>.
    /// </remarks>
    /// <param name="app">The application <see cref="BuildAsync"/> built.</param>
    private static WebApplication Compose(WebApplication app)
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

        app.MapAlvo();

        if (options.Docs.Enabled)
        {
            app.MapAlvoHostDocs();
        }

        return app;
    }

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

    /// <summary>
    /// Binds <see cref="AlvoHostOptions"/> and refuses a bad value at startup, before anything touches the
    /// database.
    /// </summary>
    /// <remarks>
    /// <c>ValidateOnStart</c> rather than a check at first use: <c>extensibility.md</c> rule 5, and the
    /// acceptance criterion A:91. <see cref="AlvoHostOptionsValidation"/>'s own remarks say why the ordering
    /// against the boot's DDL is a guarantee rather than a preference. <c>TryAddEnumerable</c>, so a host that
    /// composed twice validates once.
    /// </remarks>
    /// <param name="builder">The builder being registered.</param>
    private static void AddHostOptions(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AlvoHostOptions>()
            .Bind(builder.Configuration.GetSection(ConfigurationSection))
            .ValidateOnStart();

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoHostOptions>, AlvoHostOptionsValidation>());
    }

    private static string? ConnectionString(ConfigurationManager configuration) =>
        configuration.GetConnectionString(AlvoHostConfiguration.ConnectionName) is { } configured
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
