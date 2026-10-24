using System.Collections;
using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// The calls a typed query may make: a list's <c>Contains</c> of a field, as <c>in</c> (an empty list matches nothing).
/// The text tests wait for their filter operators (plan Ruling 7) and never fall back to <c>like</c>.
/// </summary>
internal sealed partial class FilterTranslator
{
    private const string NoTextOperatorFix = "match the whole value with ==, or filter the rows after reading them.";
    private const string ListFix = "copy the values into an array or a List<T>.";

    private static readonly FrozenSet<string> _textTests = new[] { nameof(string.StartsWith), nameof(string.EndsWith), nameof(string.Contains) }.ToFrozenSet(StringComparer.Ordinal);

    private AlvoFilter Call(MethodCallExpression call)
    {
        if (call.Object?.Type == typeof(string) && _textTests.Contains(call.Method.Name))
        {
            throw Refuse(call, $"'{call.Method.Name}' has no filter operator yet: AlvoFilter offers no literal prefix, suffix or substring test", NoTextOperatorFix);
        }

        if (!LambdaShape.IsContains(call, out var source, out var value))
        {
            throw Refuse(call, LambdaShape.IsClock(call) ? "a query has no clock" : $"method '{call.Method.Name}' has no filter equivalent");
        }

        return !LambdaShape.Touches(source, _row) && value.Type != typeof(object)
            ? Membership(call, source, value)
            : throw Refuse(call, "Contains translates only as a list of values containing a field");
    }

    private AlvoFilter Membership(MethodCallExpression call, Expression source, Expression value)
    {
        var enumType = EnumOf(value);
        var field = Field(enumType is null ? value : LambdaShape.Bare(value));
        var candidates = Candidates(call, source, enumType);

        return candidates.Count == 0 ? new AlvoOr([]) : new AlvoComparison(field, AlvoFilterOperator.In, candidates);
    }

    private List<object?> Candidates(MethodCallExpression call, Expression source, Type? enumType)
    {
        if (LambdaShape.FindClock(source) is { } clock)
        {
            throw Refuse(clock, "a query has no clock", NoClockFix);
        }

        var items = LambdaValues.Evaluate(source) as IEnumerable ?? throw Refuse(source, "the list is null", ListFix);
        EnsureDefaultEquality(source, items);
        if (items is ICollection { Count: var count } && count > AlvoFilter.MaxInCandidates)
        {
            throw TooMany(call, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var candidates = items.Cast<object?>().Take(AlvoFilter.MaxInCandidates + 1).Select(item => (object?)Candidate(item, enumType, source)).ToList();
        return candidates.Count > AlvoFilter.MaxInCandidates ? throw TooMany(call, $"more than {AlvoFilter.MaxInCandidates}") : candidates;
    }

    private object Candidate(object? item, Type? enumType, Expression source) =>
        Typed(item, enumType, source) ?? throw Refuse(source, "a candidate is null, and 'in' never matches an empty value", "test for null with == null, beside the list.");

    private AlvoTranslationException TooMany(MethodCallExpression call, string count) =>
        Refuse(call, $"it lists {count} candidates, past the {AlvoFilter.MaxInCandidates} one filter may bind", "split the list across queries.");

    private void EnsureDefaultEquality(Expression source, IEnumerable items)
    {
        var type = items.GetType();
        if (type.IsArray || IsGeneric(type, typeof(List<>)))
        {
            return;
        }

        if (IsGeneric(type, typeof(HashSet<>)))
        {
            if (!Equals(type.GetProperty(nameof(HashSet<int>.Comparer))!.GetValue(items), DefaultComparer(type.GetGenericArguments()[0])))
            {
                throw Refuse(source, $"the {SetName(type)} carries its own comparer, which a filter cannot apply", ListFix);
            }

            return;
        }

        if (type.GetInterfaces().Any(contract => IsGeneric(contract, typeof(ICollection<>))))
        {
            throw Refuse(source, $"a {type.Name.Split('`')[0]} may compare with its own equality, which a filter cannot apply", ListFix);
        }
    }

    private static bool IsGeneric(Type type, Type definition) => type.IsGenericType && type.GetGenericTypeDefinition() == definition;

    private static object? DefaultComparer(Type item) =>
        typeof(EqualityComparer<>).MakeGenericType(item).GetProperty(nameof(EqualityComparer<int>.Default))!.GetValue(null);

    private static string SetName(Type type) => $"HashSet<{type.GetGenericArguments()[0].Name}>";
}
