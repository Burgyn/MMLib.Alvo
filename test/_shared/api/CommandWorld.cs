using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>What the command facts' handlers saw and did.</summary>
internal sealed class CommandProbe
{
    private int _runs;

    internal int Runs => Volatile.Read(ref _runs);

    internal ConcurrentQueue<string> Completions { get; } = new();

    internal bool? UnitOpenDuringExternalCall { get; set; }

    internal Exception? Refusal { get; set; }

    internal Exception? ContextDataRefusal { get; set; }

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ShiftedClock Clock { get; } = new();

    internal int Ran() => Interlocked.Increment(ref _runs);
}

/// <summary>The system clock, moved forward on demand — so a claim's 15-minute lease can run out within a fact.</summary>
internal sealed class ShiftedClock : TimeProvider
{
    private long _shift;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _shift));

    internal void Advance(TimeSpan by) => Interlocked.Add(ref _shift, by.Ticks);
}

/// <summary>
/// A stand-in for a payment provider: an external call, made before the command's unit. Its own data read is what tells
/// it whether a unit is open — <see cref="IAlvoData"/> refuses every call made while one is.
/// </summary>
internal sealed class PaymentGateway(CommandProbe probe, IAlvoData data)
{
    internal async Task ChargeAsync(Guid booking, AlvoContext caller, CancellationToken ct)
    {
        try
        {
            _ = await data.GetAsync("bookings", booking, caller, ct);
            probe.UnitOpenDuringExternalCall = false;
        }
        catch (AlvoUnitInProgressException)
        {
            probe.UnitOpenDuringExternalCall = true;
            throw;
        }
    }
}

/// <summary>A stay quote's input and answer.</summary>
public sealed record StayRequest(int Nights);

/// <summary>A stay quote's answer.</summary>
public sealed record Quote(decimal Total);

/// <summary>
/// The command-desk world: its descriptor, its keys and its commands. It maps Alvo's problem details, because a TestServer
/// rethrows an unhandled exception to the client instead of answering 500.
/// </summary>
internal static class CommandWorld
{
    internal static TestApiKey Staff { get; } = new("staff-key", ["staff", "authenticated"], ["*:read", "*:write"]);
    internal static TestApiKey OtherStaff { get; } = new("other-staff-key", ["staff", "authenticated"], ["*:read", "*:write"]);
    internal static TestApiKey Guest { get; } = new("guest-key", ["guest", "authenticated"], ["*:read", "*:write"]);
    internal static TestApiKey ReadOnlyStaff { get; } = new("read-only-staff-key", ["staff", "authenticated"], ["bookings:read"]);
    internal static TestApiKey WriteOnlyStaff { get; } = new("write-only-staff-key", ["staff", "authenticated"], ["bookings:write"]);
    internal static TestApiKey StaffAsGuest { get; } = new("staff-as-guest-key", ["guest", "authenticated"], ["*:read", "*:write"]) { User = Staff.User };
    internal const string BlockedTenant = "0b0e0c2a-1b1b-4c4c-8d8d-9e9e9e9e9e9e";

