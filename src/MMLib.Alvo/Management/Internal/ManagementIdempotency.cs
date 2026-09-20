using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// What makes two management writes "the same request" for replay purposes, and the token one carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every input that changes the outcome is in the hash</b>, on <c>IdempotencyFingerprint</c>'s precedent:
/// the operation, the project, the expected revision, the destructive allowance and the payload. A retry
/// that flipped <c>allowDestructive</c> is a different request and must not replay the refusal — or the
/// success. The operation is in it too, so one key spent on an apply cannot be answered with a rollback's
/// revision.
/// </para>
/// <para>
/// <b>The descriptor is hashed as sent, not canonicalised.</b> The Data API canonicalises a body because two
/// key orders are one record; a descriptor is stored verbatim and exported verbatim, so two spellings are
/// two different things to store and hashing them alike would replay the wrong text.
/// </para>
/// </remarks>
internal static class ManagementIdempotency
{
    /// <summary>The fingerprint of one management write.</summary>
    /// <param name="operation">The contract member being called.</param>
    /// <param name="project">The project being written to.</param>
    /// <param name="expectedRevision">The base the write is written against.</param>
    /// <param name="allowDestructive">Whether the caller allowed a plan that discards data.</param>
    /// <param name="payload">What the write carries — a descriptor, or the revision to restore.</param>
    /// <returns>A lower-case hex SHA-256 digest.</returns>
    internal static string FingerprintOf(
        string operation, string project, int expectedRevision, bool allowDestructive, string payload)
    {
        var input = new StringBuilder(operation)
            .Append('\n').Append(project)
            .Append('\n').Append(expectedRevision.ToString(CultureInfo.InvariantCulture))
            .Append('\n').Append(allowDestructive ? "true" : "false")
            .Append('\n').Append(payload);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
    }
}

/// <summary>One caller's key, resolved into the three things a record is filed under.</summary>
/// <param name="Key">The caller's key, verbatim.</param>
/// <param name="Scope">The caller's identity, from <c>AlvoIdempotency.IdentityOf</c>.</param>
/// <param name="Fingerprint">What makes this request the same request on a retry.</param>
internal readonly record struct ManagementIdempotencyToken(string Key, string Scope, string Fingerprint);
