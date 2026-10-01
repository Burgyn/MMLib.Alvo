namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>One submit at a time: a second press while the first is in flight does nothing.</summary>
/// <remarks>
/// The disabled attribute alone is not enough: it arrives one render after the first click, and a fast double click
/// lands both clicks before that render (inventory defect #6).
/// </remarks>
internal sealed class SubmitGate
{
    /// <summary>Whether a submit is in flight.</summary>
    public bool Busy { get; private set; }

    /// <summary>Starts a submit; answers <see langword="false"/> when one is already running.</summary>
    public bool TryBegin()
    {
        if (Busy)
        {
            return false;
        }

        Busy = true;
        return true;
    }

    /// <summary>Ends the submit in flight, whatever its outcome.</summary>
    public void End() => Busy = false;
}
