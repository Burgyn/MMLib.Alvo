using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The binder's own dispatch, asserted where no database is needed to see it. A bound
/// <see cref="DbParameter"/> is a value object the provider hands back from a <em>closed</em> connection, so
/// every branch this class takes — column or no column, mapped or unmapped, value or <see langword="null"/> —
/// is observable from the parameter alone.
/// </summary>
/// <remarks>
/// Deliberately not a thinner copy of <c>SqliteParameterBindingTests</c>: that suite proves the bound
/// parameter <em>finds the row it wrote</em>, which needs a real engine and is the reason the binder exists.
/// What is asserted here is the decision tree in front of that — which mapping is consulted, and what happens
/// when there is none — which a round trip cannot distinguish anyway and which had no coverage at all in this
/// assembly.
/// </remarks>
public sealed class PredicateParameterBinderTests : IDisposable
{
    private const string Name = "alvo_u0";

    private readonly AlvoDataContext _context = NewContext();

    /// <summary>
    /// A value with no column behind it is bound through the mapping <em>its own CLR type</em> has, so it
    /// reaches ADO.NET with EF's <see cref="DbType"/> rather than the <c>String</c> a provider infers for an
    /// unmapped value — the silent misrepresentation this class exists to prevent.
    /// </summary>
    /// <param name="value">A value a rendered policy predicate can carry.</param>
    /// <param name="expected">The type EF's own mapping chooses for it.</param>
    [Theory]
    [InlineData("ACME-001", DbType.String)]
    [InlineData(42L, DbType.Int64)]
    [InlineData(true, DbType.Boolean)]
    public void A_value_without_a_column_binds_through_its_own_types_mapping(object value, DbType expected)
        => BindWithoutColumn(value).DbType.ShouldBe(expected);

    /// <summary>
    /// The same fact for the one type whose hand-formatted spelling matches no row on SQLite, stated
    /// separately because an attribute argument cannot be a <see cref="Guid"/>.
    /// </summary>
    [Fact]
    public void A_guid_without_a_column_binds_through_efs_own_guid_mapping()
        => BindWithoutColumn(Guid.NewGuid()).DbType.ShouldBe(DbType.Guid);

    /// <summary>
    /// A <see langword="null"/> has no CLR type to take a mapping from, so it takes the untyped path and
    /// reaches ADO.NET as the null sentinel rather than as a .NET <see langword="null"/> the provider would
    /// reject.
    /// </summary>
    [Fact]
    public void A_null_without_a_column_binds_as_the_ado_net_null_sentinel()
        => BindWithoutColumn(null).Value.ShouldBe(DBNull.Value);

    /// <summary>
    /// A value whose type the provider has no mapping for is refused loudly. Handing it to ADO.NET and
    /// letting the driver infer a type is exactly the silent misrepresentation the binder exists to prevent,
    /// so the untyped path admits <see langword="null"/> and nothing else.
    /// </summary>
    [Fact]
    public void A_value_with_no_relational_mapping_is_refused_rather_than_inferred()
        => Should.Throw<InvalidOperationException>(() => BindWithoutColumn(new object()));

    /// <summary>
    /// A value compared against a column is bound through <em>that column's</em> mapping, not its own: a
    /// <c>uuid</c> column compared against a value that arrived as a <see cref="string"/> matches nothing and
    /// raises nothing when bound the other way round.
    /// </summary>
    [Fact]
    public void A_value_with_a_column_binds_through_the_columns_mapping_rather_than_its_own()
        => BindThroughColumn("owner_id", Guid.NewGuid().ToString("D"))
            .DbType.ShouldBe(DbType.Guid);

    /// <summary>
    /// The column path's real advantage: a <see langword="null"/> still carries the <em>column's</em> type,
    /// where the column-less path has none to take it from.
    /// </summary>
    [Fact]
    public void A_null_with_a_column_still_carries_the_columns_type()
        => BindThroughColumn("created_at", null).DbType.ShouldBe(DbType.DateTimeOffset);

    /// <summary>
    /// Every parameter is bound nullable, on both paths. Every property of the read model is optional
    /// regardless of the column's own nullability, so a parameter declared non-nullable would be describing a
    /// shape this model never has.
    /// </summary>
    [Fact]
    public void Every_bound_parameter_is_nullable_on_both_paths()
    {
        BindThroughColumn("plate", "ACME-001").IsNullable.ShouldBeTrue();
        BindWithoutColumn("ACME-001").IsNullable.ShouldBeTrue();
    }

