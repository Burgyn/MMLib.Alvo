namespace MMLib.Alvo.Management;

/// <summary>
/// <b>The one operation surface for administering an Alvo project.</b> The admin dashboard resolves it from
/// DI and calls it in-process; an agent, the CLI and a later MCP adapter reach the same members over HTTP.
/// One path, two transports — spec §0.5 contract 4 forbids a divergent write <em>path</em>, not
/// serialisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member is descriptor-shaped and idempotent</b>, so an MCP adapter is a mapping rather than a
/// translation: there is no HTTP-only affordance an adapter would have to fake. A write carries its own
/// expected revision (optimistic concurrency) and its own idempotency key, rather than reading either off a
/// request header, which is what keeps the in-process caller's semantics identical to the HTTP caller's.
/// </para>
/// <para>
/// <b>Data is deliberately absent.</b> Rows are read and written through the Data API under the caller's own
/// context, so no management privilege over data exists and none has to be audited.
/// </para>
/// <para>
/// <b>Every member has an HTTP route, and a contract test holds that</b> — the drift this shape risks is an
/// operation reachable in-process and not over the wire. The test reads the <em>live endpoint table</em>, so
/// adding a member here without adding a route fails a build.
/// </para>
/// <para>
/// <b>Authorization is not this interface's job.</b> Over HTTP every route carries the gate
/// <c>access</c> compiles (<c>RequireAlvoManagementAccess</c>); an in-process caller holding this reference
/// has already been admitted by whatever composed it. Publishing two answers here and there is exactly the
/// divergence contract 4 exists to prevent.
/// </para>
/// </remarks>
public interface IAlvoManagement
{
    /// <summary>Describes this deployment: the build, the mode, the data provider and the startup mode.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What this instance is.</returns>
    Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default);
}
