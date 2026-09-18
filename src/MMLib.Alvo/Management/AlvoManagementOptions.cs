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
    /// How <c>GET {prefix}/info</c> describes this deployment, when the host wants to say something other
    /// than the <see cref="AlvoMode"/> it registered. <see langword="null"/> reports the mode itself.
    /// </summary>
    public string? ModeLabel { get; set; }
}
