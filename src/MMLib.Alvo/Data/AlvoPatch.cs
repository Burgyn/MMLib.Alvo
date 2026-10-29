using MMLib.Alvo.Data.Internal;

namespace MMLib.Alvo.Data;

/// <summary>Starts a typed patch: <c>AlvoPatch.For&lt;Invoice&gt;().Set(x =&gt; x.Paid, true)</c>.</summary>
public static class AlvoPatch
{
    /// <summary>An empty patch of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <returns>A patch that sets nothing yet.</returns>
    public static IAlvoPatch<T> For<T>()
        where T : class => new TypedPatch<T>();
}
