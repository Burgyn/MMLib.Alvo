namespace MMLib.Alvo.Rules;

/// <summary>The kind of operation performed against an entity's data.</summary>
public enum DataOperation
{
    /// <summary>Listing multiple records.</summary>
    List,

    /// <summary>Reading a single record.</summary>
    Get,

    /// <summary>Creating a record.</summary>
    Create,

    /// <summary>Updating a record.</summary>
    Update,

    /// <summary>Deleting a record.</summary>
    Delete,
}

/// <summary>
/// The single, shared mapping from <see cref="DataOperation"/> to its lowercase wire name — the
/// descriptor's <c>rules.&lt;operation&gt;</c> JSON key and the operation name a deny reason may
/// safely name. Kept next to <see cref="DataOperation"/> so the policy catalog builder (JSON error
/// paths) and the policy engine (deny-reason text) share one definition rather than two literal
/// tables that could silently drift apart.
/// </summary>
internal static class DataOperationNames
{
    /// <summary>Gets <paramref name="operation"/>'s lowercase wire name.</summary>
    /// <param name="operation">The operation to name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not one of the named cases.</exception>
    internal static string ToWireName(this DataOperation operation) => operation switch
    {
        DataOperation.List => "list",
        DataOperation.Get => "get",
        DataOperation.Create => "create",
        DataOperation.Update => "update",
        DataOperation.Delete => "delete",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unmapped DataOperation; add its lowercase wire name here rather than falling back to PascalCase."),
    };
}
