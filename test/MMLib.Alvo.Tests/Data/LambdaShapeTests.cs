using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;

using System.Collections;
using System.Linq.Expressions;

namespace MMLib.Alvo.Tests.Data;

/// <summary>What both translators ask of a lambda's tree (spec 2026-10-14 §4.3): conversions, the three Contains shapes, constants, the refusal's shape.</summary>
public sealed class LambdaShapeTests
{
    private static readonly long[] _numbers = [1, 2];
    private static readonly OrderStatus[] _statuses = [OrderStatus.Draft];
    private static readonly decimal _limit = 5m;
    private static readonly HashSet<string> _codes = ["a"];
    private static readonly Stack<string> _stack = new(["a"]);
    private static readonly char[] _letters = ['a'];
    private static readonly int _big = 300;
    private static readonly int _three = 3;

    [Fact]
    public void A_lifting_or_widening_conversion_is_seen_through()
    {
        Expression<Func<Order, long?>> lifted = x => x.Rank;

        LambdaShape.Unwrapped(lifted.Body).ShouldBeAssignableTo<MemberExpression>();
    }

    [Fact]
    public void A_narrowing_conversion_is_kept()
    {
        Expression<Func<Order, int>> narrowed = x => (int)x.Total;

        LambdaShape.Unwrapped(narrowed.Body).NodeType.ShouldBe(ExpressionType.Convert);
    }

    [Fact]
    public void Every_contains_shape_names_its_list_and_its_value()
    {
        Expression<Func<Order, bool>>[] shapes =
        [
            x => _numbers.Contains(x.Quantity),
            x => _statuses.Contains(x.Status),
            x => new List<string> { "a" }.Contains(x.Note),
            x => _codes.Contains(x.Note),
        ];

        foreach (var shape in shapes)
        {
            LambdaShape.IsContains((MethodCallExpression)shape.Body, out var source, out var value).ShouldBeTrue(shape.ToString());
            typeof(IEnumerable).IsAssignableFrom(source!.Type).ShouldBeTrue(shape.ToString());
            LambdaShape.Touches(source, shape.Parameters[0]).ShouldBeFalse(shape.ToString());
            value.ShouldBeAssignableTo<MemberExpression>(shape.ToString());
        }
    }

    [Fact]
    public void A_text_s_own_contains_is_not_a_list()
    {
        Expression<Func<Order, bool>> text = x => x.Note.Contains("ab");

        LambdaShape.IsContains((MethodCallExpression)text.Body, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void An_instance_contains_on_another_collection_is_not_a_list()
    {
        Expression<Func<Order, bool>> stack = x => _stack.Contains(x.Note);

        LambdaShape.IsContains((MethodCallExpression)stack.Body, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_text_read_as_a_sequence_of_characters_is_not_a_list()
    {
        Expression<Func<Order, bool>> enumerable = x => Enumerable.Contains("ab", x.Note[0]);
        Expression<Func<Order, bool>> span = x => _letters.Contains(x.Note[0]);
        Expression<Func<Order, bool>> compared = x => _letters.Contains(x.Note[0], null);

        LambdaShape.IsContains((MethodCallExpression)enumerable.Body, out _, out _).ShouldBeFalse();
        LambdaShape.IsContains(OverText(span), out _, out _).ShouldBeFalse();
        LambdaShape.IsContains(OverText(compared), out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Reading_the_value_out_of_a_nullable_is_kept()
    {
        Expression<Func<Order, int>> unwrapped = x => (int)x.Rank!;

        LambdaShape.Unwrapped(unwrapped.Body).NodeType.ShouldBe(ExpressionType.Convert);
    }

    [Fact]
    public void A_built_in_conversion_of_a_constant_is_folded()
    {
        Expression<Func<decimal>> widened = () => _three;

        LambdaValues.TryConstant(widened.Body, out var value, out _).ShouldBeTrue();
        value.ShouldBe(3m);
    }

    [Fact]
    public void A_conversion_that_overflows_or_runs_host_code_is_refused_with_a_reason()
    {
        Expression<Func<byte>> overflow = () => checked((byte)_big);
        Expression<Func<Celsius>> custom = () => (Celsius)_limit;

        LambdaValues.TryConstant(overflow.Body, out _, out var why).ShouldBeFalse();
        why.ShouldContain("cannot be computed");
        LambdaValues.TryConstant(custom.Body, out _, out why).ShouldBeFalse();
        why.ShouldContain("user-defined");
    }

    [Fact]
    public void A_constant_is_a_literal_a_static_readonly_field_or_an_array_of_them()
    {
        Expression<Func<decimal>> field = () => _limit;
        Expression<Func<long[]>> array = () => new[] { 1L, 2L };

        LambdaValues.TryConstant(field.Body, out var limit, out _).ShouldBeTrue();
        limit.ShouldBe(5m);
        LambdaValues.TryConstant(array.Body, out var items, out _).ShouldBeTrue();
        items.ShouldBe(new object?[] { 1L, 2L });
    }

    [Fact]
    public void A_captured_variable_or_a_static_property_is_not_a_constant_and_says_why()
    {
        var captured = 3m;
        Expression<Func<decimal>> local = () => captured;
        Expression<Func<string>> property = () => Environment.NewLine;

        LambdaValues.TryConstant(local.Body, out _, out var why).ShouldBeFalse();
        why.ShouldContain("'captured' is a captured variable");
        LambdaValues.TryConstant(property.Body, out _, out why).ShouldBeFalse();
        why.ShouldContain("'NewLine' is a static property");
    }

    [Fact]
    public void A_clock_is_found_anywhere_in_a_value()
    {
        Expression<Func<DateTimeOffset>> later = () => DateTimeOffset.UtcNow.AddDays(1);

        LambdaShape.FindClock(later.Body).ShouldNotBeNull();
    }

    [Fact]
    public void A_refusal_names_the_construct_where_it_was_written_why_and_the_fix()
    {
        var refusal = TranslationRefusal.Query("orders", Expression.Constant(1), "because");

        refusal.Message.ShouldBe("Cannot translate '1' in a typed query on 'orders': because. Fix: " + TranslationRefusal.QueryFix);
        refusal.FixSuggestion.ShouldBe(TranslationRefusal.QueryFix);
    }

    private static MethodCallExpression OverText(Expression<Func<Order, bool>> shape)
    {
        var call = (MethodCallExpression)shape.Body;
        var text = Expression.Call(typeof(string).GetMethod("op_Implicit", [typeof(string)])!, Expression.Constant("ab"));

        return call.Update(null, [text, .. call.Arguments.Skip(1)]);
    }

    private readonly record struct Celsius(decimal Degrees)
    {
        public static explicit operator Celsius(decimal degrees) => new(degrees);
    }
}
