namespace MMLib.Alvo.Data;

/// <summary>The write a host write hook runs on.</summary>
/// <remarks>
/// Not <see cref="Rules.DataOperation"/>, the policy's verb: the policy has no replace, and a hook tells a replace from an
/// update. Append only: a later version adds members after these and never renumbers them.
/// </remarks>
public enum AlvoWriteOperation
{
    /// <summary>A record is created.</summary>
    Create = 0,

    /// <summary>Some fields of an existing record are changed.</summary>
    Update = 1,

    /// <summary>A whole record is written at a known id: replacing it, or creating it when there was none (then there is no old record).</summary>
    Replace = 2,

    /// <summary>A record is deleted.</summary>
    Delete = 3,
}
