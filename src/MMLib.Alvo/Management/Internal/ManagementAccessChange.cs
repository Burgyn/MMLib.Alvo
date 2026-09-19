using MMLib.Alvo.Descriptor;
using System.Text.Json;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Whether one apply would change <b>who may reach the project</b> — the descriptor's <c>access</c> block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec §3.3 draws the line this answers:</b> <i>"<c>developer</c> edits what the backend is,
/// <c>admin</c> also decides who may reach it."</i> <c>access</c> is the one infrastructure-shaped block
/// inside the descriptor, and every accepted apply re-primes the catalog
/// <see cref="ManagementAccessEvaluator"/> reads — so an apply that rewrites it is an authorization change
/// wearing a configuration change's clothes.
/// </para>
/// <para>
/// <b>The parsed blocks are compared, never the text.</b> <see cref="Access"/> is a record of three
/// nullable strings, so its value equality <em>is</em> "the same three predicates", and whitespace, key
/// order and indentation cannot change the answer. A text comparison would need an administrator for every
/// apply from an editor that pretty-prints on save.
/// </para>
/// </remarks>
internal static class ManagementAccessChange
{
    /// <summary>
    /// Whether applying <paramref name="candidateDescriptorJson"/> would leave a different <c>access</c>
    /// block than <paramref name="appliedDescriptorJson"/> declares.
    /// </summary>
    /// <remarks>
    /// <b>A candidate nothing can parse is not an escalation.</b> It cannot be applied at all — the
    /// validator refuses it first, and the caller gets that refusal — so answering <see langword="true"/>
    /// here would only mean telling them to find an administrator for a descriptor no administrator could
    /// apply either. A stored descriptor that will not parse is the other way round: that is an invariant
    /// this instance relies on, so it fails closed.
    /// </remarks>
    /// <param name="appliedDescriptorJson">The descriptor currently applied, or empty when none is.</param>
    /// <param name="candidateDescriptorJson">The descriptor the caller is applying.</param>
    internal static bool Differs(string appliedDescriptorJson, string candidateDescriptorJson) =>
        TryReadAccess(candidateDescriptorJson, out var candidate)
        && (!TryReadAccess(appliedDescriptorJson, out var applied) || applied != candidate);

    /// <summary>The descriptor's <c>access</c> block, when it can be read at all.</summary>
    /// <remarks>
    /// Blank is a <em>known</em> answer rather than a failure: a project with nothing applied has no
    /// <c>access</c> block, so a first descriptor that declares none changes nothing and a first descriptor
    /// that declares one does.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor text to read.</param>
    /// <param name="access">The block it declares, or <see langword="null"/> when it declares none.</param>
    /// <returns><see langword="false"/> when the text is not a descriptor this build can parse.</returns>
    private static bool TryReadAccess(string descriptorJson, out Access? access)
    {
        access = null;
        if (string.IsNullOrWhiteSpace(descriptorJson))
        {
            return true;
        }

        try
        {
            access = AlvoDescriptor.Parse(descriptorJson).Access;
            return true;
        }
        catch (Exception refusal) when (
            refusal is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
