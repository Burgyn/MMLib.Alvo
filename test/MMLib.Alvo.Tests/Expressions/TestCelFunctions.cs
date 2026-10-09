using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>Host-like functions and an entity for the function tests, built without the registration API.</summary>
internal static class TestCelFunctions
{
    /// <summary>Gets an entity with a field of every type a function test needs; two are named like built-ins.</summary>
    internal static EntitySchema Items { get; } = new()
    {
        Name = "items",
        Fields =
        [
            new FieldSchema { Name = "name", Type = FieldType.String, MaxLength = 200, Nullable = true },
            new FieldSchema { Name = "qty", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "price", Type = FieldType.Decimal, Precision = 18, Scale = 4, Nullable = true },
            new FieldSchema { Name = "due", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "ref_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "round", Type = FieldType.String, MaxLength = 20, Nullable = true },
            new FieldSchema { Name = "size", Type = FieldType.String, MaxLength = 20, Nullable = true },
        ],
    };

    /// <summary>Gets <c>echo(s: String) -> String</c>, which answers its argument.</summary>
    internal static CelFunction Echo => Host("echo", CelValueType.String, arguments => arguments[0], Parameter("s", CelValueType.String));

    /// <summary>Gets <c>pair(a: String, b: String) -> String</c>.</summary>
    internal static CelFunction Pair => Host(
        "pair", CelValueType.String, arguments => $"{arguments[0]}|{arguments[1]}",
        Parameter("a", CelValueType.String), Parameter("b", CelValueType.String));

    /// <summary>Gets <c>half(x: Decimal) -> Decimal</c>.</summary>
    internal static CelFunction Half => Host("half", CelValueType.Decimal, arguments => (decimal)arguments[0]! / 2, Parameter("x", CelValueType.Decimal));

    /// <summary>Gets <c>isBlank(s: String?) -> Bool</c>, the one parameter here that receives null.</summary>
    internal static CelFunction IsBlank => Host(
        "isBlank", CelValueType.Bool, arguments => string.IsNullOrWhiteSpace((string?)arguments[0]),
        Parameter("s", CelValueType.String, nullable: true));

    /// <summary>A host function with the Condition + Mutate profiles.</summary>
    internal static CelFunction Host(string name, CelValueType result, Func<object?[], object?> body, params CelFunctionArgument[] parameters) =>
        new(name, parameters, result, ResultNullable: true, $"Test function {name}.", IsHost: true, CelBuiltInFunctions.ConditionAndMutate, body);

    /// <summary>A parameter of a CEL type with the CLR type a body receives.</summary>
    internal static CelFunctionArgument Parameter(string name, CelValueType type, bool nullable = false) =>
        new(name, type, nullable, CelBuiltInFunctions.ClrTypeOf(type));

    /// <summary>The built-ins plus <paramref name="functions"/>.</summary>
    internal static CelFunctionCatalog With(params CelFunction[] functions) => CelFunctionCatalog.BuiltIns.With(functions);

    /// <summary>A compiler over the built-ins plus <paramref name="functions"/>.</summary>
    internal static CelCompiler Compiler(params CelFunction[] functions) => new(With(functions));

    /// <summary>Compiles against <see cref="Items"/>, or throws with every refusal.</summary>
    internal static CompiledExpression Compile(string source, CelProfile profile, params CelFunction[] functions)
    {
        var result = Compiler(functions).Compile(source, profile, Items);
        return result.IsSuccess
            ? result.Expression!
            : throw new InvalidOperationException(
                $"'{source}' did not compile as {profile}: {string.Join("; ", result.Errors.Select(error => error.Message))}");
    }
}
