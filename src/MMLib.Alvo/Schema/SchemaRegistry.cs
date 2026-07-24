namespace MMLib.Alvo.Schema;

/// <summary>Read model for the applied schema.</summary>
internal sealed class SchemaRegistry(SchemaModel model) : ISchemaRegistry
{
    /// <summary>Gets the current schema model.</summary>
    /// <returns>The schema model.</returns>
    public SchemaModel GetSchema() => model;
}
