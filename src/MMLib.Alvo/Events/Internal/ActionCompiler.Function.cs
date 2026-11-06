using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Functions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;

namespace MMLib.Alvo.Events.Internal;

/// <summary>The <c>function</c> arm: the name resolved against the host's registrations, the input against its schema.</summary>
internal static partial class ActionCompiler
{
    private const string NameSlot = "name";
    private const string InputSlot = "input";

    private const string ObjectInputFix = "Write the input as an object of literals and {\"$cel\": \"…\"} values instead.";

    private static readonly Dictionary<string, ValueOrExpr?> _noInput = new(StringComparer.Ordinal);

    private static CompiledAction? CompileFunction(FunctionAction function, string path, AfterHookScope scope)
    {
        var host = scope.Host?.Host ?? DescriptorHost.Empty;
        if (!host.Functions.TryGetValue(function.Name, out var registration))
        {
            scope.Errors.Add(UnregisteredFunction(function.Name, path, host, scope.Host?.Descriptor));
            return null;
        }

        if (function.Input?.Transform is not null)
        {
            var refusal = UnhonouredFeatures.RawJsonata;
            scope.Errors.Add(Error($"{path}/{InputSlot}", refusal.Consequence, $"{refusal.Fix} {ObjectInputFix}"));
            return null;
        }

        return CompileFunctionInput(function, registration, path, scope);
    }

    private static CompiledAction? CompileFunctionInput(
        FunctionAction function, FunctionRegistration registration, string path, AfterHookScope scope)
    {
        var fields = function.Input?.Fields ?? _noInput;
        var problems = FunctionInputCheck.Check(path, registration, fields).ToList();
        scope.Errors.AddRange(problems);
        var compiled = fields
            .Where(field => registration.Schema.Properties.ContainsKey(field.Key))
            .ToDictionary(field => field.Key, field => CompileInputValue(field.Key, field.Value, registration, path, scope), StringComparer.Ordinal);

        return problems.Count > 0 || compiled.Values.Any(value => value is null)
            ? null
            : new CompiledAction(function, _noTemplates, Endpoint: null, ActionType.Function)
            {
                Function = registration,
                Fields = compiled.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal),
            };
    }

    private static CompiledValue? CompileInputValue(
        string name, ValueOrExpr? value, FunctionRegistration registration, string path, AfterHookScope scope)
    {
        if (value is not { IsExpression: true })
        {
            return new CompiledValue((value ?? _nullLiteral).Literal, Expression: null);
        }

        var slot = $"{path}/{InputSlot}/{name}";
        if (CompileRowExpression(value.Expression!, slot, scope) is not { } expression)
        {
            return null;
        }

        var types = registration.Schema.Properties[name];
        if (FunctionInputCheck.Accepts(types, expression.ResultType))
        {
            return new CompiledValue(Literal: null, expression);
        }

        scope.Errors.Add(FunctionInputCheck.WrongExpressionType(path, registration.Id, name, types, expression.ResultType));
        return null;
    }

    /// <summary>
    /// Compiles a value expression in <see cref="CelProfile.Mutate"/> over the triggering row, refusing a read of a row
    /// image the point's event does not carry and of a field the entity leaves out of its event images.
    /// </summary>
    private static CompiledExpression? CompileRowExpression(string source, string path, AfterHookScope scope)
    {
        var result = scope.Compiler.Compile(source, CelProfile.Mutate, scope.Schema);
        if (!result.IsSuccess)
        {
            scope.Errors.AddRange(result.Errors.Select(error => Error(path, error.Message, error.FixSuggestion)));
            return null;
        }

        var expression = result.Expression!;
        IReadOnlyList<DescriptorValidationError> refusals =
        [
            .. AfterImagePhase.Refusals(expression.Root, AfterImages.At(scope.Point), scope.Point, path),
            .. EventConditionReads.ExcludedFieldsRead(expression.Root, scope.Schema).Select(field => Error(
                path, ExcludedValueRead(field), ExcludedPlaceholderFix(scope, field))),
        ];
        scope.Errors.AddRange(refusals);

        return refusals.Count == 0 ? expression : null;
    }

    private static DescriptorValidationError UnregisteredFunction(
        string name, string path, DescriptorHost host, AlvoDescriptor? descriptor)
    {
        var registered = host.Functions.Keys.Order(StringComparer.Ordinal).ToList();

        return Error(
            $"{path}/{NameSlot}",
            $"'{name}' is not a function this host registers.",
            descriptor?.Functions?.ContainsKey(name) == true
                ? $"'{name}' is declared under 'functions', a csx script this build does not run; register a host function instead."
                : UnregisteredFunctionFix(name, registered));
    }

    private static string UnregisteredFunctionFix(string name, List<string> registered) =>
        NameSuggestion.Closest(name, registered) is { } closest
            ? $"Did you mean '{closest}'?"
            : registered.Count == 0
                ? $"Register it with AddFunction<T>(\"{name}\")."
                : $"Register it with AddFunction<T>(\"{name}\"), or use one of: {string.Join(", ", registered)}.";

    private static string ExcludedValueRead(string field) =>
        $"This value reads '{field}', which this entity leaves out of its event images (events.exclude), so it would read "
        + "null on every delivery.";
}
