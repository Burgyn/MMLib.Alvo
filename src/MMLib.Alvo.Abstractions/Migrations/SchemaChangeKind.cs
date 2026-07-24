namespace MMLib.Alvo.Migrations;

/// <summary>Enumeration of possible schema changes.</summary>
public enum SchemaChangeKind
{
    /// <summary>Create a new entity.</summary>
    CreateEntity,

    /// <summary>Drop an existing entity.</summary>
    DropEntity,

    /// <summary>Rename an entity.</summary>
    RenameEntity,

    /// <summary>Add a field to an entity.</summary>
    AddField,

    /// <summary>Drop a field from an entity.</summary>
    DropField,

    /// <summary>Rename a field.</summary>
    RenameField,

    /// <summary>Alter a field definition.</summary>
    AlterField,

    /// <summary>Add an index.</summary>
    AddIndex,

    /// <summary>Drop an index.</summary>
    DropIndex,

    /// <summary>Rename an index.</summary>
    RenameIndex,
}
