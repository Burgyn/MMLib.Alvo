using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>The field a lambda over a record type names: a mapped property, or a wrapper's <c>x.Record["field"]</c>.</summary>
internal static class TypedField
{
    /// <summary>The field <paramref name="field"/> names.</summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="field">A lambda such as <c>x =&gt; x.Status</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="field"/> names no field of <typeparamref name="T"/>.</exception>
    internal static string Of<T, TValue>(Expression<Func<T, TValue>> field)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(field);
        var shape = RecordShape.Of(typeof(T));
        var row = field.Parameters[0];
        var body = LambdaShape.Unwrapped(field.Body);
        if (body is MemberExpression { Expression: ParameterExpression owner } member && owner == row && shape.Mapped(member.Member) is { } mapped)
        {
            return mapped.Field;
        }

        if (body is MethodCallExpression call && LambdaShape.RecordKey(call) is { } key && shape.IsWrapper
            && call.Object is MemberExpression { Member.Name: nameof(IAlvoEntity.Record), Expression: ParameterExpression wrapper } && wrapper == row)
        {
            return key;
        }

        throw new ArgumentException(
            $"'{field.Body}' names no field of {typeof(T).Name}: set a mapped property, such as x => x.Status, or a wrapper's x.Record[\"field\"].",
            nameof(field));
    }
}
