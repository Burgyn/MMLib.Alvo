namespace MMLib.Alvo.Migrations;

/// <summary>
/// Thrown when an append loses the optimistic-lock race: the caller's expected revision no longer
/// matches the store's current revision, so another client changed the descriptor first.
/// </summary>
// Deliberately does not implement the standard (), (message), (message, inner) constructor set:
// this is a structured, typed exception always raised with Project/ExpectedRevision/ActualRevision
// by IDescriptorVersionStore.AppendAsync, never constructed generically by callers.
#pragma warning disable RCS1194
public sealed class DescriptorConcurrencyException(string project, int expectedRevision, int actualRevision)
    : Exception($"Descriptor for project '{project}' changed concurrently: expected revision {expectedRevision}, but current is {actualRevision}. Reload the latest revision and retry.")
{
    /// <summary>Gets the project whose append conflicted.</summary>
    public string Project { get; } = project;

    /// <summary>Gets the revision the caller expected to be current.</summary>
    public int ExpectedRevision { get; } = expectedRevision;

    /// <summary>Gets the revision that was actually current.</summary>
    public int ActualRevision { get; } = actualRevision;
}
#pragma warning restore RCS1194