    /// <summary>
    /// A value naming a field this read model does not map has no column to bind through, so it is refused
    /// rather than falling back to the value's own type — the fallback that would silently reintroduce the
    /// defect the origin-tagged shape exists to prevent. The refusal names the field, because the only reader
    /// of this message is whoever composed the fragment.
    /// </summary>
    [Fact]
    public void A_column_this_read_model_does_not_map_is_refused_and_the_refusal_names_it()
        => Should.Throw<InvalidOperationException>(() => BindThroughColumn("no_such_field", "x"))
            .Message.ShouldContain("no_such_field");

    /// <summary>
    /// A statement's values are bound in one call, dispatched on where each came from, and no name is lost —
    /// disjoint prefixes exist so several fragments can be merged without one value overwriting another.
    /// </summary>
    [Fact]
    public void A_statements_values_bind_in_one_call_without_losing_a_name()
        => Bind(new Dictionary<string, BoundValue>(StringComparer.Ordinal)
        {
            ["alvo_u0"] = BoundValue.FromPolicyPredicate(Guid.NewGuid()),
            ["alvo_f0"] = BoundValue.ForColumn("plate", "ACME-001"),
            ["alvo_limit"] = BoundValue.FromFramework(5),
        }).Select(parameter => parameter.ParameterName)
            .ShouldBe(["@alvo_u0", "@alvo_f0", "@alvo_limit"], ignoreOrder: true);

    /// <summary>
    /// A column-less value still needs a parameter name: an unnamed parameter cannot be referenced by the
    /// statement that carries it, so it is a broken caller rather than a refused one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_column_less_value_with_no_parameter_name_is_a_broken_caller(string name)
        => Should.Throw<ArgumentException>(() => Bind(
            new Dictionary<string, BoundValue>(StringComparer.Ordinal)
            {
                [name] = BoundValue.FromPolicyPredicate("ACME-001"),
            }));

    [Fact]
    public void The_context_is_required()
        => Should.Throw<ArgumentNullException>(() => new PredicateParameterBinder(null!));

    /// <summary>
    /// Both arguments are a broken caller rather than a refused one, and each refusal names the argument it
    /// is about — which is the only thing that distinguishes the guard from the incidental throw the first
    /// dereference would produce anyway.
    /// </summary>
    [Fact]
    public void The_read_model_and_the_values_are_both_required()
    {
        var binder = new PredicateParameterBinder(_context);
        var values = new Dictionary<string, BoundValue>(StringComparer.Ordinal)
        {
            [Name] = BoundValue.ForColumn("plate", "ACME-001"),
        };

        Should.Throw<ArgumentNullException>(() => binder.Bind(null!, values)).ParamName.ShouldBe("rows");
        Should.Throw<ArgumentNullException>(() => binder.Bind(Rows(), null!)).ParamName.ShouldBe("parameters");
    }

    public void Dispose() => _context.Dispose();

    private DbParameter BindWithoutColumn(object? value)
        => Bound(BoundValue.FromPolicyPredicate(value));

    private DbParameter BindThroughColumn(string field, object? value)
        => Bound(BoundValue.ForColumn(field, value));

    private DbParameter Bound(BoundValue bound)
        => Bind(new Dictionary<string, BoundValue>(StringComparer.Ordinal) { [Name] = bound })[0];

    private DbParameter[] Bind(IReadOnlyDictionary<string, BoundValue> values)
        => new PredicateParameterBinder(_context).Bind(Rows(), values);

    private IEntityType Rows() => _context.Model.FindEntityType(AlvoDataFixtures.Vehicle.Name)!;

    /// <summary>
    /// A context over the canonical fixture whose connection is never opened. A model is metadata and a
    /// bound parameter is a value object, so neither needs a database — which is the whole reason these
    /// facts can live in the cheap assembly.
    /// </summary>
    private static AlvoDataContext NewContext()
    {
        var options = new DbContextOptionsBuilder();
        options.UseSqlite("Data Source=:memory:", static sqlite => sqlite.UseRelationalNulls());

        return new AlvoDataContext(options.Options, new SchemaModel([AlvoDataFixtures.Vehicle]), Guid.NewGuid());
    }
}
