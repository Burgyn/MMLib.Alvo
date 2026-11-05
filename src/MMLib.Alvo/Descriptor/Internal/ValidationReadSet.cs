using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// What a field's <c>validation</c> really reads, derived fields seen through (HX-11 Ruling 61): a <c>computed</c> field reads
/// what its expression reads, transitively, and a <c>rollup</c> reads its child's aggregated field — so a hidden value reached
/// through either is a hidden read, exactly as a direct one is.
/// </summary>
/// <remarks>
/// A <c>count</c> rollup aggregates no field and contributes nothing; which child rows it counts is a row question, the
/// accepted risk of Ruling 62. A <c>computed</c> expression that does not parse contributes nothing here, because
/// <see cref="ComputedFieldCheck"/> refuses it on its own.
/// </remarks>
/// <param name="schema">The mapped schema, whose derived fields are expanded.</param>
/// <param name="descriptor">The descriptor, whose <c>hidden</c> flags decide what is hidden.</param>
internal sealed class ValidationReadSet(SchemaModel schema, AlvoDescriptor descriptor)
{
    private readonly Dictionary<string, HashSet<string>> _hidden = new(StringComparer.Ordinal);

    /// <summary>One field of one entity.</summary>
    /// <param name="Entity">The entity's name.</param>
    /// <param name="Field">The field's name.</param>
    internal readonly record struct Read(string Entity, string Field);

    /// <summary>Every field <paramref name="fields"/> of <paramref name="entity"/> read, each derived field expanded to its sources.</summary>
    /// <param name="entity">The entity the fields belong to.</param>
    /// <param name="fields">The fields read directly.</param>
    internal HashSet<Read> Closure(EntitySchema entity, IEnumerable<string> fields)
    {
        var seen = new HashSet<Read>();
        var pending = new Stack<(EntitySchema Entity, string Field)>(fields.Select(field => (entity, field)));
        while (pending.TryPop(out var next))
        {
            if (seen.Add(new Read(next.Entity.Name, next.Field)))
            {
                foreach (var source in Sources(next.Entity, next.Field))
                {
                    pending.Push(source);
                }
            }
        }

        return seen;
    }

    /// <summary>Whether the descriptor flags <paramref name="read"/> hidden, statically or per role.</summary>
    /// <param name="read">The field.</param>
    internal bool IsHidden(Read read) => HiddenOf(read.Entity).Contains(read.Field);

    private IEnumerable<(EntitySchema Entity, string Field)> Sources(EntitySchema entity, string name)
    {
        var field = entity.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return field switch
        {
            { ComputedExpression: { } computed } => ComputedReads(computed).Select(read => (entity, read)),
            { Rollup: { Field: { } child } rollup } when Entity(rollup.From) is { } from => [(from, child)],
            _ => [],
        };
    }

    private EntitySchema? Entity(string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static IReadOnlySet<string> ComputedReads(string computed)
    {
        try
        {
            return CelDataOrigin.FieldsRead(CelParser.Parse(computed, CelFunctionCatalog.BuiltIns));
        }
        catch (CelSyntaxException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private HashSet<string> HiddenOf(string entity)
    {
        if (!_hidden.TryGetValue(entity, out var hidden))
        {
            hidden = descriptor.Entities.TryGetValue(entity, out var declared)
                ? declared.Fields
                    .Where(pair => pair.Value.Hidden is { IsExpression: true } or { Boolean: true })
                    .Select(pair => pair.Key)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            _hidden.Add(entity, hidden);
        }

        return hidden;
    }
}
