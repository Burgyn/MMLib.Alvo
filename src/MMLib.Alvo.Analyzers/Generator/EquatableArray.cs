using System.Collections;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>An <see cref="ImmutableArray{T}"/> compared by its items, so a model holding one is a value the generator's cache can compare.</summary>
/// <typeparam name="T">An item type with value equality.</typeparam>
internal readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items = items;

    public int Count => _items.IsDefault ? 0 : _items.Length;

    public T this[int index] => _items[index];

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new(items);

    public bool Equals(EquatableArray<T> other) => AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in AsSpan())
        {
            hash = unchecked((hash * 31) + (item?.GetHashCode() ?? 0));
        }

        return hash;
    }

    public ReadOnlySpan<T> AsSpan() => _items.IsDefault ? ReadOnlySpan<T>.Empty : _items.AsSpan();

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items.IsDefault ? ImmutableArray<T>.Empty : _items)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
