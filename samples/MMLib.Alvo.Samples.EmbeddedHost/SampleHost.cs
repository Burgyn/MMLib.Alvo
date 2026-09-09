using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using System.Security.Claims;

namespace MMLib.Alvo.Samples.EmbeddedHost;

/// <summary>
/// Alvo embedded: somebody else's app — a fleet desk — that keeps its records in Alvo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds of caller reach the same backend two different ways, and that is the thing to understand
/// before anything else here.</b>
/// </para>
/// <list type="bullet">
///   <item>
///   <c>/app/*</c> — this app's own cookie users. These endpoints resolve <see cref="IAlvoData"/> and build
///   an <see cref="AlvoContext"/> from the cookie's claims, so <em>Alvo's policy engine</em> decides what
///   they may do. This app writes no authorization logic of its own.
///   </item>
///   <item>
///   <c>/api/alvo/*</c> — Alvo's generated Data API, for agents and machines, with Alvo's own API-key
///   credential.
///   </item>
/// </list>
/// <para>
/// Both serve <c>examples/vehicle-registry/vehicles.alvo.json</c> — the same descriptor the repository's
/// root <c>docker-compose.yml</c> mounts into the standalone image. That is spec §"Spoločné kontrakty"
/// point 2: one descriptor, several doors, identical result.
/// </para>
/// <para>
/// <b>The shape is <c>CreateBuilder</c> + <c>Build</c>, not a top-level <c>Program</c></b>, for the same
/// reason <c>MMLib.Alvo.Host</c> has it: a test can compose the host and swap the server without the entry
/// point being reachable only through reflection. <c>Program.cs</c> is two lines over it.
/// </para>
/// </remarks>
public static class SampleHost
{
    /// <summary>The configuration section this app reads its own settings from.</summary>
    private const string SampleSection = "FleetDesk";

    /// <summary>Every signed-in caller holds this, and the descriptor's read rules key on it.</summary>
    private const string AuthenticatedRole = "authenticated";

    /// <summary>Builds the host, registering Alvo through its one entry point.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="configure">
    /// Anything a caller adds to the configuration — how the suite supplies the dev key's secret and a
    /// database of its own, both of which this app deliberately has no default for.
    /// </param>
    public static WebApplicationBuilder CreateBuilder(
        string[] args, Action<IConfigurationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder.Configuration);

