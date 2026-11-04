using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using MMLib.Alvo;
using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using System.Threading.RateLimiting;

namespace FrontDesk;

public sealed record CallerView(Guid User, string Actor);

public sealed record AuditedWrite(string Entity);

public static class FrontDeskEndpoints
{
    public const string RoomsPolicy = "rooms";

    public const string PerEntityPolicy = "per-entity";

    public static void AddFrontDeskLimits(this RateLimiterOptions limiter)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.AddFixedWindowLimiter(RoomsPolicy, window =>
        {
            window.PermitLimit = 2;
            window.Window = TimeSpan.FromMinutes(1);
            window.QueueLimit = 0;
        });
        limiter.AddPerEntityLimit();
    }

    public static void AddPerEntityLimit(this RateLimiterOptions limiter) =>
        limiter.AddPolicy(PerEntityPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
            http.GetEndpoint()?.Metadata.GetMetadata<AlvoEndpointMetadata>()?.Entity ?? "",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 2, Window = TimeSpan.FromMinutes(1) }));

    public static WebApplication MapFrontDesk(this WebApplication app)
    {
        app.MapAlvoDataApi()
            .ForEntity("rooms", rooms => rooms.RequireRateLimiting(RoomsPolicy))
            .ForEntity("guests", guests => guests.RequireRateLimiting(PerEntityPolicy))
            .ForEntity("bookings", bookings => bookings.RequireRateLimiting(PerEntityPolicy))
            .Add(endpoint =>
            {
                if (endpoint.Metadata.OfType<AlvoEndpointMetadata>().FirstOrDefault() is { Operation: not DataOperation.Get and not DataOperation.List } write)
                {
                    endpoint.Metadata.Add(new AuditedWrite(write.Entity));
                }
            });

        app.MapGet("/me", (AlvoCaller caller) => TypedResults.Ok(new CallerView(caller.Context.User.Value, caller.Context.ActorKind.ToString())))
            .WithAlvoCaller();

        app.MapGet("/me/rooms", async (AlvoCaller caller, IAlvoSessions sessions, CancellationToken ct) =>
                TypedResults.Ok((await sessions.For(caller.Context).QueryAsync(new AlvoQuery { Entity = "rooms" }, ct)).Items.Count))
            .WithAlvoCaller();

        return app;
    }
}
