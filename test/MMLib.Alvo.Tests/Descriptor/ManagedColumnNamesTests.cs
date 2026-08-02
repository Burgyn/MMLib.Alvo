using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// The tie between "which columns the framework manages" and "why each one cannot be declared" — the fact
/// that stands in for a compile error C# will not give.
/// </summary>
/// <remarks>
/// <see cref="ManagedColumnNames"/> deliberately has no catch-all arm, because the catch-all it used to have
/// drifted the moment a fourth column existed: it told a <c>softDelete</c>-only entity that its
/// <c>deleted_at</c> was "part of the audit trail this entity asked for by declaring 'audit'", wrong in both
/// halves. Without a default, though, nothing in the language notices a managed column added to
/// <see cref="AlvoManagedColumns"/> and not here — a <c>switch</c> expression would throw at run time, which
/// is a worse failure than a wrong sentence. So the tie is this file.
/// </remarks>
public class ManagedColumnNamesTests
{
    /// <summary>
    /// Every framework-managed column has its own recorded reason, and nothing else does — asserted in
    /// <b>both</b> directions.
    /// </summary>
    /// <remarks>
    /// Set equality rather than "each managed column has an entry": a stale entry for a column that stopped
    /// being managed is the same defect one direction later, and it would leave prose nothing can reach.
    /// The traits are all set at once because that is what makes the set complete — <c>deleted_at</c> only
    /// appears under <c>softDelete</c>, and it is the one the old catch-all was wrong about.
    /// </remarks>
    [Fact]
    public void Every_managed_column_has_its_own_reason()
    {
        var managed = AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true);

        ManagedColumnNames.Explained.Order(StringComparer.Ordinal).ShouldBe(
            managed.Order(StringComparer.Ordinal),
            "a managed column with no reason would fall through to nothing, and a reason for a column that is "
            + "no longer managed is prose nothing can reach");
    }

    /// <summary>
    /// A name that is not managed has no reason, and asking for one <b>throws</b> rather than inventing prose.
    /// </summary>
    /// <remarks>
    /// This is the half that makes the absence of a catch-all safe rather than merely tidy: if the fact above
    /// is ever deleted along with a new column's entry, the failure is a loud exception naming the column
    /// instead of a confident sentence about the wrong one.
    /// </remarks>
    [Fact]
    public void A_name_with_no_recorded_reason_fails_loudly()
    {
        var failure = Should.Throw<InvalidOperationException>(() => ManagedColumnNames.Refusing("not_managed"));

        failure.Message.ShouldContain("not_managed");
        failure.Message.ShouldContain("no catch-all", Case.Sensitive);
    }

    /// <summary>
    /// Each reason names the column it is about, so no entry can be pasted from its neighbour — the exact
    /// mistake the catch-all made at scale.
    /// </summary>
    [Fact]
    public void Every_reason_names_its_own_column()
    {
        foreach (var column in ManagedColumnNames.Explained)
        {
            var (consequence, fix) = ManagedColumnNames.Refusing(column);

            consequence.ShouldContain($"'{column}'", Case.Sensitive, $"{column}'s consequence must be about it");
            fix.ShouldContain($"'{column}'", Case.Sensitive, $"{column}'s fix must be about it");
        }
    }

    /// <summary>
    /// The set a declaration is refused against is exactly what the framework injects — read from the ports
    /// rather than restated, so the two cannot disagree about which names are owned.
    /// </summary>
    /// <param name="audit">Whether the entity declares <c>audit</c>.</param>
    /// <param name="softDelete">Whether the entity declares <c>softDelete</c>.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_refused_set_is_the_injected_set(bool audit, bool softDelete)
    {
        ManagedColumnNames.InjectedFor(TenancyMode.Global, audit, softDelete).ShouldBe(
            AlvoManagedColumns.For(TenancyMode.Global, audit, softDelete));
    }
}
