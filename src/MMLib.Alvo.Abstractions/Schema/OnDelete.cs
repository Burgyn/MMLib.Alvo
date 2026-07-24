namespace MMLib.Alvo.Schema;

/// <summary>Enumeration of on-delete referential integrity actions.</summary>
public enum OnDelete
{
    /// <summary>Restrict the deletion if referenced.</summary>
    Restrict,

    /// <summary>Cascade the deletion to referencing records.</summary>
    Cascade,

    /// <summary>Set reference to null on deletion.</summary>
    SetNull
}
