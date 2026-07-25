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
