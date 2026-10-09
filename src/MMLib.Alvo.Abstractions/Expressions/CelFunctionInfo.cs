using System.Text.Json.Serialization;

namespace MMLib.Alvo.Expressions;

/// <summary>Where a CEL function comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CelFunctionProvenance>))]
public enum CelFunctionProvenance
{
    /// <summary>Shipped with Alvo: every host, the standalone image and the CLI know it.</summary>
    BuiltIn,

    /// <summary>Registered by the embedding host with <c>AddCelFunction</c>: only that host knows it, and it runs host code.</summary>
    Host,
}

/// <summary>One parameter of a CEL function overload, as discovery shows it.</summary>
public sealed record CelFunctionParameter
{
    /// <summary>Gets the parameter's name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the CEL type an argument must have; an Int also binds a Decimal parameter.</summary>
    public required CelValueType Type { get; init; }

    /// <summary>
    /// Gets a value indicating whether the function receives a null argument. When it does not, a null argument makes
    /// the call's value null and the function is not invoked.
    /// </summary>
    public required bool AcceptsNull { get; init; }
}

/// <summary>
/// One overload of a CEL function a descriptor may call. A name with several overloads (<c>abs</c>, <c>round</c>)
/// appears once per overload; a host function has exactly one.
/// </summary>
/// <remarks>
/// Init-only rather than positional, unlike the Management records beside it: this is the description most likely to
/// grow (examples, a "since", a summary key), and an added optional init member is additive where an added positional
/// parameter is not.
/// </remarks>
public sealed record CelFunctionInfo
{
    /// <summary>Gets the function's CEL spelling.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the parameters, in call order.</summary>
    public required IReadOnlyList<CelFunctionParameter> Parameters { get; init; }

    /// <summary>Gets the CEL type of the value the call yields.</summary>
    public required CelValueType Result { get; init; }

    /// <summary>Gets a value indicating whether the call may yield null even when every argument is present.</summary>
    public required bool ResultMayBeNull { get; init; }

    /// <summary>Gets one sentence on what the function does; empty when the host gave none.</summary>
    public required string Summary { get; init; }

    /// <summary>Gets where the function comes from.</summary>
    public required CelFunctionProvenance Provenance { get; init; }

    /// <summary>Gets the profiles a call to it compiles in.</summary>
    public required IReadOnlyList<CelProfile> Profiles { get; init; }
}
