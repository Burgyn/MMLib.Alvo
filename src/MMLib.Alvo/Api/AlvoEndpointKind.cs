namespace MMLib.Alvo.Api;

/// <summary>Which generated Data API route an endpoint is — finer than the operation it is authorized as.</summary>
/// <remarks>
/// Two kinds can be one operation: <see cref="Query"/> is a second way to reach the <see cref="List"/> read, and
/// <see cref="Replace"/> and both batch updates are authorized as <c>update</c>. Members are appended, never reordered or
/// renumbered.
/// </remarks>
public enum AlvoEndpointKind
{
    /// <summary>The collection read, its parameters in the query string.</summary>
    List = 0,

    /// <summary>The collection read, its parameters in a JSON body.</summary>
    Query = 1,

    /// <summary>The single-row read.</summary>
    Get = 2,

    /// <summary>The create.</summary>
    Create = 3,

    /// <summary>The partial update.</summary>
    Update = 4,

    /// <summary>The create-or-replace of the row the path names.</summary>
    Replace = 5,

    /// <summary>The delete.</summary>
    Delete = 6,

    /// <summary>The batch create.</summary>
    BatchCreate = 7,

    /// <summary>The batch update.</summary>
    BatchUpdate = 8,

    /// <summary>The batch delete.</summary>
    BatchDelete = 9,
}
