using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Reads;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The field-service world with one narrowing and one enricher on work orders — its own world, so the others' lanes and counts stay.</summary>
public sealed class ReadsWorld : AdminWorld
{
    internal const string Narrowing = "own-region";
    internal const string Enricher = "order-label";

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo.Entity("work_orders", orders => orders
        .Narrow(Narrowing, (QueryNarrowing _) => null)
        .Enrich(Enricher, (Enrich<AlvoRecord> _) => { }));
}