        // This app's own authentication. Nothing about it is Alvo's: an embedded host owns its pipeline,
        // and Alvo never adds authentication, authorization or routing middleware on a host's behalf.
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.Cookie.Name = "fleet-desk");
        builder.Services.AddAuthorization();

        // Alvo's own credential for the /api/alvo surface. The secret is deliberately absent from
        // appsettings.json: AlvoAuthOptionsValidator refuses to start without one, so the sample cannot
        // ship a working default credential (spec §"Spoločné kontrakty" point 5). Supply it with
        //   dotnet user-secrets set "Alvo:Auth:DevKeys:0:Secret" "<at least 32 characters>"
        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));

        // The one entry point (extensibility.md rule 1). UseSqlite selects infrastructure, FromDescriptor
        // supplies domain input, AddDataApi configures the generated HTTP surface — three verbs from the
        // fixed taxonomy, and nothing else to learn.
        //
        // There is deliberately no ApplyAlvoDescriptorAsync call: AddAlvo registers a hosted lifecycle
        // service that brings the schema up before anything else starts, so /health/ready gating on it is
        // the whole story. A host that applied the descriptor itself would duplicate that boot.
        builder.Services.AddAlvo(alvo => alvo
            .UseSqlite($"Data Source={DatabasePath(builder)}")
            .FromDescriptor(DescriptorPath(builder))
            .AddDataApi(api =>
            {
                // Alvo mounts BESIDE this app's own routes, never over them.
                api.RoutePrefix = "/api/alvo";

                // RequireJsonContentType is left at its default (true), and this is the context that
                // default exists for: a body-taking route with no media-type requirement is reachable as a
                // CORS simple request, which stops being harmless the moment a host authenticates with
                // cookies. See docs/architecture/data-api.md, "Requiring a JSON Content-Type".
            }));

        // AddAlvoProblemDetails() is deliberately NOT called. An embedded host owns its own error
        // rendering, and Alvo taking over the shape of UseExceptionHandler's document inside somebody
        // else's application is worse than one explicit call (#119).
        return builder;
    }

    /// <summary>Maps this app's own endpoints and Alvo's, and returns the application unstarted.</summary>
    /// <param name="builder">The builder <see cref="CreateBuilder"/> produced.</param>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        MapAppEndpoints(app);
        MapAlvoEndpoints(app);

        return app;
    }

    /// <summary>Builds, maps and runs.</summary>
    /// <param name="args">The process arguments.</param>
    public static Task RunAsync(string[] args) => Build(CreateBuilder(args)).RunAsync();

    /// <summary>This app's own surface, for this app's own cookie users.</summary>
    /// <remarks>
    /// Every one of these calls <see cref="IAlvoData"/> with a caller it built itself. There is no HTTP
    /// round trip through <c>/api/alvo</c> and no second authorization model: the descriptor's
    /// <c>rules</c> are what decide, which is the single most useful thing this sample demonstrates.
    /// </remarks>
    /// <param name="app">The application to map onto.</param>
    private static void MapAppEndpoints(WebApplication app)
    {
        // A development sign-in. A real app has its own; all that matters here is that the cookie ends up
        // carrying a stable user id and the role names this app grants.
        app.MapPost("/app/login", (LoginRequest request, HttpContext http) =>
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, request.User.ToString()),
                    .. request.Roles.Select(role => new Claim(ClaimTypes.Role, role)),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);

            return http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        });

        // A read. `vehicles.list` admits any authenticated caller, so every signed-in user gets a page —
        // and the rows they get are the rows the descriptor's own predicate admits, not a set this file
        // filtered.
        app.MapGet("/app/vehicles", (
                HttpContext http, IAlvoData data, IRoleCatalogProvider roles, CancellationToken ct) =>
            AsCaller(http, roles, caller =>
                Answered(async () =>
                {
                    var page = await data.QueryAsync(
                        new AlvoQuery { Entity = "vehicles", Limit = 50 }, caller, ct);

                    return Results.Ok(page.Items.Select(row => row.Values));
                })))
            .RequireAuthorization();

        // A write, and the demonstration. `vehicles.update` admits admin or inspector, so a cookie user
        // holding `inspector` may repaint a vehicle and a plain authenticated one may not — a refusal
        // this file contains no code for.
        //
        // The request is this app's OWN type, mapped onto the port's field map here. That is what an
        // embedded host really does, and it is not only style: a Dictionary<string, object?> bound
        // straight from JSON carries JsonElement values, which IAlvoData has no field type for and
        // refuses as unbindable. A host owns its wire contract; Alvo owns the field map.
        app.MapPatch("/app/vehicles/{id:guid}", (
                Guid id,
                RepaintRequest request,
                HttpContext http,
                IAlvoData data,
                IRoleCatalogProvider roles,
                CancellationToken ct) =>
            AsCaller(http, roles, caller =>
                Answered(async () =>
                {
                    var record = await data.UpdateAsync(
                        "vehicles",
                        id,
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["color"] = request.Color },
                        caller,
                        cancellationToken: ct);

                    return Results.Ok(record.Values);
                })))
            .RequireAuthorization();
    }

    /// <summary>Alvo's own surface, for agents and machines.</summary>
    /// <remarks>
    /// <b>Health first, then the Data API, and the order is part of the seam rather than a preference.</b>
    /// <c>MapAlvoDataApi</c> refuses a host whose Data API services are absent, and an operator facing that
    /// refusal needs a container that can still be probed (extensibility.md rule 10).
    /// </remarks>
    /// <param name="app">The application to map onto.</param>
    private static void MapAlvoEndpoints(WebApplication app)
    {
        app.MapAlvoHealth();

        // MapAlvoDataApi returns an IEndpointConventionBuilder over Alvo's generated routes and nothing
        // else (#182), so a host attaches its own concerns to them without the framework owning any of
        // them. Health is deliberately not chainable: one builder over the probes *and* the data would let
        // an authorization policy reach /health/live, and a container probe presents no credential.
        app.MapAlvoDataApi().WithTags("alvo-data-api");
    }

    /// <summary>
    /// Runs <paramref name="handler"/> as the cookie user, or refuses when there is no usable caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Application roles can only be minted through the catalog the applied descriptor primed</b>, which
    /// is why this goes through <see cref="IRoleCatalogProvider"/> rather than naming roles directly: a
    /// typo is rejected where it arrives instead of silently matching no rule. A role this app grants that
    /// the descriptor does not declare is dropped rather than fatal — an app's own role vocabulary is
    /// bigger than its backend's, and only the overlap means anything to Alvo's rules.
    /// </para>
    /// <para>
    /// <b>The two ways this can fail are answered differently, deliberately.</b>
    /// <c>DeclaredRoles</c> is <see langword="null"/> until a descriptor is applied, and its contract is to
    /// fail closed on that — but it is a <em>boot</em> problem, so it earns a 503 rather than a 401 that
    /// would send an operator to check a cookie. A cookie with no usable user id is the 401.
    /// </para>
    /// </remarks>
    /// <param name="http">The current request.</param>
    /// <param name="roles">The catalog the applied descriptor primed.</param>
    /// <param name="handler">What to do as the resolved caller.</param>
    private static Task<IResult> AsCaller(
        HttpContext http, IRoleCatalogProvider roles, Func<AlvoContext, Task<IResult>> handler)
    {
        if (roles.DeclaredRoles is not { } catalog)
        {
            return Task.FromResult(Results.Problem(
                detail: "Alvo has not applied a descriptor yet, so no role can be resolved.",
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is not { } id
            || !Guid.TryParse(id, out var userId))
        {
            return Task.FromResult(Results.Unauthorized());
        }

        var declared = http.User.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(name => catalog.TryGet(name, out _));

        return handler(new AlvoContext
        {
            User = new UserId(userId),
            Roles = catalog.Resolve([AuthenticatedRole, .. declared]),
        });
    }

    /// <summary>
    /// Renders the refusals <see cref="IAlvoData"/> raises, so this app answers with its own documents.
    /// </summary>
    /// <remarks>
    /// <b>Both endpoints go through it, and the uniformity is the point.</b> <c>QueryAsync</c> raises
    /// <see cref="AlvoAuthorizationException"/> for a denied <c>list</c> exactly as <c>UpdateAsync</c> does
    /// for a denied <c>update</c>, so a helper wrapped around one call and not the other ships a 500 waiting
    /// for the first reader whose descriptor has a narrower read rule than this one's. The families come
    /// from <c>IAlvoData</c>'s own table; <see cref="InvalidOperationException"/> is deliberately not caught,
    /// because it means an invariant Alvo relies on is broken and this app's logging should see it.
    /// </remarks>
    /// <param name="operation">The port call to make.</param>
    private static async Task<IResult> Answered(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (AlvoAuthorizationException exception)
        {
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (AlvoRecordNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>Where this app keeps its SQLite file.</summary>
    /// <remarks>
    /// Configurable because the suite runs several hosts and each needs a database of its own; the default
    /// is a file beside the app, which is what makes <c>dotnet run</c> work with no setup.
    /// </remarks>
    /// <param name="builder">The builder being configured.</param>
    private static string DatabasePath(WebApplicationBuilder builder) =>
        builder.Configuration[$"{SampleSection}:DatabasePath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "fleet-desk.db");

    /// <summary>
    /// The repository's own vehicle-registry descriptor — the same file the standalone image mounts.
    /// </summary>
    /// <param name="builder">The builder being configured.</param>
    /// <exception cref="InvalidOperationException">The descriptor was not found above the content root.</exception>
    private static string DescriptorPath(WebApplicationBuilder builder)
    {
        if (builder.Configuration[$"{SampleSection}:DescriptorPath"] is { } configured)
        {
            return configured;
        }

        var directory = new DirectoryInfo(builder.Environment.ContentRootPath);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "examples", "vehicle-registry", "vehicles.alvo.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "examples/vehicle-registry/vehicles.alvo.json was not found above the content root. This "
            + "sample runs from inside the MMLib.Alvo repository: `dotnet run --project "
            + "samples/MMLib.Alvo.Samples.EmbeddedHost`. Set FleetDesk:DescriptorPath to run it elsewhere.");
    }
}

/// <summary>A repaint: the one field this app lets a cookie user change on a vehicle.</summary>
/// <param name="Color">The vehicle's new colour.</param>
public sealed record RepaintRequest(string Color);

/// <summary>A development sign-in request.</summary>
/// <param name="User">The user id the cookie will carry.</param>
/// <param name="Roles">The role names this app grants the user.</param>
public sealed record LoginRequest(Guid User, IReadOnlyList<string> Roles);
