using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Hooks;

/// <summary>
/// A host write hook refused the write: nothing was written and no event was emitted. Over HTTP it is answered 422, 409 or
/// 403 by its <see cref="Kind"/>, with every violation of that kind; in a batch it is the refused row's refusal.
/// </summary>
/// <remarks>
/// Alvo raises it from what a hook records on its context (<c>Invalid</c>, <c>Conflict</c>, <c>Forbidden</c>). A hook that
/// <em>throws</em> one itself has failed like any other exception — 500 <c>hook-failed</c>, not a rejection.
/// </remarks>
public sealed class AlvoWriteRejectedException : Exception, IRowRefusal
{
    /// <summary>Initializes a new instance of the <see cref="AlvoWriteRejectedException"/> class.</summary>
    public AlvoWriteRejectedException()
        : this("A write hook refused the write.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoWriteRejectedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public AlvoWriteRejectedException(string message)
        : base(message)
    {
        ExtensionId = string.Empty;
        Violations = [];
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoWriteRejectedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoWriteRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ExtensionId = string.Empty;
        Violations = [];
    }

    internal AlvoWriteRejectedException(AlvoWriteRejectionKind kind, string extensionId, IReadOnlyList<AlvoViolation> violations)
        : base($"The write hook '{extensionId}' refused the write: {Messages(violations)}")
    {
        Kind = kind;
        ExtensionId = extensionId;
        Violations = violations;
    }

    /// <summary>Gets how the hook refused the write.</summary>
    public AlvoWriteRejectionKind Kind { get; }

    /// <summary>Gets the first violation's code.</summary>
    public string Code => Violations.Count > 0 ? Violations[0].Code : string.Empty;

    /// <summary>Gets the first violation's JSON pointer: the field it names, or empty for the whole body or a hidden field.</summary>
#pragma warning disable CA1720 // "Pointer" is RFC 6901's own term for this member, not the CLR's.
    public string Pointer => Violations.Count > 0 ? Violations[0].Pointer : string.Empty;
#pragma warning restore CA1720

    /// <summary>Gets the id of the hook that refused.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets every violation of <see cref="Kind"/> the hook recorded, in the order it recorded them.</summary>
    public IReadOnlyList<AlvoViolation> Violations { get; }

    /// <inheritdoc/>
    AlvoRowRefusal IRowRefusal.At(int index) => new(index, Code, Messages(Violations), null);

    private static string Messages(IReadOnlyList<AlvoViolation> violations) =>
        string.Join(" ", violations.Select(violation => violation.Message).Distinct(StringComparer.Ordinal));
}
