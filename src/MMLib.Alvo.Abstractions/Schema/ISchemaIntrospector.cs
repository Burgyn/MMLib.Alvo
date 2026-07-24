namespace MMLib.Alvo.Schema;

/// <summary>Port for introspecting a database schema.</summary>
public interface ISchemaIntrospector
{
    /// <summary>Introspects the current database schema.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The introspected schema model.</returns>
    Task<SchemaModel> IntrospectAsync(CancellationToken ct = default);
}
