using System.Reflection;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Turns a host's <see cref="Delegate"/> into a catalogued <see cref="CelFunction"/>, refusing at registration — as an
/// <see cref="ArgumentException"/> at the <c>AddCelFunction</c> call — everything the catalog cannot honour (spec §5.7).
/// </summary>
/// <remarks>
/// The call signature is the delegate type's <c>Invoke</c>, which is right for every delegate shape; parameter names and
/// <c>string?</c> annotations come from <see cref="Delegate.Method"/> when its arity matches (a lambda or a method
/// group). An oblivious nullability context reads as non-nullable — the safe side: null-propagation.
/// </remarks>
internal static partial class HostCelFunction
{
    /// <summary>The most parameters a host function may take (spec R3).</summary>
    internal const int MaxParameters = 4;

    private const string SupportedTypes =
        "a CEL function's parameters and result are string, long, int, decimal, bool, DateTimeOffset or Guid, or a nullable one of those";

    /// <summary>The catalogued function for <paramref name="function"/>.</summary>
    /// <param name="name">The CEL name.</param>
    /// <param name="function">The implementation.</param>
    /// <param name="summary">One sentence for discovery, or <see langword="null"/>.</param>
    /// <returns>The function, with the Condition and Mutate profiles.</returns>
    /// <exception cref="ArgumentException">The name or the delegate cannot be catalogued.</exception>
    internal static CelFunction Create(string name, Delegate function, string? summary)
    {
        EnsureValidName(name);
        ArgumentNullException.ThrowIfNull(function);
        var invoke = InvokeMethodOf(function, name);
        var (result, resultNullable) = Result(function, invoke, name);

        return new CelFunction(
            name, Parameters(function, invoke, name), result, resultNullable, summary ?? string.Empty,
            IsHost: true, CelBuiltInFunctions.ConditionAndMutate,
            arguments => invoke.Invoke(function, BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null));
    }

    private static void EnsureValidName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a CEL function name: start with a lower-case ASCII letter and continue with ASCII "
                + "letters, digits or '_', for example normalizePhone or vat_rate.",
                nameof(name));
        }

        if (ReservedReason(name) is { } reason)
        {
            throw new ArgumentException($"'{name}' cannot name a host function: {reason}.", nameof(name));
        }
    }

    private static string? ReservedReason(string name) => name switch
    {
        "has" or "changed" => "it is one of Alvo's own CEL macros",
        "in" or "true" or "false" or "null" => "it is a CEL keyword",
        "old" or "new" => "it names a row image (old./new.)",
        "all" or "exists" or "exists_one" or "map" or "filter" => "it is a CEL comprehension macro, which no Alvo profile admits",
        "as" or "break" or "const" or "continue" or "else" or "for" or "function" or "if" or "import" or "let"
            or "loop" or "package" or "namespace" or "return" or "var" or "void" or "while" => "it is a word the CEL specification reserves",
        _ when CelFunctionCatalog.BuiltIns.Contains(name) => "it is a built-in function",
        _ => null,
    };

    private static MethodInfo InvokeMethodOf(Delegate function, string name)
    {
        var targets = function.GetInvocationList().Length;
        if (targets != 1)
        {
            throw Refused(name, $"was given a delegate that calls {targets} methods; a CEL function is one method");
        }

        return function.Method.ContainsGenericParameters
            ? throw Refused(name, "is an open generic method; close it over concrete types")
            : function.GetType().GetMethod("Invoke")!;
    }

    private static List<CelFunctionArgument> Parameters(Delegate function, MethodInfo invoke, string name)
    {
        var signature = invoke.GetParameters();
        if (signature.Length > MaxParameters)
        {
            throw Refused(name, $"takes {signature.Length} parameters; a CEL function takes at most {MaxParameters}");
        }

        var declared = function.Method.GetParameters();
        var annotated = declared.Length == signature.Length ? declared : signature;
        return [.. signature.Select((parameter, index) => Parameter(parameter, annotated[index], name))];
    }

    private static CelFunctionArgument Parameter(ParameterInfo parameter, ParameterInfo annotated, string name)
    {
        var clr = parameter.ParameterType;
        if (clr.IsByRef || TypeOf(clr) is not { } type)
        {
            throw Refused(name, $"has parameter '{annotated.Name}' of type {clr.Name}; {SupportedTypes}");
        }

        return new CelFunctionArgument(annotated.Name ?? $"arg{parameter.Position}", type, IsNullable(clr, annotated), Underlying(clr));
    }

    private static (CelValueType Type, bool Nullable) Result(Delegate function, MethodInfo invoke, string name)
    {
        var returned = invoke.ReturnType;
        if (returned == typeof(void))
        {
            throw Refused(name, "returns nothing; a CEL function returns a value");
        }

        if (IsAsynchronous(returned))
        {
            throw Refused(name, "is asynchronous; a CEL function runs inside the write's transaction and cannot await — do I/O in an after-hook instead");
        }

        var type = TypeOf(returned) ?? throw Refused(name, $"returns {returned.Name}; {SupportedTypes}");
        var annotated = function.Method.ReturnType == returned ? function.Method.ReturnParameter : invoke.ReturnParameter;
        return (type, IsNullable(returned, annotated));
    }

    private static bool IsAsynchronous(System.Type type) =>
        typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>));

    private static CelValueType? TypeOf(System.Type clr) => Underlying(clr) switch
    {
        var t when t == typeof(string) => CelValueType.String,
        var t when t == typeof(long) || t == typeof(int) => CelValueType.Int,
        var t when t == typeof(decimal) => CelValueType.Decimal,
        var t when t == typeof(bool) => CelValueType.Bool,
        var t when t == typeof(DateTimeOffset) => CelValueType.Timestamp,
        var t when t == typeof(Guid) => CelValueType.Uuid,
        _ => (CelValueType?)null,
    };

    private static System.Type Underlying(System.Type clr) => Nullable.GetUnderlyingType(clr) ?? clr;

    private static bool IsNullable(System.Type clr, ParameterInfo annotated) =>
        Nullable.GetUnderlyingType(clr) is not null
        || (clr == typeof(string) && new NullabilityInfoContext().Create(annotated).ReadState == NullabilityState.Nullable);

#pragma warning disable CA2208 // "function" is AddCelFunction's parameter, the one the caller handed in.
    private static ArgumentException Refused(string name, string reason) => new($"The CEL function '{name}' {reason}.", "function");
#pragma warning restore CA2208

    /// <summary>R1's pattern, anchored with <c>\z</c>: <c>$</c> would admit a trailing newline in .NET.</summary>
    [GeneratedRegex(@"^[a-z][a-zA-Z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
