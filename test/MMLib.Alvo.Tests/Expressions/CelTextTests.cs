using CsCheck;

using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The one CEL writer (spec 2026-10-14 §4.3): what it writes reads back as what it was given — for every string over the
/// whole code space (HX-05 acceptance (3)), every number, and every canonical condition.
/// </summary>
public sealed class CelTextTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", @"'it\'s'")]
    [InlineData(@"back\slash", @"'back\\slash'")]
    [InlineData("line\nbreak\r\ttab", @"'line\nbreak\r\ttab'")]
    [InlineData("\"double\"", "'\"double\"'")]
    [InlineData("čaj 中  ", "'čaj 中  '")]
    public void A_string_literal_escapes_exactly_what_the_lexer_unescapes(string value, string literal) =>
        CelText.StringLiteral(value).ShouldBe(literal);

    [Fact]
    public void Every_string_reads_back_as_itself()
    {
        var sampled = 0;
        LiteralText.Any().Sample(
            value =>
            {
                Interlocked.Increment(ref sampled);
                return CelParser.Parse(CelText.StringLiteral(value)) == new CelLiteral(CelValueType.String, value);
            },
            iter: 10_000);

        sampled.ShouldBe(10_000);
    }

    [Fact]
    public void Every_number_reads_back_as_its_own_text()
    {
        var numbers = Gen.OneOf(
            Gen.Long.Where(number => number != long.MinValue).Select(number => new CelLiteral(CelValueType.Int, number)),
            Gen.Decimal.Select(number => new CelLiteral(CelValueType.Decimal, number)));

        numbers.Sample(literal => CelText.Write(CelParser.Parse(CelText.Write(literal))) == CelText.Write(literal), iter: 5_000);
    }

    [Theory]
    [InlineData("new.status == 'resolved'")]
    [InlineData("changed(status) && new.status == 'resolved'")]
    [InlineData("(changed(status) || changed(total)) && new.is_paid")]
    [InlineData("a || b && c")]
    [InlineData("a && (b && c)")]
    [InlineData("!(new.is_paid && new.quantity > 1)")]
    [InlineData("!has(new.code)")]
    [InlineData("(new.quantity + 1) * 2 > 10")]
    [InlineData("new.quantity - -5 > 0")]
    [InlineData("'manager' in @user.roles")]
    [InlineData("new.owner_id == @user.id || old.tenant_id == @tenant.id")]
    [InlineData("string(new.owner_id) == '3fa85f64-5717-4562-b3fc-2c963f66afa6'")]
    [InlineData("new.due_at < timestamp('2026-01-01T00:00:00Z')")]
    [InlineData("startsWith(new.note, 'ab') || contains(new.note, 'x')")]
    [InlineData("new.total >= 10.0")]
    [InlineData("(a == b) == c")]
    [InlineData("a == (b == c)")]
    [InlineData("math.abs(new.total) > 1.5")]
    public void A_canonical_condition_is_written_back_as_itself(string condition) =>
        CelText.Write(CelParser.Parse(condition)).ShouldBe(condition);

    [Theory]
    [InlineData("a == 'x' || true")]
    [InlineData("old")]
    [InlineData("in")]
    [InlineData("1st")]
    [InlineData("")]
    public void A_field_name_that_is_not_a_cel_identifier_is_refused(string name)
    {
        CelText.IsFieldName(name).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => CelText.Write(new CelFieldRef(name, CelValueType.Null, CelRecordState.New)));
        Should.Throw<ArgumentException>(() => CelText.Write(new CelChanged(name)));
    }

    [Theory]
    [InlineData("x) || true || f")]
    [InlineData("has")]
    [InlineData("changed")]
    [InlineData("math.")]
    [InlineData("")]
    public void A_call_name_that_does_not_read_back_as_the_same_call_is_refused(string name) =>
        Should.Throw<ArgumentException>(() => CelText.Write(new CelCall(name, [new CelLiteral(CelValueType.Int, 1L)])));

    [Fact]
    public void A_decimal_always_carries_a_point_so_it_never_reads_back_as_an_integer() =>
        CelText.Write(new CelLiteral(CelValueType.Decimal, 10m)).ShouldBe("10.0");

    [Fact]
    public void The_smallest_long_has_no_spelling() =>
        Should.Throw<ArgumentOutOfRangeException>(() => CelText.Write(new CelLiteral(CelValueType.Int, long.MinValue)));

    [Fact]
    public void A_literal_the_lexer_has_no_spelling_for_is_refused() =>
        Should.Throw<ArgumentException>(() => CelText.Write(new CelLiteral(CelValueType.Uuid, Guid.Empty)));

    [Fact]
    public void A_conditional_has_no_canonical_text() =>
        Should.Throw<ArgumentException>(() => CelText.Write(CelParser.Parse("a ? b : c")));
}
