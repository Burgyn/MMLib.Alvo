namespace MMLib.Alvo.Api;

/// <summary>
/// Configuration for the generated HTTP Data API. Infrastructure only, never domain input: which
/// entities exist, and what a caller may do to them, comes from the project descriptor
/// (<c>IDescriptorSource</c>) — the "descriptor ≠ options" rule
/// (<c>docs/architecture/extensibility.md</c> rule 6) is what keeps a backend's shape out of
/// <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Every member here is validated at startup (<c>Internal.AlvoApiOptionsValidator</c>), never at the
/// first request. That is not ceremony: a <see cref="RoutePrefix"/> of <c>"/"</c> produces the pattern
/// <c>//owners</c> and an opaque <c>RoutePatternException</c> from deep inside routing, and a negative
/// <see cref="DefaultPageSize"/> turns every list into a 422. A misconfiguration must fail fast with a
/// message naming the option and the fix (§0 principle 4).
/// </remarks>
public sealed class AlvoApiOptions
{
    /// <summary>The route prefix every generated endpoint sits under. Default <c>/api</c>.</summary>
    /// <remarks>
    /// Configurable because an embedded host is mounting Alvo <em>beside</em> its own endpoints and
    /// must be able to keep the two apart. A leading and trailing <c>/</c> is normalized away, so
    /// <c>"api"</c>, <c>"/api"</c> and <c>"/api/"</c> all mount at the same place; anything that cannot
    /// normalize into a legal route pattern — an empty segment, a route-parameter brace, a wildcard, a
    /// query or fragment marker — is refused at startup rather than left to fail when the first route
    /// is built.
    /// </remarks>
    public string RoutePrefix { get; set; } = "/api";

    /// <summary>The page size used when a request names none. Default 50.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>
    /// The largest page a request may ask for. Default 200. Server-enforced rather than advisory:
    /// §2.1 requires a maximum, because an unbounded limit is a denial-of-service one query long.
    /// </summary>
    public int MaxPageSize { get; set; } = 200;

    /// <summary>The largest request body a write endpoint will read. Default 1 MiB.</summary>
    /// <remarks>
    /// The three payload bounds exist because the body parser is reachable <b>before policy and without
    /// authentication</b> — an anonymous caller's POST is parsed before the port has any say — so an
    /// unbounded parser is a denial of service that needs no credential at all. Each bound is enforced
    /// <em>while</em> reading, never on a finished document: a limit checked after parsing has already
    /// paid the cost it exists to prevent. 1 MiB is generous for a descriptor-shaped record (a flat map
    /// of declared fields) and small enough that a request cannot exhaust a host by arriving.
    /// </remarks>
    public int MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>How deeply a request body may nest. Default 32.</summary>
    /// <remarks>
    /// Deliberately the same number as <c>CelParser.MaxDepth</c> and
    /// <see cref="Data.AlvoFilter.MaxDepth"/>: one depth cap the whole framework can explain, rather
    /// than three numbers a reader has to look up separately. A write payload is a flat field map, so
    /// depth only grows through a <c>json</c> field's own value — 32 is far past anything a descriptor
    /// declares, and short of the nesting that makes a recursive reader a stack overflow away from a
    /// 500.
    /// </remarks>
    public int MaxPayloadDepth { get; set; } = 32;

    /// <summary>How many keys a request body's top-level object may carry. Default 512.</summary>
    /// <remarks>
    /// The bound <see cref="MaxPayloadDepth"/> misses: a wide-but-shallow object escapes a depth cap
    /// entirely, which is exactly why <see cref="Data.AlvoFilter.MaxTerms"/> exists beside
    /// <see cref="Data.AlvoFilter.MaxDepth"/>. 512 is far more fields than any entity the schema admits
    /// declares, so a legitimate payload never approaches it.
    /// </remarks>
    public int MaxPayloadKeys { get; set; } = 512;
}
