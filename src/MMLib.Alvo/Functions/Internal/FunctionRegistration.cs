using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Functions.Internal;

/// <summary>A host function: the class a <c>function</c> action names, the input it binds, and that input's schema.</summary>
/// <param name="Id">The function's name, which is its id in the one extension namespace.</param>
/// <param name="Sequence">Its place among every registration of this host, in call order.</param>
/// <param name="Function">The class that implements it, resolved keyed by <paramref name="Id"/>.</param>
/// <param name="Input">The type its input binds to.</param>
/// <param name="Schema">The JSON Schema <paramref name="Input"/> exports, as the apply check reads it.</param>
internal sealed record FunctionRegistration(string Id, int Sequence, Type Function, Type Input, FunctionInputSchema Schema)
    : ExtensionRegistration(Id, Sequence)
{
    /// <summary>The kind a function is listed and refused as.</summary>
    internal const string Kind = "function";
}
