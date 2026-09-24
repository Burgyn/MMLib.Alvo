namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>Where a field's value comes from — chosen instead of a type rather than beside one (docs/todo-admin.md §7).</summary>
internal enum FieldKind
{
    /// <summary>A caller writes it, within its type and constraints.</summary>
    Supplied,

    /// <summary>Alvo maintains it from the rows of an entity that points here (<c>rollup</c>).</summary>
    Rollup,

    /// <summary>The database computes it from this row's own fields (<c>computed</c>).</summary>
    Computed,
}
