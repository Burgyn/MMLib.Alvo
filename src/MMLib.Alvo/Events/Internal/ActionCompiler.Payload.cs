using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// One <c>entity.update</c> payload entry: a field of the target the framework does not own, and a value of its type — a
/// JSON <c>null</c> entry is the null literal it was written as (#328), refused only where a create would leave a required
/// field empty.
/// </summary>
internal static partial class ActionCompiler
{
    private static readonly Dictionary<string, AlvoTemplate> _noTemplates = new(StringComparer.Ordinal);

    private static readonly ValueOrExpr _nullLiteral = ValueOrExpr.FromLiteral(JsonElement.Parse("null"));

    private static CompiledValue? CompilePayloadValue(
        string name, ValueOrExpr? value, PayloadTarget target, string path, AfterHookScope scope)
    {
        var slot = $"{path}/{name}";
        if (WritableField(name, target, slot, scope) is not { } field)
        {
            return null;
        }

        value ??= _nullLiteral;
        return value.IsExpression
            ? CompilePayloadExpression(value.Expression!, field, slot, scope)
            : CompilePayloadLiteral(value.Literal!.Value, field, target, slot, scope);
    }

    private static FieldSchema? WritableField(string name, PayloadTarget target, string path, AfterHookScope scope)
    {
        var entity = target.Entity;
        var field = entity.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        var refusal = AlvoManagedColumns.For(entity).Contains(name) ? ManagedTarget(entity.Name, name)
            : field is null ? null
            : FrameworkWritten(field) is { } kind ? $"Field '{entity.Name}.{name}' is {kind}, so the framework writes it and an 'entity.update' may not."
            : IsReadOnly(target.Host.Descriptor, entity.Name, name) ? $"Field '{entity.Name}.{name}' is readOnly, so an 'entity.update' may not write it."
            : null;
        if (field is null && refusal is null)
        {
            scope.Errors.Add(UnknownPayloadField(name, entity, path));
            return null;
        }

        if (refusal is null)
        {
            return field;
        }

        scope.Errors.Add(Error(path, refusal, $"Remove '{name}' from the payload."));
        return null;
    }

    private static string? FrameworkWritten(FieldSchema field) =>
        field.IsVirtual ? "virtual"
        : field.ComputedExpression is not null ? "computed"
        : field.Rollup is not null ? "a rollup"
        : field.Sequence is not null ? "a sequence"
        : null;

    private static bool IsReadOnly(AlvoDescriptor descriptor, string entity, string field) =>
        descriptor.Entities.GetValueOrDefault(entity)?.Fields?.GetValueOrDefault(field)?.ReadOnly is { } flag
        && flag.Boolean != false;

    private static string ManagedTarget(string entity, string field) =>
        $"'{field}' is a column the framework manages on '{entity}', so an 'entity.update' may not write it.";

    private static DescriptorValidationError UnknownPayloadField(string name, EntitySchema entity, string path)
    {
        var declared = entity.Fields.Select(field => field.Name).Order(StringComparer.Ordinal).ToList();
        var known = $"Declared fields: {string.Join(", ", declared)}.";

        return Error(
            path,
            $"'{name}' is not a field of '{entity.Name}', so this payload entry could never be written.",
            NameSuggestion.Closest(name, declared) is { } closest ? $"Did you mean '{closest}'? {known}" : known);
    }

    private static CompiledValue? CompilePayloadExpression(string source, FieldSchema field, string path, AfterHookScope scope)
    {
        if (CompileRowExpression(source, path, scope) is not { } expression)
        {
            return null;
        }

        var declared = CelFieldType.Of(field);
        if (expression.ResultType == declared || (declared == CelValueType.Decimal && expression.ResultType == CelValueType.Int))
        {
            return new CompiledValue(Literal: null, expression);
        }

        scope.Errors.Add(Error(
            path,
            $"This value evaluates to {expression.ResultType}, and '{field.Name}' is declared as '{field.Type}', which holds {declared}.",
            $"Produce a {declared} value, or write a field declared to hold {expression.ResultType}."));
        return null;
    }

    private static CompiledValue? CompilePayloadLiteral(
        JsonElement literal, FieldSchema field, PayloadTarget target, string path, AfterHookScope scope)
    {
        var isNull = literal.ValueKind == JsonValueKind.Null;
        var refusal = isNull
            ? (field.Required && target.IsCreate ? $"Field '{field.Name}' is required on '{target.Entity.Name}', so a create cannot set it to null." : null)
            : FieldLiteral.Convert(literal, CelFieldType.Of(field)) is null
                ? $"This literal is a JSON {literal.ValueKind.ToString().ToLowerInvariant()}, and '{field.Name}' is declared as '{field.Type}'."
                : null;
        if (refusal is null)
        {
            return new CompiledValue(literal, Expression: null);
        }

        scope.Errors.Add(Error(path, refusal, isNull ? RequiredNullFix : $"Write a literal a '{field.Type}' field holds, or a {{\"$cel\": \"…\"}} that yields one."));
        return null;
    }

    private const string RequiredNullFix = "Write a value, or name a 'recordId' so the action updates a record instead of creating one.";
}
