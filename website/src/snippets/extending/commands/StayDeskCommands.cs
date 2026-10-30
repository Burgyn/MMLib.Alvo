using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Api;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;

namespace StayDesk;

[AlvoEntity("stays")]
public sealed class Stay
{
    public Guid Id { get; init; }

    public Guid GuestId { get; init; }

    public required string Room { get; init; }

    public StayStatus Status { get; init; }

    public long Nights { get; init; }
}

public enum StayStatus
{
    Requested,
    Confirmed,
    Cancelled,
}

public interface IPaymentProvider
{
    Task<string> ChargeAsync(Guid stay, decimal amount, CancellationToken cancellationToken);
}

public interface IGuestMessenger
{
    Task RemindAsync(Guid stay, CancellationToken cancellationToken);
}

public sealed record QuoteRequest(int Nights);

public sealed record Quote(decimal Total);

public sealed record PaymentNotice(Guid StayId, string Reference, decimal Amount);

public static class StayDeskCommands
{
    public const decimal NightlyRate = 80m;

    public static IAlvoBuilder AddStayDesk(this IAlvoBuilder alvo) =>
        alvo.AddCommand<RemindGuest>("remind_guest", caller => caller.HasRole("admin"));

    public static WebApplication MapStayDesk(this WebApplication app)
    {
        app.MapAlvoCommand<Stay>(
            "confirm_stay",
            "/commands/stays/{id:guid}/confirm",
            (stay, caller) => stay.Status == StayStatus.Requested && caller.HasRole("staff"),
            ConfirmAsync);

        app.MapAlvoCommand(
            "cancel_stay",
            "/commands/stays/{id:guid}/cancel",
            "stays",
            "status == 'requested' && (guest_id == @user.id || 'staff' in @user.roles)",
            CancelAsync);

        app.MapAlvoCommand("quote_stay", "/commands/quote", caller => caller.HasRole("guest") || caller.HasRole("staff"),
            (QuoteRequest request) => TypedResults.Ok(new Quote(request.Nights * NightlyRate)));

        app.MapAlvoWebhookReceiver("/hooks/payments", "payment-provider", RecordPaymentAsync);

        return app;
    }

    private static async Task<IResult> ConfirmAsync(Guid id, AlvoCommandContext ctx, IPaymentProvider payments, CancellationToken ct)
    {
        var stay = ctx.Record!.As<Stay>();
        var amount = stay.Nights * NightlyRate;
        var reference = await payments.ChargeAsync(id, amount, ct);

        await ctx.TransactAsync(async (unit, token) =>
        {
            await unit.UpdateAsync(id, AlvoPatch.For<Stay>().Set(x => x.Status, StayStatus.Confirmed), cancellationToken: token);
            await unit.CreateAsync("payments", Payment(id, reference, amount), token);
        }, cancellationToken: ct);

        return TypedResults.Ok(new { stay = id, reference });
    }

    private static async Task<IResult> CancelAsync(Guid id, AlvoCommandContext ctx, CancellationToken ct)
    {
        await ctx.TransactAsync(
            (unit, token) => unit.AsSystem().UpdateAsync(id, AlvoPatch.For<Stay>().Set(x => x.Status, StayStatus.Cancelled), cancellationToken: token),
            cancellationToken: ct);

        return TypedResults.NoContent();
    }

    private static async Task<IResult> RecordPaymentAsync(PaymentNotice notice, AlvoCaller caller, IAlvoTransactions transactions, CancellationToken ct)
    {
        await transactions.TransactAsync(caller.Context, async (unit, token) =>
        {
            try
            {
                await unit.CreateAsync("payments", Payment(notice.StayId, notice.Reference, notice.Amount), token);
            }
            catch (AlvoConstraintViolationException recorded) when (recorded.Kind == AlvoConstraintKind.Unique)
            {
                // The provider sent this payment before: it is already recorded.
            }
        }, cancellationToken: ct);

        return TypedResults.Ok();
    }

    private static Dictionary<string, object?> Payment(Guid stay, string reference, decimal amount) =>
        new() { ["stay_id"] = stay, ["reference"] = reference, ["amount"] = amount };
}

public sealed class RemindGuest(IGuestMessenger messenger) : IAlvoCommand<Guid>
{
    public Task HandleAsync(Guid input, AlvoCommandContext context, CancellationToken cancellationToken) =>
        messenger.RemindAsync(input, cancellationToken);
}
