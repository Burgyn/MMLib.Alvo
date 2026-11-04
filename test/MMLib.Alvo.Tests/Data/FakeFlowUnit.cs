namespace MMLib.Alvo.Tests.Data;

/// <summary>An open unit as <c>AlvoFlow</c> sees it, recording what poisoned and what blamed it.</summary>
internal sealed class FakeFlowUnit : IAlvoFlowUnit
{
    private readonly List<Exception> _poisoned = [];
    private readonly List<Exception> _blamed = [];

    public bool IsOpen { get; set; } = true;

    internal IReadOnlyList<Exception> Poisoned => _poisoned;

    internal IReadOnlyList<Exception> Blamed => _blamed;

    public void Poison(Exception failure) => _poisoned.Add(failure);

    public void Blame(Exception cause) => _blamed.Add(cause);
}
