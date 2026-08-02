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

        foreach (var value in statement.Parameters.Values.Select(bound => bound.Value).Where(value => value is not null))
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
        statement.Parameters[PolicyParameterPrefix.RowId].Value.ShouldBe(id);
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

    /// <summary>
    /// The caller's filter can only ever narrow: it arrives as one more parenthesised term <c>AND</c>-ed onto
    /// a fully parenthesised policy predicate, so nothing a caller supplies reaches the policy term's nesting
    /// level, let alone gets <c>OR</c>-ed beside it.
    /// </summary>
    [Fact]
    public void The_callers_filter_is_one_more_and_ed_term_and_never_replaces_the_policy_predicate()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions
        {
            Filter = new AlvoComparison("status", AlvoFilterOperator.Eq, "open"),
        });

        statement.Sql.ShouldContain("@alvo_u0");
        statement.Sql.ShouldContain("AND (\"status\" = @alvo_f0)");
        statement.Parameters[PolicyParameterPrefix.Filter + "0"].Value.ShouldBe("open");
    }

    [Fact]
    public void A_keyset_cursor_is_one_more_and_ed_term_too()
    {
        var anchor = new KeysetAnchor([new AlvoSort("plate")], ["ACME-001"], Guid.NewGuid());
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions { Anchor = anchor });

        statement.Sql.ShouldContain("AND ((\"plate\" > @alvo_k0 OR (\"plate\" = @alvo_k0 AND \"id\" > @alvo_k1)))");
        statement.Parameters[PolicyParameterPrefix.Keyset + "0"].Value.ShouldBe("ACME-001");
    }

    /// <summary>
    /// The reason the prefixes are reserved: four fragments number their parameters from zero independently,
    /// so in one statement they must still be pairwise disjoint.
    /// </summary>
    [Fact]
    public void Every_fragment_of_one_statement_binds_under_its_own_reserved_prefix()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions
        {
            RowId = Guid.NewGuid(),
            Filter = new AlvoComparison("status", AlvoFilterOperator.Eq, "open"),
            Anchor = new KeysetAnchor([new AlvoSort("plate")], ["ACME-001"], Guid.NewGuid()),
        });

        statement.Parameters.Keys.ShouldBeUnique();
        statement.Parameters.Keys.ShouldContain(PolicyParameterPrefix.Using + "0");
        statement.Parameters.Keys.ShouldContain(PolicyParameterPrefix.TenantScope + "0");
        statement.Parameters.Keys.ShouldContain(PolicyParameterPrefix.Filter + "0");
        statement.Parameters.Keys.ShouldContain(PolicyParameterPrefix.Keyset + "0");
        statement.Parameters.Keys.ShouldContain(PolicyParameterPrefix.RowId);
    }

    /// <summary>
    /// The <c>ORDER BY</c> lives in this statement rather than in a LINQ chain composed over it, and that is
    /// the whole point: it is rendered through the same <see cref="IFieldSqlRenderer"/> the keyset boundary
    /// is, so the page's order and the page's boundary cannot describe different sequences. Composed in LINQ
    /// they could — EF's own SQLite <c>ORDER BY</c> over a decimal collates exactly, while the boundary
    /// compares the repaired value.
    /// </summary>
    [Fact]
    public void A_sorted_read_carries_its_order_by_inside_the_one_statement()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions
        {
            Sort = [new AlvoSort("plate", Descending: true)],
        });

        statement.Sql.ShouldContain(
            " ORDER BY \"plate\" DESC, \"id\"");
    }

    /// <summary>
    /// A <c>LIMIT</c> composed in LINQ would sit <em>outside</em> the derived table EF wraps a
    /// <c>FromSql</c> root in, whose row order is not guaranteed to survive — so the limit would truncate an
    /// unordered set. Inside the one statement it truncates the ordered, policy-filtered one.
    /// </summary>
    [Fact]
    public void A_limited_read_orders_before_it_truncates_and_binds_the_limit()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions { Limit = 5 });

        statement.Sql.ShouldEndWith(" ORDER BY \"id\" LIMIT @alvo_limit");
        statement.Parameters[PolicyParameterPrefix.RowLimit].Value.ShouldBe(5);
    }

    [Fact]
    public void An_offset_read_binds_both_markers_in_one_window_clause()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions { Limit = 5, Offset = 3 });

        statement.Sql.ShouldEndWith(" ORDER BY \"id\" LIMIT @alvo_limit OFFSET @alvo_offset");
        statement.Parameters[PolicyParameterPrefix.RowLimit].Value.ShouldBe(5);
        statement.Parameters[PolicyParameterPrefix.RowOffset].Value.ShouldBe(3);
    }

    /// <summary>
    /// SQLite's grammar makes <c>OFFSET</c> a sub-clause of <c>LIMIT</c> and rejects a bare one, so a
    /// caller who asks for an offset with no explicit <see cref="AlvoQuery.Limit"/> still gets a
    /// <c>LIMIT</c> rendered — bound to a sentinel large enough that no real page is ever bounded by it,
    /// rather than left out or bound to a negative value PostgreSQL's own <c>LIMIT</c> refuses.
    /// </summary>
    [Fact]
    public void An_offset_with_no_caller_supplied_limit_still_renders_a_window_clause()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions { Offset = 5 });

        statement.Sql.ShouldEndWith(" ORDER BY \"id\" LIMIT @alvo_limit OFFSET @alvo_offset");
        statement.Parameters[PolicyParameterPrefix.RowLimit].Value.ShouldBe(int.MaxValue);
        statement.Parameters[PolicyParameterPrefix.RowOffset].Value.ShouldBe(5);
    }

    [Fact]
    public void An_unsorted_unlimited_first_page_needs_no_ordering_at_all()
    {
        var statement = Compose(new ReadStatementComposer.ReadStatementOptions());

        statement.Sql.ShouldNotContain("ORDER BY");
        statement.Sql.ShouldNotContain("LIMIT");
    }

    /// <summary>
    /// A cursor's boundary is only meaningful against a total order, so a cursored page is ordered even when
    /// the caller named no sort key at all — otherwise the engine's own row order decides which rows the
    /// boundary excludes, and two pages of one query can skip or repeat a row.
    /// </summary>
    [Fact]
    public void A_cursored_page_is_ordered_even_with_no_caller_sort_key()
        => Compose(new ReadStatementComposer.ReadStatementOptions
        {
            Anchor = new KeysetAnchor([], [], Guid.NewGuid()),
        }).Sql.ShouldContain(" ORDER BY \"id\"");

    /// <summary>
    /// A <c>WITH CHECK</c> verdict is reached over the complete stored row, so the pre-image read asks for
    /// the masked field's real value: reading it as a projected <c>NULL</c> would silently change what a rule
    /// referencing it decides. Masking is applied to what is <em>returned</em>, not to what is judged.
    /// </summary>
    [Fact]
    public void An_unmasked_read_projects_a_hidden_fields_real_column()
    {
        var descriptor = SnapshotFixture.VehicleWith(list: "owner_id == @user.id", hiddenFields: "secret_note");
        var statement = Compose(
            new ReadStatementComposer.ReadStatementOptions { Unmasked = true }, descriptor);

        statement.Sql.ShouldContain("\"secret_note\"");
        statement.Sql.ShouldNotContain("CAST(NULL AS");
    }

    [Fact]
    public void A_masked_read_is_still_the_default()
        => Compose(
                new ReadStatementComposer.ReadStatementOptions(),
                SnapshotFixture.VehicleWith(list: "owner_id == @user.id", hiddenFields: "secret_note"))
            .Sql.ShouldContain("CAST(NULL AS TEXT) AS \"secret_note\"");

    /// <summary>
    /// Both engines put the locking clause after <c>ORDER BY</c>/<c>LIMIT</c>, so the composer appends it
    /// last however many clauses precede it — a lock spelled before <c>ORDER BY</c> is a syntax error in the
    /// one statement a <c>WITH CHECK</c> verdict is based on.
    /// </summary>
    [Fact]
    public void The_row_lock_is_the_last_clause_however_many_others_precede_it()
        => Compose(new ReadStatementComposer.ReadStatementOptions
        {
            Sort = [new AlvoSort("plate")],
            Limit = 1,
            LockFor = PreImageMutation.Update,
        }).Sql.ShouldEndWith(" ORDER BY \"plate\", \"id\" LIMIT @alvo_limit FOR TEST");

    [Fact]
    public void A_filter_naming_an_undeclared_field_is_refused_before_any_statement_exists()
        => Should.Throw<AlvoAuthorizationException>(() => Compose(new ReadStatementComposer.ReadStatementOptions
        {
            Filter = new AlvoComparison("nope\"; DROP TABLE vehicle; --", AlvoFilterOperator.Eq, "x"),
        }));

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
