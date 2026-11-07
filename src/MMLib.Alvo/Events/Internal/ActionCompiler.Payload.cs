using MMLib.Alvo.Api.Internal;
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
/// JSON <c>null</c> entry is the null literal it was written as (#328), refused into a required field, on a create and an
/// update alike, since the write would refuse it every time.
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
        if (IsTemplateString(value))
        {
            scope.Errors.Add(Error(slot, TemplateInPayload, TemplateInPayloadFix));
            return null;
        }

        return value.IsExpression
            ? CompilePayloadExpression(value.Expression!, field, slot, scope)
            : CompilePayloadLiteral(value.Literal!.Value, field, target, slot, scope);
    }

    private static bool IsTemplateString(ValueOrExpr value) =>
        value is { IsExpression: false, Literal: { ValueKind: JsonValueKind.String } literal }
        && AlvoTemplate.TryParse(literal.GetString()!, out var template, out _)
        && template!.Placeholders.Count > 0;

    private const string TemplateInPayload =
        "This value uses '{{…}}', which an 'entity.update' payload does not render in this build, so it would be written verbatim.";

    private const string TemplateInPayloadFix = "Write {\"$cel\": \"…\"} instead, e.g. {\"$cel\": \"new.number\"}.";

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
        var converted = isNull ? null : FieldLiteral.Convert(literal, CelFieldType.Of(field));
        var refusal = isNull
            ? (field.Required ? $"Field '{field.Name}' is required on '{target.Entity.Name}', so an 'entity.update' cannot set it to null." : null)
            : converted is null
                ? $"This literal is a JSON {literal.ValueKind.ToString().ToLowerInvariant()}, and '{field.Name}' is declared as '{field.Type}'."
                : null;
        if (refusal is not null)
        {
            scope.Errors.Add(Error(path, refusal, isNull ? RequiredNullFix(target) : $"Write a literal a '{field.Type}' field holds, or a {{\"$cel\": \"…\"}} that yields one."));
            return null;
        }

        return HonoursTheFacets(converted, PayloadFacets.TargetOf(field, target.Entity, target.Host.Descriptor), path, scope)
            ? new CompiledValue(literal, Expression: null)
            : null;
    }

    /// <summary>
    /// Whether a converted literal honours the target field's declared facets — Ruling Q22: an <c>entity.update</c> payload
    /// is measured exactly as a before-hook <c>mutate</c> is (Ruling V), so a literal that breaks one is refused at apply.
    /// </summary>
    private static bool HonoursTheFacets(object? value, MutationTarget target, string path, AfterHookScope scope)
    {
        if (target.Violation(value) is not { } violation)
        {
            return true;
        }

        scope.Errors.Add(Error(
            path,
            $"This literal breaks the '{violation.Code}' facet '{target.Field.Name}' declares: {violation.Message}",
            violation.FixSuggestion));
        return false;
    }

    /// <summary>A create that leaves out a required field the framework does not fill: the write would refuse it every time.</summary>
    private static IEnumerable<DescriptorValidationError> MissingRequired(
        EntityUpdateAction update, EntitySchema target, ActionHost host, string path) =>
        target.Fields
            .Where(field => field.Required && field.Default is null && FrameworkWritten(field) is null)
            .Where(field => !AlvoManagedColumns.For(target).Contains(field.Name) && !update.Payload.ContainsKey(field.Name))
            .Where(field => !IsReadOnly(host.Descriptor, target.Name, field.Name))
            .Select(field => Error(
                path,
                $"This 'entity.update' creates a '{target.Name}' record and leaves out '{field.Name}', which is required, so every write would be refused.",
                $"Add '{field.Name}' to the payload, or name the record to update with 'recordId'."));

    private static string RequiredNullFix(PayloadTarget target) => target.IsCreate
        ? "Write a value; a create must fill every required field."
        : "Write a value, or remove the entry so the update leaves the field as it is.";
}
