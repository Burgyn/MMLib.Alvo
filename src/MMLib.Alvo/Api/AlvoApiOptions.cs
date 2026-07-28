namespace MMLib.Alvo.Api;

/// <summary>
/// Configuration for the generated HTTP Data API. Infrastructure only, never domain input: which
/// entities exist, and what a caller may do to them, comes from the project descriptor
/// (<c>IDescriptorSource</c>) — the "descriptor ≠ options" rule
/// (<c>docs/architecture/extensibility.md</c> rule 6) is what keeps a backend's shape out of
/// <c>appsettings.json</c>.
/// </summary>
public sealed class AlvoApiOptions
{
    /// <summary>The route prefix every generated endpoint sits under. Default <c>/api</c>.</summary>
    /// <remarks>
    /// Configurable because an embedded host is mounting Alvo <em>beside</em> its own endpoints and
    /// must be able to keep the two apart; a leading and trailing <c>/</c> is normalized away, so
    /// <c>"api"</c>, <c>"/api"</c> and <c>"/api/"</c> all mount at the same place.
    /// </remarks>
    public string RoutePrefix { get; set; } = "/api";

    /// <summary>The page size used when a request names none. Default 50.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>
    /// The largest page a request may ask for. Default 200. Server-enforced rather than advisory:
    /// §2.1 requires a maximum, because an unbounded limit is a denial-of-service one query long.
    /// </summary>
    public int MaxPageSize { get; set; } = 200;
}
