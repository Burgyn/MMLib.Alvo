using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Samples.FieldService;

/// <summary>
/// The field-service embedded host: the advanced demo's second layer, over the same descriptor the standalone stack serves.
/// </summary>
/// <remarks>
/// <para>
/// <b>The descriptor does almost everything.</b> State machine, gapless job numbers, the SLA timer, CHECKs, the partial
/// unique index, the visit calendar's exclusion, the relation rule, the lookup check, the rollup and the billing webhook
/// all live in <c>examples/field-service/field-service.alvo.json</c> and run unchanged in the standalone image. This host
/// adds what that file has no construct for — a two-entity transaction, an inbound webhook, a time-zone-aware hour, a
/// subscriber for a timer's event, a per-tenant job — and each slice names its own reason in its remarks (README.md
/// tabulates them).
/// </para>
/// <para>
/// <b>The shape is <c>CreateBuilder</c> + <c>Build</c></b>, as in <c>MMLib.Alvo.Samples.EmbeddedHost</c>, so a test can
/// compose the host without reaching the entry point through reflection. <c>Program.cs</c> is one line over it.
/// </para>
/// </remarks>
public static class FieldServiceHost
{
    /// <summary>The connection string this host stores its records under (<c>ConnectionStrings:FieldService</c>).</summary>
    public const string ConnectionName = "FieldService";

    /// <summary>Builds the host, registering Alvo through its one entry point.</summary>
    /// <param name="args">The process arguments.</param>
    /// <exception cref="InvalidOperationException">No PostgreSQL connection string is configured.</exception>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = FieldServiceSetup.ReadOptions(builder.Configuration);
        var connectionString = builder.Configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionName} is not set. This host runs on PostgreSQL only: set "
                + $"ConnectionStrings__{ConnectionName} (docker-compose.field-service-host.yml does).");

        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));
        builder.Services.AddAlvoProblemDetails();
        builder.Services.AddFieldServiceServices(builder.Configuration);
        builder.Services.AddAlvo(alvo => alvo
            .UsePostgreSql(connectionString)
            .FromDescriptor(DescriptorPath(builder, options))
            .AddDataApi()
            .AddFieldService(options));

        return builder;
    }

    /// <summary>Maps Alvo's routes and this host's own, and returns the application unstarted.</summary>
    /// <param name="builder">The builder <see cref="CreateBuilder"/> produced.</param>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAlvo();
        app.MapFieldService();

        return app;
    }

    /// <summary>Builds, maps and runs.</summary>
    /// <param name="args">The process arguments.</param>
    public static Task RunAsync(string[] args) => Build(CreateBuilder(args)).RunAsync();

    /// <summary>The configured descriptor, or the repository's own field-service descriptor above the content root.</summary>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="options">The host's settings.</param>
    /// <exception cref="InvalidOperationException">No path is configured and the repository's file was not found.</exception>
    private static string DescriptorPath(WebApplicationBuilder builder, FieldServiceOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DescriptorPath))
        {
            return options.DescriptorPath;
        }

        for (var directory = new DirectoryInfo(builder.Environment.ContentRootPath); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "examples", "field-service", "field-service.alvo.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "examples/field-service/field-service.alvo.json was not found above the content root. Run this sample from "
            + "inside the MMLib.Alvo repository, or set FieldService:DescriptorPath.");
    }
}
