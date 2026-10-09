namespace MMLib.Alvo.Expressions.Internal;

/// <summary>One host function, as <c>AddCelFunction</c> registers it; the catalog collects every instance.</summary>
/// <param name="Function">The catalogued function, already validated.</param>
internal sealed record CelFunctionRegistration(CelFunction Function);
