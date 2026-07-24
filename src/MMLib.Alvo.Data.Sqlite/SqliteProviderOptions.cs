namespace MMLib.Alvo.Data.Sqlite;

/// <summary>
/// Configuration for the SQLite database provider, resolved from DI as
/// <c>IOptions&lt;SqliteProviderOptions&gt;</c> when the provider is built. Every <c>UseSqlite</c>
/// overload funnels into this object, so a host can pass the connection string inline or bind it
/// from configuration.
/// </summary>
public sealed class SqliteProviderOptions
{
    /// <summary>
    /// Gets or sets the SQLite ADO.NET connection string. A connection string must end up set by
    /// the time the provider is built (inline, or via a configure delegate that binds it from
    /// configuration); otherwise provider construction fails fast with a clear error.
    /// </summary>
    public string? ConnectionString { get; set; }
}
