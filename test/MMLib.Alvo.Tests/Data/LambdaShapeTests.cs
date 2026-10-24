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
}
