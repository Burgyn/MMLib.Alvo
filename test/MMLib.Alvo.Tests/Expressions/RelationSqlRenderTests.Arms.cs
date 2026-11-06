using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Every arm that reads a field accepts an outer one: a bare outer boolean, an outer decimal, outer timestamp arithmetic,
/// an outer date and <c>has(outer)</c> render in column mode and in image mode, and image mode binds the declared type.
/// </summary>
public sealed partial class RelationSqlRenderTests
{
    private static readonly Dictionary<string, (string Entity, string Source, AlvoRecord Row, Type? Bound)> _arms = new(StringComparer.Ordinal)
    {
        ["bool"] = ("projects", "project_members.exists(m, m.user_id == @user.id && archived)",
            Image(("id", _project.ToString()), ("archived", true)), typeof(bool)),
        ["decimal"] = ("orders", "customer_id.credit_limit >= amount",
            Image(("customer_id", _customer.ToString()), ("amount", 12.5d)), typeof(decimal)),
        ["timestamp"] = ("orders", "customer_id.since <= placed_at - duration('24h')",
            Image(("customer_id", _customer), ("placed_at", new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc))), typeof(DateTimeOffset)),
        ["date"] = ("orders", "customer_id.opened_on <= due_on",
            Image(("customer_id", _customer), ("due_on", new DateOnly(2026, 11, 9))), typeof(long)),
        ["has"] = ("projects", "project_members.exists(m, has(note) && m.note == note)",
            Image(("id", _project), ("note", "x")), typeof(string)),
    };

    private static readonly string[] _armNames = ["bool", "decimal", "timestamp", "date", "has"];
    private static readonly string[] _dialectNames = ["test", "tsql"];

    public static TheoryData<string, string> Arms() =>
        new(from arm in _armNames from dialect in _dialectNames select (arm, dialect));

    [Theory]
    [MemberData(nameof(Arms))]
    public Task An_outer_bool_decimal_and_timestamp_render_in_every_arm(string arm, string dialect)
    {
        var (entity, source, row, bound) = _arms[arm];
        var expression = _world.Bound(entity, source, CelFixtures.Alice);

        var column = Render(dialect, expression, CelFixtures.Alice, ReadOf(dialect, entity));
        var image = Probe(dialect, (CelRelation)expression.Root, entity, row);

        column.Sql.ShouldContain($"{(dialect == "tsql" ? "[" : "\"")}{entity}{(dialect == "tsql" ? "]" : "\"")}.");
        image.Parameters.Values.Select(value => value?.GetType()).ShouldContain(bound);
        return Verify(new { Column = Snapshot(column), Image = Snapshot(image) }).UseParameters(arm, dialect);
    }
}
