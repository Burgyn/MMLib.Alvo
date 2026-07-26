using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Auth;

/// <summary>The access an <see cref="ApiKeyScope"/> grants: reading or writing data.</summary>
public enum ScopeAccess
{
    /// <summary>Grants <see cref="DataOperation.List"/> and <see cref="DataOperation.Get"/>.</summary>
    Read,

    /// <summary>
    /// Grants <see cref="DataOperation.Create"/>, <see cref="DataOperation.Update"/> and
    /// <see cref="DataOperation.Delete"/>. Does not imply <see cref="Read"/> — a caller that
    /// needs both must be granted both explicitly.
    /// </summary>
    Write,
}

/// <summary>
/// A single grant on an API key: an entity name (or <c>*</c> for every entity) paired with
/// read or write access. Parsed from the descriptor form <c>"&lt;entity|*&gt;:&lt;read|write&gt;"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here compares ordinally</b> — byte-for-byte, case-sensitive, culture-invariant, the
/// same rule <c>CelInterpreter</c> and the schema registry follow. So <c>orders:read</c> grants
/// nothing on an entity named <c>Orders</c>, and <c>orders:READ</c> does not parse at all. That is
/// deliberate: a scope is matched against a descriptor entity name, and a case-insensitive match
/// would silently widen a grant on any engine whose identifiers are case-sensitive.
/// </para>
/// <para>
/// A grant is <b>exact, never hierarchical</b>: <see cref="ScopeAccess.Write"/> does not imply
/// <see cref="ScopeAccess.Read"/>, and an unrecognized <see cref="DataOperation"/> maps to no access
/// level at all rather than to a guessed one, so it is refused. A caller needing both must be granted
/// both.
/// </para>
/// </remarks>
public readonly record struct ApiKeyScope
{
    private const char Separator = ':';
    private const string Wildcard = "*";

    /// <summary>Gets the entity this scope applies to, or <c>*</c> for every entity.</summary>
    public required string Entity { get; init; }

    /// <summary>Gets the access this scope grants.</summary>
    public required ScopeAccess Access { get; init; }

    /// <summary>Parses the descriptor form <c>"&lt;entity|*&gt;:&lt;read|write&gt;"</c>.</summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="scope">The parsed scope when parsing succeeds.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed scope.</returns>
    public static bool TryParse(string value, out ApiKeyScope scope)
    {
        scope = default;

        var span = value.AsSpan();
        var separatorIndex = span.IndexOf(Separator);
        if (separatorIndex < 0 || span[(separatorIndex + 1)..].Contains(Separator))
        {
            return false;
        }

        var entity = span[..separatorIndex];
        var access = span[(separatorIndex + 1)..];
        if (entity.IsEmpty || !TryParseAccess(access, out var parsedAccess))
        {
            return false;
        }

        scope = new ApiKeyScope { Entity = entity.ToString(), Access = parsedAccess };
        return true;
    }

    /// <summary>
    /// Answers whether this scope allows <paramref name="operation"/> against <paramref name="entity"/>.
    /// An unrecognized <paramref name="operation"/> is denied rather than mapped to a guessed access
    /// level.
    /// </summary>
    /// <param name="entity">The entity the operation targets.</param>
    /// <param name="operation">The operation being performed.</param>
    public bool Allows(string entity, DataOperation operation) =>
        TryGetRequiredAccess(operation, out var requiredAccess)
        && (Entity == Wildcard || string.Equals(Entity, entity, StringComparison.Ordinal))
        && Access == requiredAccess;

    private static bool TryParseAccess(ReadOnlySpan<char> text, out ScopeAccess access)
    {
        if (text.Equals("read", StringComparison.Ordinal))
        {
            access = ScopeAccess.Read;
            return true;
        }

        if (text.Equals("write", StringComparison.Ordinal))
        {
            access = ScopeAccess.Write;
            return true;
        }

        access = default;
        return false;
    }

    private static bool TryGetRequiredAccess(DataOperation operation, out ScopeAccess access)
    {
        switch (operation)
        {
            case DataOperation.List:
            case DataOperation.Get:
                access = ScopeAccess.Read;
                return true;
            case DataOperation.Create:
            case DataOperation.Update:
            case DataOperation.Delete:
                access = ScopeAccess.Write;
                return true;
            default:
                access = default;
                return false;
        }
    }
}
