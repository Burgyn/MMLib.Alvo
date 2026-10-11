using Microsoft.AspNetCore.Authentication.Cookies;
using MMLib.Alvo;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Your app's own users: an ordinary ASP.NET Core cookie, with its options set explicitly.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "fleet-desk";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
    });
builder.Services.AddAuthorization();

// Alvo, for agents and machines on /api/alvo, with its own API keys.
builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));
builder.Services.AddAlvo(alvo => alvo
    .UseSqlite("Data Source=alvo.db")
    .FromDescriptor("vehicles.alvo.json")
    .AddDataApi(api => api.RoutePrefix = "/api/alvo"));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// Development only: signs in a demo user by name. The roles come from this table on the server, never from the request.
if (app.Environment.IsDevelopment())
{
    var demoUsers = new Dictionary<string, (Guid Id, string[] Roles)>(StringComparer.OrdinalIgnoreCase)
    {
        ["inspector"] = (Guid.Parse("3f6b9c21-5a4d-4e88-9b2f-7c1a0d5e6f30"), ["inspector"]),
        ["clerk"] = (Guid.Parse("b8a45d17-2e93-4c60-8f1d-6a2b3c4d5e6f"), []),
    };

    app.MapPost("/app/login", (LoginRequest request) =>
    {
        if (!demoUsers.TryGetValue(request.User, out var user))
        {
            return Results.NotFound(new { known = demoUsers.Keys });
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), .. user.Roles.Select(role => new Claim(ClaimTypes.Role, role))],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: CookieAuthenticationDefaults.AuthenticationScheme);
    });
}

// A read, as the signed-in user: the rows are the ones the descriptor's list rule admits for them.
app.MapGet("/app/vehicles", async (HttpContext http, IAlvoData data, IRoleCatalogProvider roles, CancellationToken ct) =>
{
    if (roles.DeclaredRoles is not { } catalog)
    {
        return Results.Problem(detail: "Alvo has not applied a descriptor yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var caller = new AlvoContext
    {
        User = new UserId(userId),
        Roles = catalog.Resolve(["authenticated", .. http.User.FindAll(ClaimTypes.Role).Select(c => c.Value).Where(r => catalog.TryGet(r, out _))]),
    };

    try
    {
        var page = await data.QueryAsync(new AlvoQuery { Entity = "vehicles", Limit = 50 }, caller, ct);
        return Results.Ok(page.Items.Select(row => row.Values));
    }
    catch (AlvoAuthorizationException exception)
    {
        return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

// A write: the update rule admits admin or inspector, so a clerk is refused without a line of code here.
app.MapPatch("/app/vehicles/{id:guid}", async (Guid id, RepaintRequest request, HttpContext http, IAlvoData data, IRoleCatalogProvider roles, CancellationToken ct) =>
{
    if (roles.DeclaredRoles is not { } catalog)
    {
        return Results.Problem(detail: "Alvo has not applied a descriptor yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var caller = new AlvoContext
    {
        User = new UserId(userId),
        Roles = catalog.Resolve(["authenticated", .. http.User.FindAll(ClaimTypes.Role).Select(c => c.Value).Where(r => catalog.TryGet(r, out _))]),
    };

    try
    {
        var record = await data.UpdateAsync("vehicles", id, new Dictionary<string, object?> { ["color"] = request.Color }, caller, cancellationToken: ct);
        return Results.Ok(record.Values);
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
}).RequireAuthorization();

// Alvo's surface: health first (it takes no conventions), then the generated Data API.
app.MapAlvoHealth();
app.MapAlvoDataApi().WithTags("alvo-data-api");

app.Run();

record LoginRequest(string User);

record RepaintRequest(string Color);
