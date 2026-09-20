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
    /// <param name="appliedDescriptorJson">The descriptor currently applied, or blank when none is.</param>
    /// <param name="candidateDescriptorJson">The descriptor the caller is applying.</param>
    internal static bool Differs(string appliedDescriptorJson, string candidateDescriptorJson) =>
        TryReadAccess(candidateDescriptorJson, out var candidate)
        && (!TryReadApplied(appliedDescriptorJson, out var applied) || applied != candidate);

    /// <summary>
    /// Whether restoring <paramref name="storedDescriptorJson"/> would leave a different <c>access</c> block
    /// than <paramref name="appliedDescriptorJson"/> declares — the rollback route's reading.
    /// </summary>
    /// <remarks>
    /// <b>A stored descriptor that will not parse fails <em>closed</em>, the opposite of
    /// <see cref="Differs"/>.</b> That member's open failure is justified by a validator standing behind it:
    /// a caller's candidate is refused before it can be applied, so calling it an escalation would only send
    /// an editor to find an administrator for a descriptor no administrator could apply either. Nothing
    /// stands behind a revision this instance already appended — it is an invariant of this instance, which
    /// the type's own remarks already said must fail closed, and reading it as "no access change" would hand
    /// a <c>developer</c> a restore whose block nobody could compare.
    /// </remarks>
    /// <param name="appliedDescriptorJson">The descriptor currently applied, or blank when none is.</param>
    /// <param name="storedDescriptorJson">The stored revision the caller is restoring.</param>
    /// <returns><see langword="true"/> when the restore changes who may reach the project, or cannot be read.</returns>
    internal static bool DiffersFromStored(string appliedDescriptorJson, string storedDescriptorJson) =>
        !TryReadAccess(storedDescriptorJson, out var stored)
        || !TryReadApplied(appliedDescriptorJson, out var applied)
        || applied != stored;

    /// <summary>
    /// The <c>access</c> block of the descriptor currently applied, where <b>blank is a known answer</b>.
    /// </summary>
    /// <remarks>
    /// <b>The two sides read blank differently, and the asymmetry is the point.</b> Here it means nothing
    /// has been applied yet, so there is genuinely no <c>access</c> block and a first descriptor declaring
    /// one is a change. On the candidate side it means the caller sent no descriptor at all, which is a
    /// malformed request for the validator to refuse — never a block to compare against.
    /// </remarks>
    /// <param name="descriptorJson">The applied descriptor text, or blank when none is applied.</param>
    /// <param name="access">The block it declares, or <see langword="null"/> when it declares none.</param>
    /// <returns><see langword="false"/> when the text is not a descriptor this build can parse.</returns>
    private static bool TryReadApplied(string descriptorJson, out Access? access)
    {
        access = null;

        return string.IsNullOrWhiteSpace(descriptorJson) || TryReadAccess(descriptorJson, out access);
    }

    /// <summary>The descriptor's <c>access</c> block, when the text is a descriptor at all.</summary>
    /// <param name="descriptorJson">The descriptor text to read.</param>
    /// <param name="access">The block it declares, or <see langword="null"/> when it declares none.</param>
    /// <returns><see langword="false"/> when the text is blank, or not a descriptor this build can parse.</returns>
    private static bool TryReadAccess(string descriptorJson, out Access? access)
    {
        access = null;
        if (string.IsNullOrWhiteSpace(descriptorJson))
        {
            return false;
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
