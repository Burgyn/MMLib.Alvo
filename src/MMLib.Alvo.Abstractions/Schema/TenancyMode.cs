namespace MMLib.Alvo.Schema;

/// <summary>Enumeration of tenancy modes for entities.</summary>
public enum TenancyMode
{
    /// <summary>Scoped tenancy (data is separated by tenant).</summary>
    Scoped,

    /// <summary>Global tenancy (data is not separated by tenant).</summary>
    Global
}
