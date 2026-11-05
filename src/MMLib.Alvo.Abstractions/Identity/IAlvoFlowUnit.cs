namespace MMLib.Alvo;

/// <summary>What the ambient flow knows of the transaction unit open on it: whether it is still open, and how to fail it.</summary>
/// <remarks>
/// Internal and in Abstractions because the core refuses a publish inside a unit the EF driver opened, and neither may
/// reference the other. Implemented by the driver's write unit only.
/// </remarks>
internal interface IAlvoFlowUnit
{
    /// <summary>Gets a value indicating whether the <c>TransactAsync</c> that opened the unit has not returned yet.</summary>
    bool IsOpen { get; }

    /// <summary>Fails the unit: it will roll back however its delegate ends, and its commit throws <paramref name="failure"/>.</summary>
    /// <param name="failure">The first failure that makes the unit's work untrustworthy.</param>
    void Poison(Exception failure);

    /// <summary>
    /// Records <paramref name="cause"/> as the unit's failure, replacing a cancellation recorded before it and nothing else:
    /// a hook's deadline is why its own calls were cancelled (Ruling 11).
    /// </summary>
    /// <param name="cause">The failure the unit should end with.</param>
    void Blame(Exception cause);
}
