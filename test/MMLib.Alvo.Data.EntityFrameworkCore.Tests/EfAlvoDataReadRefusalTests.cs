using MMLib.Alvo.Rules;
using MMLib.Alvo.Testing.Data;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The four refusals a read reaches before any row is fetched, in the order <see cref="EfAlvoData"/>'s own
/// contract fixes them: the policy denial and the reason it carries, the structural cap on a caller's
/// filter, and the two fail-closed arms for an entity the applied schema does not declare.
/// </summary>
/// <remarks>
/// Every one of these is asserted on a port that is otherwise fully wired, so a refusal cannot be an
/// accident of a half-built harness. None of them executes a statement — which is the property under test
/// in three of the four cases.
/// </remarks>
public class EfAlvoDataReadRefusalTests
{
    /// <summary>
    /// A denial carries the <b>policy's own</b> reason, not the generic unmapped-entity sentence. An
    /// operator reading a log needs to tell "no rule allows this operation" from "this entity is not in the
    /// applied schema", and those are the only two strings this port can produce for a refused read.
    /// </summary>
    [Fact]
    public async Task A_denied_operation_is_refused_with_the_policys_own_reason()
    {
        using var harness = EfAlvoDataHarness.Over(EfAlvoDataHarness.VehicleRuledBy(list: "true"));
        var denial = harness.DecisionFor(DataOperation.Get);
        denial.DenyReason.ShouldNotBe(AlvoDataContext.UnmappedEntityMessage);

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            async () => await harness.Data.GetAsync(
                AlvoDataFixtures.Vehicle.Name, Guid.NewGuid(), AlvoDataFixtures.Caller));

        refusal.Message.ShouldBe(denial.DenyReason);
    }

    /// <summary>
    /// The structural cap runs <b>before</b> the field-name check, and the order is the contract rather than
    /// an accident: the field check walks the whole tree to collect the names it validates, so a tree that is
    /// too wide to walk has to be refused first. The filter under test is both over the cap and made of
    /// undeclared names, so the refusal that arrives says which check ran — a malformed-argument
    /// <see cref="ArgumentException"/> naming <c>filter</c> and the limit, not a denial.
    /// </summary>
    [Fact]
    public async Task A_filter_past_the_term_limit_is_refused_before_its_field_names_are_walked()
    {
        using var harness = EfAlvoDataHarness.Over(EfAlvoDataHarness.VehicleRuledBy(list: "true"));
        var query = new AlvoQuery { Entity = AlvoDataFixtures.Vehicle.Name, Filter = TooManyUndeclaredTerms() };

        var refusal = await Should.ThrowAsync<ArgumentException>(
            async () => await harness.Data.QueryAsync(query, AlvoDataFixtures.Caller));

        refusal.ParamName.ShouldBe("filter");
        refusal.Message.ShouldContain(AlvoFilter.MaxTerms.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The catalog allows the entity and the applied schema does not declare it — the mismatch F7's dynamic
    /// registry makes reachable. A single-row read fails closed with the unmapped-entity refusal rather
    /// than raising anything that names the entity or the shape of the model.
    /// </summary>
    [Fact]
    public async Task A_get_for_an_entity_the_applied_schema_does_not_declare_fails_closed()
    {
        using var harness = EfAlvoDataHarness.OverAnEmptySchema(EfAlvoDataHarness.VehicleRuledBy(get: "true"));

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            async () => await harness.Data.GetAsync(
                AlvoDataFixtures.Vehicle.Name, Guid.NewGuid(), AlvoDataFixtures.Caller));

        refusal.Message.ShouldBe(AlvoDataContext.UnmappedEntityMessage);
    }

    /// <summary>
    /// The list read's arm of the same mismatch, and it is a longer walk: a query naming no field at all
    /// passes the field guard vacuously and composes its read options — including the unselected-field set,
    /// which has its own undeclared-entity arm — before the page itself fails closed.
    /// </summary>
    [Fact]
    public async Task A_list_for_an_entity_the_applied_schema_does_not_declare_fails_closed()
    {
        using var harness = EfAlvoDataHarness.OverAnEmptySchema(EfAlvoDataHarness.VehicleRuledBy(list: "true"));
        var query = new AlvoQuery { Entity = AlvoDataFixtures.Vehicle.Name };

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            async () => await harness.Data.QueryAsync(query, AlvoDataFixtures.Caller));

        refusal.Message.ShouldBe(AlvoDataContext.UnmappedEntityMessage);
    }

    /// <summary>
    /// One term past <see cref="AlvoFilter.MaxTerms"/>, flat, over a field the entity does not declare — so
    /// both the breadth cap and the field guard would refuse it, and which one does is observable.
    /// </summary>
    private static AlvoAnd TooManyUndeclaredTerms() => new(
        [.. Enumerable
            .Range(0, AlvoFilter.MaxTerms + 1)
            .Select(_ => new AlvoComparison("not_a_field", AlvoFilterOperator.Eq, "ACME"))]);
}
