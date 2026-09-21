namespace MMLib.Alvo.Management;

/// <summary>
/// Infrastructure configuration for the Management API — where it mounts, and how this deployment
/// describes itself. Never domain input: what a project is, and who may manage it, comes from the
/// descriptor.
/// </summary>
/// <remarks>
/// <para>
/// The spelling is <c>Alvo:*</c>, not the <c>ALVO_*</c> the spec's environment table sketches, and that is a
/// deliberate deviation (the F5 design's D2): <c>Alvo:Schema</c> and <c>Alvo:Events</c> already deviated the
/// same way, and a third spelling would be worse than either. In an environment variable the key is
/// <c>Alvo__Management__RoutePrefix</c>.
/// </para>
/// <para>
/// Public for the reason <see cref="Api.AlvoApiOptions"/> is: an embedded host mounts Alvo beside its own
/// endpoints and has to be able to move the prefix out of the way.
/// </para>
/// </remarks>
public sealed class AlvoManagementOptions
{
    /// <summary>The configuration section these options bind from: <c>Alvo:Management</c>.</summary>
    public const string SectionName = "Alvo:Management";

    /// <summary>The route prefix every management endpoint sits under. Default <c>/management</c>.</summary>
    /// <remarks>
    /// Normalized exactly as <see cref="Api.AlvoApiOptions.RoutePrefix"/> is, by the same single reduction —
    /// so <c>"management"</c>, <c>"/management"</c> and <c>"/management/"</c> mount in one place. Unlike the
    /// Data API's, it may <b>not</b> reduce to the empty string: the management surface owns literal path
    /// segments (<c>info</c>, and the ones later members add) that would shadow an entity route at the root.
    /// </remarks>
    public string RoutePrefix { get; set; } = "/management";

    /// <summary>
    /// How <c>GET {prefix}/info</c> describes this deployment, overriding the <see cref="AlvoMode"/>
    /// registered. <see langword="null"/> — the only value anything outside this assembly can observe —
    /// reports the mode itself.
    /// </summary>
    /// <remarks>
    /// <b><see langword="internal"/>, and that is what keeps <c>mode</c> a two-valued contract.</b>
    /// <see cref="ManagementInfo.Mode"/> and spec §2.2 both document it as <c>standalone</c> or
    /// <c>embedded</c>; a public free-text override would mean an agent branching on <c>info.mode</c> could
    /// no longer rely on either value, in exchange for a capability <see cref="AlvoOptions.Mode"/> already
    /// gives a host. It stays as the seam a later member of this surface can report through, and it does not
    /// bind from configuration — <c>ConfigurationBinder</c> does not write non-public properties, which is
    /// the behaviour wanted rather than an accident to work around.
    /// </remarks>
    internal string? ModeLabel { get; set; }
}
