using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>A write hook bound to one catalog: its compiled condition per named entity, or one for every entity.</summary>
internal sealed record BoundWriteHook(WriteHookRegistration Registration, IReadOnlyDictionary<string, CompiledExpression?> Conditions, CompiledExpression? Fallback)
{
    internal string Id => Registration.Id;

    /// <summary>The condition the hook judges a write to <paramref name="entity"/> by; <see langword="null"/> runs it always.</summary>
    internal CompiledExpression? ConditionFor(string entity) => Registration.IsEveryEntity ? Fallback : Conditions.GetValueOrDefault(entity);
}
