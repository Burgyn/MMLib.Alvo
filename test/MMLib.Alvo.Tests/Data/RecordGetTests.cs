using CsCheck;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary><c>Get&lt;T&gt;</c>: one field as a host type, converted exactly as <c>As&lt;T&gt;()</c> converts a mapped property.</summary>
public sealed class RecordGetTests
{
    private const string Seed = "0N0XIzNsQ0O2";

    private static readonly Action<object>[] _targets =
    [
        SameAs<long>, SameAs<int>, SameAs<decimal>, SameAs<Guid>, SameAs<DateOnly>, SameAs<DateTimeOffset>, SameAs<string>,
        SameAs<bool>, SameAs<OrderStatus>, SameAs<long?>, SameAs<decimal?>, SameAs<Guid?>, SameAs<DateOnly?>, SameAs<OrderStatus?>,
    ];

    private static readonly Gen<object> _raw = Gen.OneOf(
        Gen.Long.Select(value => (object)value),
        Gen.Int.Select(value => (object)value),
        Gen.Decimal.Select(value => (object)value),
        Gen.Double.Select(value => (object)value),
        Gen.Bool.Select(value => (object)value),
        Gen.Guid.Select(value => (object)value),
        Gen.Guid.Select(value => (object)value.ToString()),
        Gen.DateTime.Select(value => (object)value),
        Gen.DateTime.Select(value => (object)value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
        Gen.DateTime.Select(value => (object)value.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
        Gen.OneOfConst<object>("ok", "draft", "in_review", "archived", "", "secret-text", OrderStatus.Approved, 12L, 12.5m));

    [Fact]
    public void Get_converts_every_held_type_as_As_does() =>
        Gen.Select(_raw, Gen.Int[0, _targets.Length - 1]).Sample((raw, target) => _targets[target](raw), iter: 3000, seed: Seed);

    [Fact]
    public void A_missing_field_is_named()
    {
        var failure = Should.Throw<InvalidOperationException>(() => Record(("other", 1L)).Get<long>("total"));

        failure.Message.ShouldContain("'total'");
        failure.Message.ShouldContain("no field");
    }

    [Fact]
    public void A_null_answers_null_for_a_nullable_target_and_throws_for_a_value_type()
    {
        var record = Record(("total", null));

        record.Get<decimal?>("total").ShouldBeNull();
        record.Get<string>("total").ShouldBeNull();
        var failure = Should.Throw<InvalidOperationException>(() => record.Get<decimal>("total"));
        failure.Message.ShouldContain("'total' is null");
        failure.Message.ShouldContain("Decimal");
    }

    [Fact]
    public void An_enum_reads_from_its_declared_value()
    {
        Record(("status", "ok")).Get<OrderStatus>("status").ShouldBe(OrderStatus.Approved);
        Record(("status", "in_review")).Get<OrderStatus?>("status").ShouldBe(OrderStatus.InReview);
        Record(("status", OrderStatus.Draft)).Get<OrderStatus>("status").ShouldBe(OrderStatus.Draft);
    }

    [Fact]
    public void An_id_a_day_and_an_instant_read_from_their_text()
    {
        var id = Guid.NewGuid();
        var record = Record(("id", id.ToString()), ("day", "2026-11-03"), ("at", "2026-11-02T08:30:00Z"), ("count", 3L));

        record.Get<Guid>("id").ShouldBe(id);
        record.Get<DateOnly>("day").ShouldBe(new DateOnly(2026, 11, 3));
        record.Get<DateTimeOffset?>("at").ShouldBe(new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero));
        record.Get<int>("count").ShouldBe(3);
    }

    [Fact]
    public void The_message_never_echoes_the_value()
    {
        var failure = Should.Throw<InvalidOperationException>(() => Record(("id", "secret-text")).Get<Guid>("id"));

        failure.Message.ShouldContain("'id'");
        failure.Message.ShouldContain("Guid");
        failure.Message.ShouldNotContain("secret-text");
    }

    [Fact]
    public void An_enum_value_without_a_member_is_refused_without_echoing_it()
    {
        var failure = Should.Throw<InvalidOperationException>(() => Record(("status", "archived")).Get<OrderStatus>("status"));

        failure.Message.ShouldContain("'status'");
        failure.Message.ShouldNotContain("archived");
    }

    [Fact]
    public void A_blank_field_name_is_refused() =>
        Should.Throw<ArgumentException>(() => Record(("a", 1L)).Get<long>(" "));

    private static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal));

    private static void SameAs<T>(object raw)
    {
        var record = Record(("value", raw));
        var expected = Outcome(() => record.As<Probe<T>>().Value);
        var actual = Outcome(() => record.Get<T>("value"));

        actual.ShouldBe(expected, $"{raw.GetType().Name} {raw} as {typeof(T).Name}");
    }

    private static (bool Read, object? Value) Outcome<T>(Func<T> read)
    {
        try
        {
            return (true, read());
        }
        catch (InvalidOperationException)
        {
            return (false, null);
        }
    }

    public sealed class Probe<T>
    {
        [AlvoField("value")]
        public T Value { get; set; } = default!;
    }
}
