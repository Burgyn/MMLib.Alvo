namespace MMLib.Alvo.Admin.Components.Operations;

/// <summary>Whether the Operations page is running a step, counting steps that run inside another one.</summary>
/// <remarks>
/// A confirm runs its change, then reloads the page — a guarded step of its own — then moves focus and closes. A single
/// flag let the reload's end enable the confirm button while the confirm was still running, so a second press could run
/// the change again; counting keeps the page busy until the outermost step ends.
/// </remarks>
internal sealed class NestedBusy
{
    private int _depth;

    /// <summary>Gets whether any step is running.</summary>
    public bool Busy => _depth > 0;

    /// <summary>Starts a step; disposing the answer ends it.</summary>
    /// <returns>The step's scope.</returns>
    public IDisposable Enter()
    {
        _depth++;
        return new Step(this);
    }

    private sealed class Step(NestedBusy owner) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (!_ended)
            {
                _ended = true;
                owner._depth--;
            }
        }
    }
}
