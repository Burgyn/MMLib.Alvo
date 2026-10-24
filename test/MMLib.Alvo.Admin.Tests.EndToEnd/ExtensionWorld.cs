using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field-service world with three event handlers registered as a host developer would: one on <c>work_orders</c>
/// reading <c>scheduled_for</c>, which nothing in the descriptor names, a typed one on <c>work_orders</c> whose
/// condition is a lambda, and one on every entity.
/// </summary>
/// <remarks>Registered through <see cref="AdminWorld"/>'s test-side builder seam; the shipped host gains none.</remarks>
public sealed class ExtensionWorld : AdminWorld
{
    internal const string Reschedule = "notify-reschedule";
    internal const string Audit = "audit-every-save";
    internal const string Completed = "notify-completed";
    internal const string RescheduleDescription = "Tells the dispatcher when a work order is rescheduled.";

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .Entity("work_orders", orders => orders.OnUpdated(
            Reschedule,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(scheduled_for)",
            configure: options => options.Description = RescheduleDescription))
        .Entity<WorkOrder>(orders => orders.OnUpdated(
            Completed,
            (AlvoUpdated<WorkOrder> _) => Task.CompletedTask,
            when: e => e.Changed(x => x.Status) && e.New.Status == WorkOrderStatus.Completed))
        .Entity("*", every => every.OnSaved(
            Audit,
            (AlvoSaved<AlvoRecord> _) => Task.CompletedTask,
            configure: options => options.Description = "Keeps an audit line for every saved record."));
}

/// <summary>The part of a field-service work order the typed handler maps.</summary>
[AlvoEntity("work_orders")]
public sealed class WorkOrder
{
    public required string Reference { get; init; }

    public WorkOrderStatus Status { get; init; }
}

/// <summary>A work order's life cycle, as <c>examples/field-service</c> declares it.</summary>
public enum WorkOrderStatus
{
    Scheduled,
    InProgress,
    Completed,
    Cancelled,
}
