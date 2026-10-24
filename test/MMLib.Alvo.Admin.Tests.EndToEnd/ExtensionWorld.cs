using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field-service world with two event handlers registered as a host developer would: one on <c>work_orders</c>
/// reading <c>scheduled_for</c>, which nothing in the descriptor names, and one on every entity.
/// </summary>
/// <remarks>Registered through <see cref="AdminWorld"/>'s test-side builder seam; the shipped host gains none.</remarks>
public sealed class ExtensionWorld : AdminWorld
{
    internal const string Reschedule = "notify-reschedule";
    internal const string Audit = "audit-every-save";
    internal const string RescheduleDescription = "Tells the dispatcher when a work order is rescheduled.";

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .Entity("work_orders", orders => orders.OnUpdated(
            Reschedule,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(scheduled_for)",
            configure: options => options.Description = RescheduleDescription))
        .Entity("*", every => every.OnSaved(
            Audit,
            (AlvoSaved<AlvoRecord> _) => Task.CompletedTask,
            configure: options => options.Description = "Keeps an audit line for every saved record."));
}
