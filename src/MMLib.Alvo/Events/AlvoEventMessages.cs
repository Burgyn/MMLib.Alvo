namespace MMLib.Alvo.Events;

/// <summary>A record was created.</summary>
/// <typeparam name="T">The record's shape: <c>AlvoRecord</c> in the untyped core.</typeparam>
public sealed record AlvoCreated<T>
    where T : class
{
    /// <summary>Gets the record as it was stored.</summary>
    public required T New { get; init; }
}

/// <summary>A record was updated or replaced.</summary>
/// <typeparam name="T">The record's shape: <c>AlvoRecord</c> in the untyped core.</typeparam>
public sealed record AlvoUpdated<T>
    where T : class
{
    /// <summary>Gets the record before the write.</summary>
    public required T Old { get; init; }

    /// <summary>Gets the record as it was stored.</summary>
    public required T New { get; init; }
}

/// <summary>A record was deleted.</summary>
/// <typeparam name="T">The record's shape: <c>AlvoRecord</c> in the untyped core.</typeparam>
public sealed record AlvoDeleted<T>
    where T : class
{
    /// <summary>Gets the record as it was before the delete.</summary>
    public required T Old { get; init; }
}

/// <summary>A record was created or updated.</summary>
/// <typeparam name="T">The record's shape: <c>AlvoRecord</c> in the untyped core.</typeparam>
public sealed record AlvoSaved<T>
    where T : class
{
    /// <summary>Gets the record as it was stored.</summary>
    public required T New { get; init; }

    /// <summary>Gets the record before the write, or <see langword="null"/> when it was created.</summary>
    public T? Old { get; init; }
}
