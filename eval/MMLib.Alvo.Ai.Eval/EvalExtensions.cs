using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// The eval host's own C#: two event handlers — on <c>rental_fleet</c>, which no other case touches, and on the one
/// <c>rentals</c> field (<c>damage_report</c>) no other case reads — so the extension cases have something to name and
/// something to protect without changing what any other case grades.
/// </summary>
internal static class EvalExtensions
{
    internal const string OutOfService = "notify-desk-out-of-service";
    internal const string DamageClaim = "open-damage-claim";

    /// <summary>Registers both handlers on the shipped host's builder; the stored-descriptor boot binds them strictly.</summary>
    /// <param name="alvo">The host's Alvo builder.</param>
    internal static void Register(IAlvoBuilder alvo) => alvo
        .Entity("rental_fleet", fleet => fleet.OnUpdated(
            OutOfService,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(in_service) && new.in_service == false",
            configure: options => options.Description = "Tells the rental desk when a fleet bike is taken out of service."))
        .Entity("rentals", rentals => rentals.OnUpdated(
            DamageClaim,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(damage_report)",
            configure: options => options.Description = "Opens an insurance claim when a damage report is written on a rental."));
}
