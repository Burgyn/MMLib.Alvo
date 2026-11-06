using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions;

/// <summary>Where a rendered field of one row is read from.</summary>
internal abstract record RowSource
{
    private RowSource()
    {
    }

    /// <summary>The top-level row of a read, as a bare column: the statement's own <c>FROM</c> names it.</summary>
    internal sealed record Unqualified : RowSource;

    /// <summary>A row a statement names: the outer table of a read, or a relation's alias.</summary>
    /// <param name="Qualifier">The table or alias every column is prefixed with.</param>
    internal sealed record Columns(string Qualifier) : RowSource;

    /// <summary>A row that is not stored yet: a write's post-image, each value bound as a parameter.</summary>
    /// <param name="Row">The post-image.</param>
    internal sealed record Image(AlvoRecord Row) : RowSource;
}

/// <summary>
/// The driver's half of a relation render: an unlocked table source per entity, and where the outer row is read from.
/// The core composes the correlated <c>EXISTS</c> around it.
/// </summary>
/// <param name="Table">The table an entity's rows are read from, with no lock.</param>
/// <param name="Outer">The row a relation's outer key and outer fields are read from.</param>
internal sealed record RelationSql(Func<EntitySchema, string> Table, RowSource Outer);
