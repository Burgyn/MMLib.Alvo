using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field-service world with a stepped handler on new regions, capped per tenant, whose third step always fails, and a
/// debounced handler on changed customers whose waiting delivery a newer change supersedes.
/// </summary>
/// <remarks>Its own world rather than <see cref="OperationsWorld"/>'s: a failing handler there would add rows to the list its scenarios read first.</remarks>
public sealed class DeliveryStepsWorld : AdminWorld
{
    internal const string ConfirmRegion = "confirm-region";

    internal const string RenameCustomer = "rename-customer";

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .Entity("regions", regions => regions
            .OnCreated(ConfirmRegion, ThreeSteps, configure: options => options.Concurrency = AlvoConcurrency.PerKey("tenant", 2)))
        .Entity("customers", customers => customers
            .OnUpdated(RenameCustomer, (AlvoUpdated<AlvoRecord> _) => { }, configure: options => options.Debounce = TimeSpan.FromHours(1)));

    private static async Task ThreeSteps(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken)
    {
        await context.Steps.RunAsync("charge-deposit", _ => Task.FromResult("deposit-1"), cancellationToken);
        await context.Steps.RunAsync("send-confirmation", _ => Task.FromResult("email-1"), cancellationToken);
        await context.Steps.RunAsync("notify-dispatch", _ => Task.FromException<string>(new InvalidOperationException("dispatch is down")), cancellationToken);
    }
}
