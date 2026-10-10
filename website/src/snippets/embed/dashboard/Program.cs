using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Admin;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Identity;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));
builder.Services.AddAlvo(alvo => alvo
    .UseSqlite("Data Source=alvo.db")
    .FromDescriptor("vehicles.alvo.json")
    .AddDataApi(api => api.RoutePrefix = "/api/alvo"));

// The dashboard: local accounts and their cookie sign-in, the dashboard itself, and the schema assistant.
builder.Services.AddAlvoIdentity(
    store => store.UseSqlite("Data Source=alvo-identity.db"),
    identity => builder.Configuration.GetSection(AlvoIdentity.ConfigurationSection).Bind(identity));
builder.Services.AddAlvoIdentityCookieSignIn(AlvoAdmin.SignInPath);
builder.Services.AddAlvoAdmin(admin => builder.Configuration.GetSection(AlvoAdmin.ConfigurationSection).Bind(admin));
builder.Services.AddAlvoAi();
builder.Services.AddScoped<IAlvoAdminCallerResolver, AdminCallerResolver>();

var app = builder.Build();

app.MapStaticAssets();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAlvoHealth();
app.MapAlvoDataApi();
app.MapAlvoAdmin();

app.Run();

// The one port between the dashboard and the identity package: the signed-in operator becomes the caller Alvo judges.
sealed class AdminCallerResolver(IServiceProvider services) : IAlvoAdminCallerResolver
{
    public ValueTask<AlvoPrincipal?> ResolveAsync(ClaimsPrincipal signedIn, CancellationToken cancellationToken)
    {
        var subject = signedIn.FindFirstValue(ClaimTypes.NameIdentifier);
        if (subject is not { Length: > 0 })
        {
            return ValueTask.FromResult<AlvoPrincipal?>(null);
        }

        var resolver = services.GetRequiredKeyedService<IAlvoContextResolver>(AlvoIdentity.ResolverKey);
        return resolver.ResolveAsync(subject, requestedTenant: null, cancellationToken);
    }
}
