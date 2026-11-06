using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Reads.Internal;
using MMLib.Alvo.Rules;

using System.Linq.Expressions;

namespace MMLib.Alvo.Reads;

/// <summary>One returned row an enricher may shape: hide a field, or fill a virtual one. Nothing else.</summary>
/// <typeparam name="T">The row's type: a mapped class, or <see cref="AlvoRecord"/>.</typeparam>
public sealed class Enrich<T>
    where T : class
{
    private readonly RowShaping _row;

    internal Enrich(RowShaping row, AlvoContext caller, DataOperation operation)
    {
        _row = row;
        Caller = caller;
        Operation = operation;
        Row = row.As<T>();
    }

    /// <summary>Gets the row as the caller receives it so far — a field the caller may not read is absent (a typed property reads its default).</summary>
    public T Row { get; }

    /// <summary>Gets the caller the row is returned to; the system too, which is enriched like any caller.</summary>
    public AlvoContext Caller { get; }

    /// <summary>Gets <see cref="DataOperation.Get"/>, <see cref="DataOperation.List"/>, or <see cref="DataOperation.Create"/>/<see cref="DataOperation.Update"/> for a write's echo.</summary>
    public DataOperation Operation { get; }

    /// <summary>Gets the entity the row belongs to.</summary>
    public string Entity => _row.Entity;

    /// <summary>Removes a field from this row. A framework column cannot be hidden.</summary>
    /// <param name="field">The field's name.</param>
    public void Hide(string field) => _row.Hide(field);

    /// <summary>Removes the field a mapped property names from this row. A framework column cannot be hidden.</summary>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="field">The property, such as <c>x =&gt; x.Region</c>.</param>
    public void Hide<TValue>(Expression<Func<T, TValue>> field) => _row.Hide(TypedField.Of(field));

    /// <summary>Fills a field the descriptor declares virtual; a no-op when this row does not carry it (hidden from the caller, or not selected).</summary>
    /// <param name="field">The virtual field's name.</param>
    /// <param name="value">A value its type holds, or <see langword="null"/>.</param>
    public void SetVirtual(string field, object? value) => _row.SetVirtual(field, value);

    /// <summary>Fills the virtual field a mapped property names; a no-op when this row does not carry it.</summary>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="field">The property, such as <c>x =&gt; x.Display</c>.</param>
    /// <param name="value">The value, converted as a typed write converts it.</param>
    public void SetVirtual<TValue>(Expression<Func<T, TValue>> field, TValue value) => _row.SetVirtual(TypedField.Of(field), value);
}
