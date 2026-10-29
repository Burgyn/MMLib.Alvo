namespace MMLib.Alvo.Tests.Data;

/// <summary>An open unit as <c>AlvoFlow</c> sees it, recording what poisoned it.</summary>
internal sealed class FakeFlowUnit : IAlvoFlowUnit
{
    private readonly List<Exception> _poisoned = [];

    public bool IsOpen { get; set; } = true;

    internal IReadOnlyList<Exception> Poisoned => _poisoned;

    public void Poison(Exception failure) => _poisoned.Add(failure);
}
