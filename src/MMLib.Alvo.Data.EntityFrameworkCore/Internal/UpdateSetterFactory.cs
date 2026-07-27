using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MMLib.Alvo.Schema;
using System.Linq.Expressions;
using System.Reflection;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Builds an <c>ExecuteUpdate</c> setter list at request time from a field/value patch. The field names
/// and their CLR types are only known once a request arrives, so the statically-typed
/// <c>SetProperty</c> chain is unavailable and EF Core 10's non-expression
/// <see cref="UpdateSettersBuilder{TSource}"/> overload is driven by reflection instead.
/// </summary>
/// <remarks>
/// <para>
/// The overload selected is the one whose second parameter <em>is</em> the generic method parameter —
/// <c>SetProperty&lt;TProperty&gt;(Expression&lt;Func&lt;T, TProperty&gt;&gt; selector, TProperty value)</c> —
/// not its sibling that takes a second selector, and not the non-generic pair that takes a raw
/// <see cref="Expression"/> for the value. That last distinction is the load-bearing one: a value handed over
/// as an <see cref="Expression"/> is a constant EF is free to <b>inline into the SQL text</b>, where a value
/// handed over as a value becomes a bind parameter with the column's own <c>DbType</c>. So the discriminator
/// is spelled out rather than left to overload order.
/// </para>
/// <para>
/// The patch is projected to a list <em>before</em> the returned delegate runs, so a field name is resolved
/// against the schema once, at composition time, rather than inside a callback EF invokes later.
/// </para>
/// </remarks>
internal static class UpdateSetterFactory
{
    private static readonly MethodInfo _setProperty = typeof(UpdateSettersBuilder<Dictionary<string, object>>)
        .GetMethods()
        .Single(method => string.Equals(method.Name, SetPropertyName, StringComparison.Ordinal)
            && method.GetGenericArguments().Length == 1
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType.IsGenericMethodParameter);

    private static readonly MethodInfo _efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;

    private const string SetPropertyName = "SetProperty";

    /// <summary>Builds the setter callback for one patch over <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="values">The field values to set.</param>
    /// <exception cref="AlvoAuthorizationException">A key names a field the entity does not declare.</exception>
    internal static Action<UpdateSettersBuilder<Dictionary<string, object>>> For(
        EntitySchema entity, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(values);

        var setters = values
            .Select(pair => Setter(entity, pair.Key, pair.Value))
            .ToList();

        return builder =>
        {
            foreach (var (selector, clrType, value) in setters)
            {
                _setProperty.MakeGenericMethod(clrType).Invoke(builder, [selector, value]);
            }
        };
    }

    /// <summary>
    /// One field's setter: the typed selector, the read model's type for the column, and the value <b>as that
    /// column must hold it</b>. The last part is what routes a timestamp through
    /// <see cref="StoredInstant"/> — an update is one of the three paths on which a caller's offset would
    /// otherwise become part of the stored value.
    /// </summary>
    private static (LambdaExpression Selector, Type ClrType, object? Value) Setter(
        EntitySchema entity, string field, object? value)
    {
        var clrType = ClrTypeOf(entity, field);
        return (Selector(clrType, field), clrType, StoredInstant.Stored(clrType, value));
    }

    private static LambdaExpression Selector(Type clrType, string field)
    {
        var row = Expression.Parameter(typeof(Dictionary<string, object>), "row");
        var call = Expression.Call(_efProperty.MakeGenericMethod(clrType), row, Expression.Constant(field));
        return Expression.Lambda(
            typeof(Func<,>).MakeGenericType(typeof(Dictionary<string, object>), clrType), call, row);
    }

    /// <summary>
    /// The setter's type is the <b>read model's</b> nullable type for the field, so a patch clearing a
    /// nullable column to <see langword="null"/> binds a real <c>SET col = NULL</c> rather than failing to
    /// box. The field is resolved through <see cref="QueryFieldGuard"/>, so an undeclared name is refused
    /// with the same message every other unwritable field gets rather than reaching a reflection failure.
    /// </summary>
    private static Type ClrTypeOf(EntitySchema entity, string field) =>
        FieldClrTypeMap.Optional(QueryFieldGuard.DeclaredField(entity, field));
}
