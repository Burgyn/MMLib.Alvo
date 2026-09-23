using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The warned blocks one descriptor actually declares — what Overview lists as declared, with limits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Intersected with the descriptor, because a list of five subsystems nobody asked for trains an
/// operator to ignore the panel.</b> <c>capabilities.warned</c> names every block this build does not
/// fully honour; a project that declares none of them has nothing to be told.
/// </para>
/// <para>
/// <b>Not split into "not running" and "partly running", and that is the capability contract's call
/// rather than this screen's.</b> A warned block carries a name and the framework's own consequence
/// sentence, and no signal of how much of the block runs — <c>templates</c> and <c>webhooks</c> are
/// delivered from an after-hook and dead from automation, <c>functions</c> never runs at all, and both
/// arrive in the same shape. Telling them apart here would mean reading the prose, which is served
/// verbatim precisely so that no client reinterprets it. So the heading claims only what is true of every
/// row — parts of these blocks are not honoured — and each consequence says which.
/// </para>
/// </remarks>
internal static class DeclaredLimits
{
    /// <summary>The warned blocks <paramref name="descriptorJson"/> declares, in the order the build reports them.</summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="capabilities">What this build honours, warns about and refuses.</param>
    public static IReadOnlyList<ManagementWarnedBlock> Of(string descriptorJson, ManagementCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var declared = DescriptorLens.DeclaredBlocks(descriptorJson);
        return [.. capabilities.Warned.Where(block => declared.Contains(block.Block))];
    }
}
