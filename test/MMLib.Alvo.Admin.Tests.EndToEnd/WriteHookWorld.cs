using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Hooks;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The field-service world with one write hook of three stages on work orders — its own world, so the extension world's counts stay.</summary>
public sealed class WriteHookWorld : AdminWorld
{
    internal const string Check = "check-region";
    internal const string Stamp = "stamp-dispatcher";
    internal const string Tidy = "tidy-notes";

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo.Entity("work_orders", orders => orders
        .Validate(Check, (ValidateWrite<AlvoRecord> _) => ValueTask.CompletedTask, configure: o => o.Description = "Checks the region with the dispatch service.")
        .BeforeWrite(Stamp, (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask, when: "changed(status)", configure: Roomy)
        .AfterDelete(Tidy, (AfterDelete<AlvoRecord> _) => ValueTask.CompletedTask, configure: Roomy));

    /// <summary>The longest in-transaction timeout, so a no-op hook cannot time out on a loaded runner mid-scenario.</summary>
    private static void Roomy(AlvoHookOptions options) => options.Timeout = TimeSpan.FromSeconds(30);
}
