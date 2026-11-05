using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using System.Globalization;

namespace MMLib.Alvo.Load.Host;

/// <summary>
/// The hooks the calibration measures, each selected by the work order's reference prefix so one host serves every
/// shape: <c>WO-81…</c> a before-write hook that does nothing, <c>WO-82…</c> one that reads the customer through the
/// write's transaction, <c>WO-83…</c> an after-write hook that does nothing. <c>WO-80…</c> matches none and measures the
/// write's own unit; the standalone image's <c>WO-9…</c> creates measure a write with no hook at all. <c>WO-89…</c> is
/// never measured: a before-write hook that refuses with 409, which <c>scripts/test-load</c> posts once before the hooks
/// arm, so an image that serves no gated hook voids the arm instead of publishing a hook-free number as a hook's cost.
/// </summary>
internal static class CalibrationHooks
{
    internal static IAlvoBuilder AddCalibrationHooks(this IAlvoBuilder alvo) =>
        alvo.Entity("work_orders", orders => orders
            .BeforeWrite("calibration-noop", (BeforeWrite<AlvoRecord> _) => { }, when: "startsWith(new.reference, 'WO-81')")
            .BeforeWrite("calibration-read", ReadCustomerAsync, when: "startsWith(new.reference, 'WO-82')")
            .AfterWrite("calibration-after", (AfterWrite<AlvoRecord> _) => { }, when: "startsWith(new.reference, 'WO-83')")
            .BeforeWrite("calibration-probe", RefuseProbe, when: "startsWith(new.reference, 'WO-89')"));

    private static void RefuseProbe(BeforeWrite<AlvoRecord> write) =>
        write.Conflict("calibration-probe", "The hooks image is serving its gated calibration hooks.");

    private static async Task ReadCustomerAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
    {
        var customer = write.New["customer_id"] switch
        {
            Guid id => id,
            string text => Guid.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("calibration-read needs the work order's customer_id."),
        };

        _ = await write.Transaction.GetAsync("customers", customer, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("calibration-read found no customer, so it measured nothing.");
    }
}
