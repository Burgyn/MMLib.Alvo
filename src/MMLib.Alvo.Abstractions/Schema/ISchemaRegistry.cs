namespace MMLib.Alvo.Schema;

/// <summary>Port for retrieving the current schema.</summary>
public interface ISchemaRegistry
{
    /// <summary>Gets the current schema model.</summary>
    /// <returns>The schema model.</returns>
    SchemaModel GetSchema();
}
