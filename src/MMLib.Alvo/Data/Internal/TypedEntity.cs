using MMLib.Alvo.Extensions.Internal;
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>What a typed read needs: the entity its type names, its query, and its rows as the type.</summary>
internal static class TypedEntity
{
    internal static string NameOf<T>()
    {
        var entity = RecordShape.Of(typeof(T)).Entity
            ?? throw new InvalidOperationException($"{typeof(T).Name} carries no [AlvoEntity(\"…\")], so a typed read cannot name its entity: mark it with the entity it maps.");

        return entity == EntityEvents.AllEntities
            ? throw new InvalidOperationException($"{typeof(T).Name} maps [AlvoEntity(\"*\")], so a typed read cannot name its entity: map the one entity it reads.")
            : entity;
    }

    internal static AlvoQuery Query<T>(Expression<Func<T, bool>> where, int limit)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var entity = NameOf<T>();

        return new AlvoQuery { Entity = entity, Filter = FilterTranslator.Translate(where, entity), Limit = limit };
    }

    internal static IReadOnlyList<T> Items<T>(AlvoPage page)
        where T : class =>
        [.. page.Items.Select(item => item.As<T>())];
}
