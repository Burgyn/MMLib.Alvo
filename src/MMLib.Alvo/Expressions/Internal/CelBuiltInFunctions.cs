namespace MMLib.Alvo.Expressions.Internal;

/// <summary>The functions every Alvo build knows, whatever the host registers.</summary>
/// <remarks>
/// Every member is an expression-bodied getter rather than a <c>static readonly</c> field: a static initializer runs
/// once per test-host process, so a mutant inside one is invisible to every test (the #244 comment), and these sets
/// are profile gates in the security core.
/// </remarks>
internal static class CelBuiltInFunctions
{
    /// <summary>Gets the profile set of the two legacy calls.</summary>
    internal static IReadOnlySet<CelProfile> MutateOnly => new HashSet<CelProfile> { CelProfile.Mutate };

    /// <summary>Gets the profile set of every in-process function in this slice — built-ins and host functions alike.</summary>
    internal static IReadOnlySet<CelProfile> ConditionAndMutate =>
        new HashSet<CelProfile> { CelProfile.Condition, CelProfile.Mutate };

    /// <summary>Gets every built-in overload.</summary>
    internal static IReadOnlyList<CelFunction> All => [LowerAscii, Now];

    private static CelFunction LowerAscii => new(
        CelCall.LowerAscii, [Parameter("value", CelValueType.String)], CelValueType.String, ResultNullable: true,
        "Folds A-Z to a-z in a field's text and changes nothing else; takes a field, never an expression.",
        IsHost: false, MutateOnly, Body: null);

    private static CelFunction Now => new(
        CelCall.Now, [], CelValueType.Timestamp, ResultNullable: false,
        "The instant this write is stamped with — the same one its audit columns get, never a clock read.",
        IsHost: false, MutateOnly, Body: null);

    /// <summary>A non-nullable parameter of a CEL type, with the CLR type a body receives for it.</summary>
    /// <param name="name">The parameter's name.</param>
    /// <param name="type">Its CEL type.</param>
    /// <returns>The parameter.</returns>
    internal static CelFunctionArgument Parameter(string name, CelValueType type) => new(name, type, Nullable: false, ClrTypeOf(type));

    /// <summary>The CLR type a function body receives for a CEL type.</summary>
    /// <param name="type">A scalar CEL type.</param>
    /// <returns>The CLR type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a type a function parameter can have.</exception>
    internal static System.Type ClrTypeOf(CelValueType type) => type switch
    {
        CelValueType.String => typeof(string),
        CelValueType.Int => typeof(long),
        CelValueType.Decimal => typeof(decimal),
        CelValueType.Bool => typeof(bool),
        CelValueType.Timestamp => typeof(DateTimeOffset),
        CelValueType.Uuid => typeof(Guid),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No CEL function parameter has this type."),
    };
}
