using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private const string AuthSection = $"{ConfigurationSection}:Auth";
    private const string ApiSection = $"{ConfigurationSection}:Api";
    private const string ConnectionName = "Alvo";

    /// <summary>
    /// Registers everything the standalone host needs.
    /// </summary>
    /// <remarks>
    /// <paramref name="configureConfiguration"/> runs <em>before</em> Alvo is registered, because
    /// <c>AddAlvo</c>'s callback is eager: the descriptor path and the driver are read here, so a caller with
    /// its own configuration source has to contribute it before that read. A container passes nothing (the
    /// environment is already a source); a test passes its own collection.
    /// </remarks>
    /// <param name="args">The process arguments, bound as a configuration source by ASP.NET Core.</param>
    /// <param name="configureConfiguration">Adds configuration sources before Alvo is registered.</param>
    /// <returns>The builder, for a caller that wants to add logging or a test server.</returns>
    public static WebApplicationBuilder CreateBuilder(
        string[] args, Action<IConfigurationBuilder>? configureConfiguration = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configureConfiguration?.Invoke(builder.Configuration);

        builder.Services.Configure<AlvoHostOptions>(builder.Configuration.GetSection(ConfigurationSection));
        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection(AuthSection));
        builder.Services.AddHealthChecks();
        builder.Services.AddAlvo(alvo => Configure(alvo, builder.Configuration));

        return builder;
    }

    /// <summary>
    /// Builds the application, applies the mounted descriptor, and maps the generated Data API.
    /// </summary>
    /// <param name="builder">The builder <see cref="CreateBuilder"/> returned.</param>
    /// <param name="ct">Cancels the descriptor apply.</param>
    /// <returns>The started-but-not-yet-running application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static async Task<WebApplication> BuildAsync(
        WebApplicationBuilder builder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = builder.Build();
        app.MapAlvoLiveness();

        await app.Services.ApplyAlvoDescriptorAsync(ct: ct).ConfigureAwait(false);

        app.MapAlvoDataApi();
        return app;
    }

    private static void Configure(IAlvoBuilder alvo, ConfigurationManager configuration)
    {
        var options = HostOptions(configuration);
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