    internal static Task<AlvoApiWorld> StartAsync(AlvoApiEngine engine, CommandProbe probe, Action<IAlvoBuilder>? alvo = null) =>
        AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [Staff, OtherStaff, Guest, ReadOnlyStaff, WriteOnlyStaff, StaffAsGuest], new AlvoApiWorldSetup(
            MapAlvoProblemDetails: true,
            ConfigureServices: services =>
            {
                services.AddSingleton(probe);
                services.AddSingleton<TimeProvider>(probe.Clock);
                services.AddSingleton<PaymentGateway>();
                services.Configure<AlvoEventOptions>(options => options.DispatchMode = AlvoEventDispatchMode.Manual);
            },
            ConfigureAlvo: builder =>
            {
                Record(builder, probe, "record-confirm", "confirm_booking");
                Record(builder, probe, "record-quote", "quote_stay");
                Record(builder, probe, "record-fail-in", "misuse.fail_in_unit");
                Record(builder, probe, "record-fail-after", "misuse.fail_after_unit");
                alvo?.Invoke(builder);
            },
            ConfigureApp: Map),
            engine);

    internal static async Task<Guid> BookAsync(AlvoApiWorld world, Guid guest, string status = "requested")
    {
        using var created = await world.SendAsync(HttpMethod.Post, "/api/bookings", Staff, body: new JsonObject
        {
            ["guest_id"] = guest,
            ["room"] = "12",
            ["status"] = status,
            ["nights"] = 2,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    internal static async Task<int> NotesAsync(AlvoApiWorld world)
    {
        using var notes = await world.SendAsync(HttpMethod.Get, "/api/booking_notes", Staff);
        return (await notes.ReadItemsAsync()).Count;
    }

    internal static KeyValuePair<string, string>[] Key(string key) => [new("Idempotency-Key", key)];

    private static void Record(IAlvoBuilder builder, CommandProbe probe, string id, string command) =>
        builder.AddEventHandler(id, $"alvo.command.{command}.completed", (AlvoRecord payload) =>
        {
            probe.Completions.Enqueue((string)payload["command"]!);
            return Task.CompletedTask;
        });

    private static void Map(WebApplication app)
    {
        app.MapAlvoCommand("confirm_booking", "/commands/bookings/{id:guid}/confirm", "bookings", "'staff' in @user.roles && status == 'requested'", ConfirmAsync);
        app.MapAlvoCommand("quote_stay", "/commands/quote", caller => caller.HasRole("guest") || caller.HasRole("staff"), QuoteStay);
        app.MapAlvoCommand("hold.quote", "/commands/hold", "'staff' in @user.roles", HoldAsync);
        app.MapAlvoCommand("guarded.not_blocked", "/commands/guarded", $"!(string(@tenant.id) == '{BlockedTenant}')", QuoteStay);
        app.MapAlvoCommand("misuse.data_port", "/commands/misuse/data-port", "'staff' in @user.roles", DataPortInsideUnitAsync);
        app.MapAlvoCommand("misuse.two_units", "/commands/misuse/two-units", "'staff' in @user.roles", TwoUnitsAsync);
        app.MapAlvoCommand("misuse.fail_in_unit", "/commands/misuse/fail-in-unit", "'staff' in @user.roles", FailInUnitAsync);
        app.MapAlvoCommand("misuse.fail_after_unit", "/commands/misuse/fail-after-unit", "'staff' in @user.roles", FailAfterUnitAsync);
    }

    private static async Task<IResult> ConfirmAsync(Guid id, AlvoCommandContext ctx, PaymentGateway payments, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        await payments.ChargeAsync(id, ctx.Caller, ct);
        var note = await ctx.TransactAsync(async (unit, token) =>
        {
            await unit.UpdateAsync("bookings", id, new Dictionary<string, object?> { ["status"] = "confirmed" }, cancellationToken: token);
            return await unit.CreateAsync("booking_notes", new Dictionary<string, object?> { ["booking_id"] = id, ["text"] = "confirmed" }, token);
        }, cancellationToken: ct);

        return TypedResults.Ok(new { booking = id, note = (Guid)note["id"]! });
    }

    private static Ok<Quote> QuoteStay(StayRequest request, CommandProbe probe)
    {
        probe.Ran();
        return TypedResults.Ok(new Quote(request.Nights * 80m));
    }

    private static async Task<IResult> HoldAsync(StayRequest request, CommandProbe probe, CancellationToken ct)
    {
        if (probe.Ran() > 1)
        {
            return TypedResults.Ok(new Quote(request.Nights));
        }

        probe.Started.TrySetResult();
        await probe.Release.Task.WaitAsync(ct);
        return TypedResults.Ok(new Quote(request.Nights));
    }

    private static async Task<IResult> DataPortInsideUnitAsync(AlvoCommandContext ctx, IAlvoData data, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        try
        {
            await ctx.TransactAsync(
                async (_, token) =>
                {
                    probe.ContextDataRefusal = await RefusalOfAsync(() => ctx.Data.CreateAsync("booking_notes", Note(), token));
                    await data.CreateAsync("booking_notes", Note(), ctx.Caller, cancellationToken: token);
                },
                cancellationToken: ct);
        }
        catch (InvalidOperationException refusal)
        {
            probe.Refusal = refusal;
            throw;
        }

        return TypedResults.Ok();
    }

    private static async Task<IResult> TwoUnitsAsync(AlvoCommandContext ctx, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        await ctx.TransactAsync((unit, token) => unit.CreateAsync("booking_notes", Note(), token), cancellationToken: ct);
        try
        {
            await ctx.TransactAsync((unit, token) => unit.CreateAsync("booking_notes", Note(), token), cancellationToken: ct);
        }
        catch (InvalidOperationException refusal)
        {
            probe.Refusal = refusal;
        }

        return TypedResults.Ok();
    }

    private static async Task<IResult> FailInUnitAsync(AlvoCommandContext ctx, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        await ctx.TransactAsync(async (unit, token) =>
        {
            await unit.CreateAsync("booking_notes", Note(), token);
            throw new InvalidOperationException("the handler failed inside its unit");
        }, cancellationToken: ct);

        return TypedResults.Ok();
    }

    private static async Task<IResult> FailAfterUnitAsync(AlvoCommandContext ctx, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        await ctx.TransactAsync((unit, token) => unit.CreateAsync("booking_notes", Note(), token), cancellationToken: ct);
        throw new InvalidOperationException("the handler failed after its unit committed");
    }

    private static async Task<Exception?> RefusalOfAsync(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (Exception refusal)
        {
            return refusal;
        }
    }

    private static Dictionary<string, object?> Note() => new() { ["booking_id"] = Guid.NewGuid(), ["text"] = "note" };
}
