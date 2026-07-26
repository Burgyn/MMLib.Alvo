using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class ReadStatementComposerTests
{
    [Fact]
    public void The_policy_predicate_and_the_tenant_scope_are_both_in_the_where_clause()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions());

        statement.Sql.ShouldStartWith("SELECT ");
        statement.Sql.ShouldContain(" FROM \"vehicle\" WHERE (");
        statement.Sql.ShouldContain("@alvo_u0");
        statement.Sql.ShouldContain("@alvo_t0");
        statement.Sql.ShouldContain(") AND (");
    }

    [Fact]
    public void The_predicate_parameter_families_never_share_a_name()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions());

        statement.Parameters.Keys.ShouldBeUnique();
        statement.Parameters.Keys.ShouldAllBe(name => name.StartsWith("alvo_", StringComparison.Ordinal));
    }

    [Fact]
    public void No_bound_value_appears_in_the_statement_text()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions());

        foreach (var value in statement.Parameters.Values.Where(value => value is not null))
        {
            statement.Sql.ShouldNotContain(value!.ToString()!, Case.Insensitive);
        }
    }

    [Fact]
    public void A_row_id_read_binds_the_id_and_can_take_the_dialects_row_lock()
    {
        var id = Guid.NewGuid();
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions
        {
            RowId = id,
            LockFor = PreImageMutation.Update,
        });

        statement.Sql.ShouldContain("\"id\" = @alvo_id");
        statement.Sql.ShouldEndWith(" FOR TEST");
        statement.Parameters[PolicyParameterPrefix.RowId].ShouldBe(id);
    }

    /// <summary>
    /// The clause is the mutation's, not one hint for both: a delete's pre-image needs the stronger lock,
    /// and the composer must pass the mutation through rather than pick a mode of its own.
    /// </summary>
    [Fact]
    public void A_deletes_pre_image_takes_the_dialects_delete_lock()
        => Compose(new ReadStatementComposer.ReadStatementOptions
        {
            RowId = Guid.NewGuid(),
            LockFor = PreImageMutation.Delete,
        }).Sql.ShouldEndWith(" FOR TEST DELETE");

    [Fact]
    public void A_list_read_takes_no_row_lock()
        => Compose(new ReadStatementComposer.ReadStatementOptions()).Sql.ShouldNotContain("FOR TEST");

    [Fact]
    public void A_list_read_binds_no_row_id()
        => Compose(new ReadStatementComposer.ReadStatementOptions())
            .Parameters.ShouldNotContainKey(PolicyParameterPrefix.RowId);

    /// <summary>
    /// The projection is part of the same statement, so a masked field is masked by the very string whose
    /// <c>WHERE</c> carries the policy predicate — one place to read, one place to snapshot.
    /// </summary>
    [Fact]
    public void A_masked_field_is_null_projected_in_the_same_statement()
    {
        var statement = Compose(
            new ReadStatementComposer.ReadStatementOptions(),
            SnapshotFixture.VehicleWith(list: "owner_id == @user.id", hiddenFields: "secret_note"));

        statement.Sql.ShouldContain("CAST(NULL AS TEXT) AS \"secret_note\"");
        statement.Sql.ShouldNotContain(", \"secret_note\",");
    }

    /// <summary>
    /// <c>create</c> carries no <c>USING</c>, and a <c>WHERE</c> clause with no term at all is a syntax
    /// error — so an absent predicate contributes the dialect's constant-true predicate, never nothing.
    /// </summary>
    [Fact]
    public void An_operation_with_no_using_predicate_still_composes_a_well_formed_where_clause()
    {
        var statement = Compose(
            new ReadStatementComposer.ReadStatementOptions(),
            SnapshotFixture.VehicleWith(create: "true"),
            DataOperation.Create);

        statement.Sql.ShouldContain(" WHERE (TRUE) AND (");
        statement.Sql.ShouldContain("@alvo_t0");
    }

    [Fact]
    public void Every_argument_is_required()
    {
        using var services = Services();
        var composer = new ReadStatementComposer(
            services.GetRequiredService<IPredicateRenderer>(), new TestFieldSqlRenderer(), new TestSqlDialect());
        var decision = SnapshotFixture.Decision(
            services, SnapshotFixture.VehicleWith(list: "owner_id == @user.id"), DataOperation.List);
        var rows = ReadModelFixture.Rows(AlvoDataFixtures.Vehicle);
        var options = new ReadStatementComposer.ReadStatementOptions();

        Should.Throw<ArgumentNullException>(
            () => composer.Compose(null!, decision, AlvoDataFixtures.Caller, options, rows));
        Should.Throw<ArgumentNullException>(
            () => composer.Compose(AlvoDataFixtures.Vehicle, null!, AlvoDataFixtures.Caller, options, rows));
        Should.Throw<ArgumentNullException>(
            () => composer.Compose(AlvoDataFixtures.Vehicle, decision, null!, options, rows));
        Should.Throw<ArgumentNullException>(
            () => composer.Compose(AlvoDataFixtures.Vehicle, decision, AlvoDataFixtures.Caller, null!, rows));
        Should.Throw<ArgumentNullException>(
            () => composer.Compose(AlvoDataFixtures.Vehicle, decision, AlvoDataFixtures.Caller, options, null!));
    }

    [Fact]
    public void The_composer_requires_all_three_of_its_collaborators()
    {
        using var services = Services();
        var predicates = services.GetRequiredService<IPredicateRenderer>();

        Should.Throw<ArgumentNullException>(() => new ReadStatementComposer(null!, new TestFieldSqlRenderer(), new TestSqlDialect()));
        Should.Throw<ArgumentNullException>(() => new ReadStatementComposer(predicates, null!, new TestSqlDialect()));
        Should.Throw<ArgumentNullException>(() => new ReadStatementComposer(predicates, new TestFieldSqlRenderer(), null!));
    }

    private static ReadStatement Compose(
        ReadStatementComposer.ReadStatementOptions options,
        AlvoDescriptor? descriptor = null,
        DataOperation operation = DataOperation.List)
    {
        using var services = Services();
        var composer = new ReadStatementComposer(
            services.GetRequiredService<IPredicateRenderer>(), new TestFieldSqlRenderer(), new TestSqlDialect());
        var decision = SnapshotFixture.Decision(
            services, descriptor ?? SnapshotFixture.VehicleWith(list: "owner_id == @user.id"), operation);

        return composer.Compose(
            AlvoDataFixtures.Vehicle,
            decision,
            AlvoDataFixtures.Caller,
            options,
            ReadModelFixture.Rows(AlvoDataFixtures.Vehicle));
    }

    private static ServiceProvider Services() => new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
}
