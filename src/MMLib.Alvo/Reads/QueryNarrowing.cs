using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Reads;

/// <summary>The read a narrowing is asked about. It carries no HTTP request: a narrowing binds to the data operation.</summary>
public sealed class QueryNarrowing
{
    internal QueryNarrowing(AlvoContext caller, string entity, DataOperation operation)
    {
        Caller = caller;
        Entity = entity;
        Operation = operation;
    }

    /// <summary>Gets the caller the read is for — never the system, which is not narrowed.</summary>
    public AlvoContext Caller { get; }

    /// <summary>Gets the entity being read.</summary>
    public string Entity { get; }

    /// <summary>
    /// Gets <see cref="DataOperation.List"/> (a page and its count) or <see cref="DataOperation.Get"/> (a get, or a write's
    /// echo).
    /// </summary>
    public DataOperation Operation { get; }
}
