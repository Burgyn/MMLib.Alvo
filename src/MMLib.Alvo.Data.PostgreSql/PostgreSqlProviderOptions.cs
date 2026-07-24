namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// Configuration for the PostgreSQL database provider, resolved from DI as
/// <c>IOptions&lt;PostgreSqlProviderOptions&gt;</c> when the provider is built. Every
/// <c>UsePostgreSql</c> overload funnels into this object, so a host can pass the connection string
/// inline or bind it from configuration.
/// </summary>
public sealed class PostgreSqlProviderOptions
{
    /// <summary>
    /// Gets or sets the PostgreSQL ADO.NET connection string. A connection string must end up set by
    /// the time the provider is built (inline, or via a configure delegate that binds it from
    /// configuration); otherwise provider construction fails fast with a clear error.
    /// </summary>
    public string? ConnectionString { get; set; }
}
