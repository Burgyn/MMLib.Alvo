namespace MMLib.Alvo.Hooks;

/// <summary>How a write hook refused a write: what the caller is told, and with which status.</summary>
/// <remarks>Append only. When one hook records several kinds, forbidden wins over conflict, which wins over invalid.</remarks>
public enum AlvoWriteRejectionKind
{
    /// <summary>The data is not acceptable: 422 <c>validation</c>.</summary>
    Invalid = 0,

    /// <summary>The data clashes with the state of other data: 409 <c>conflict</c>.</summary>
    Conflict = 1,

    /// <summary>The caller may not make this write: 403 <c>forbidden</c>.</summary>
    Forbidden = 2,
}
