using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>The <c>entity.update</c> arm of <see cref="DescriptorActionRunner"/>.</summary>
internal sealed class EntityUpdateEffect
{
    internal Task ApplyAsync(CompiledAction action, AlvoEventContext context, CancellationToken ct) =>
        throw new NotSupportedException("entity.update runs from Task 6.");
}
