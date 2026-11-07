using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The <c>entity.update</c> arm: a target entity of the descriptor, a payload of fields the framework does not own, values of
/// the target field's type, and a record id that is a UUID or one placeholder over the triggering row — and a host that keeps
/// the command ledger the write is made exactly once through.
/// </summary>
internal static partial class ActionCompiler
{
    private const string EntitySlot = "entity";
    private const string RecordIdSlot = "recordId";

    private static CompiledAction? CompileEntityUpdate(EntityUpdateAction update, string path, AfterHookScope scope)
    {
        if (scope.Host is not { Host.HasActionLedger: true } host)
        {
            scope.Errors.Add(scope.Host is null
                ? Error($"{path}/{TypeSlot}", NoHost, NoHostFix)
                : Error($"{path}/{TypeSlot}", NoLedger, NoLedgerFix));
            return null;
        }

        if (UpdateTarget(update.Entity, path, host, scope) is not { } target)
        {
            return null;
        }

        var recordId = CompileRecordId(update.RecordId, $"{path}/{RecordIdSlot}", scope, out var recordIdValid);
        var missing = update.RecordId is null ? MissingRequired(update, target, host, $"{path}/{ActionSlot.Payload}").ToList() : [];
        scope.Errors.AddRange(missing);
        var fields = update.Payload.ToDictionary(
            field => field.Key,
            field => CompilePayloadValue(field.Key, field.Value, new PayloadTarget(target, host, update.RecordId is null), $"{path}/{ActionSlot.Payload}", scope),
            StringComparer.Ordinal);

        return !recordIdValid || missing.Count > 0 || fields.Values.Any(value => value is null)
            ? null
            : new CompiledAction(update, _noTemplates, Endpoint: null, ActionType.EntityUpdate)
            {
                Target = target.Name,
                RecordId = recordId,
                Fields = fields.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal),
            };
    }

    /// <summary>The entity an update writes, and whether its row can carry the triggering event's tenant.</summary>
    /// <param name="Entity">The target's schema.</param>
    /// <param name="Host">The descriptor and model the target was resolved from.</param>
    /// <param name="IsCreate">Whether the action names no record, so it creates one.</param>
    private sealed record PayloadTarget(EntitySchema Entity, ActionHost Host, bool IsCreate);

    private static EntitySchema? UpdateTarget(string name, string path, ActionHost host, AfterHookScope scope)
    {
        var target = host.Model.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));
        if (target is null || target.Storage != EntityStorage.Physical)
        {
            scope.Errors.Add(target is null
                ? UndeclaredEntity(name, $"{path}/{EntitySlot}", host)
                : Error($"{path}/{EntitySlot}", $"'{name}' is a dynamic entity, which this build does not store, so this 'entity.update' could never write.", "Write into an entity with physical storage."));
            return null;
        }

        if (TenancyRefusal(target, scope) is not { } refusal)
        {
            return target;
        }

        scope.Errors.Add(Error($"{path}/{EntitySlot}", refusal, TenancyFix));
        return null;
    }

    private static string? TenancyRefusal(EntitySchema target, AfterHookScope scope) =>
        (IsScoped(target), IsScoped(scope.Schema)) switch
        {
            (true, false) =>
                $"'{target.Name}' is tenant-scoped and '{scope.Schema.Name}' is not, so the event that triggers this action "
                + "carries no tenant to write the row in.",
            (false, true) =>
                $"'{scope.Schema.Name}' is tenant-scoped and '{target.Name}' is not, so a tenant's event would write a row every "
                + "tenant shares — the action runs as the system and bypasses the target's rules.",
            _ => null,
        };

    private static bool IsScoped(EntitySchema entity) => entity.Tenancy == TenancyMode.Scoped;

    private const string TenancyFix =
        "Write into an entity with the same tenancy as the one whose event triggers the action, or perform the write from a "
        + "host function that decides the tenant itself.";

    private static DescriptorValidationError UndeclaredEntity(string name, string path, ActionHost host)
    {
        var declared = host.Model.Entities.Where(entity => entity.Storage == EntityStorage.Physical)
            .Select(entity => entity.Name).Order(StringComparer.Ordinal).ToList();
        var known = $"Declared entities: {string.Join(", ", declared)}.";

        return Error(
            path,
            $"'{name}' is not an entity of this descriptor, so this 'entity.update' could never write.",
            NameSuggestion.Closest(name, declared) is { } closest ? $"Did you mean '{closest}'? {known}" : known);
    }

    private static AlvoTemplate? CompileRecordId(string? source, string path, AfterHookScope scope, out bool valid)
    {
        valid = true;
        if (source is null)
        {
            return null;
        }

        var template = AlvoTemplate.TryParse(source, out var parsed, out _) ? parsed! : null;
        var refusals = template is null ? [NotARecordId(source, path)] : RecordIdRefusals(template, source, path, scope);
        scope.Errors.AddRange(refusals);
        valid = refusals.Count == 0;

        return valid ? template : null;
    }

    private static IReadOnlyList<DescriptorValidationError> RecordIdRefusals(
        AlvoTemplate template, string source, string path, AfterHookScope scope)
    {
        if (template.Placeholders.Count == 0)
        {
            return Guid.TryParse(source, out _) ? [] : [NotARecordId(source, path)];
        }

        if (template.Segments is not [{ IsPlaceholder: true } only] || TemplatePlaceholder.RowImageRead(only.Text) is null)
        {
            return [NotARecordId(source, path)];
        }

        return TemplatePlaceholder.TryResolve(only.Text, scope.Schema, out var refusal)
            ? [.. AfterImagePhase.Refusals(template, AfterImages.At(scope.Point), scope.Point, path), .. RecordIdFieldRefusals(only.Text, path, scope)]
            : [Error(path, refusal!, RecordIdFix)];
    }

    /// <summary>A placeholder over a field that is not a UUID, or one the entity leaves out of its event images, never names a record.</summary>
    private static IEnumerable<DescriptorValidationError> RecordIdFieldRefusals(string placeholder, string path, AfterHookScope scope)
    {
        var field = TemplatePlaceholder.RowImageRead(placeholder)!.Value.Field;
        if (scope.Schema.EventImageExclusions.Contains(field, StringComparer.Ordinal))
        {
            yield return Error(path, ExcludedPlaceholder(field), ExcludedPlaceholderFix(scope, field));
        }
        else if (!IsUuidField(scope.Schema, field))
        {
            yield return Error(path, $"'{field}' is not a UUID field, so it cannot name the record to update.", RecordIdFix);
        }
    }

    private static bool IsUuidField(EntitySchema entity, string field) =>
        string.Equals(field, "id", StringComparison.Ordinal)
        || entity.Fields.Any(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal) && CelFieldType.Of(candidate) == CelValueType.Uuid);

    private static DescriptorValidationError NotARecordId(string source, string path) =>
        Error(path, $"'{source}' is neither a record id nor one placeholder over the triggering row.", RecordIdFix);

    private const string RecordIdFix =
        "Write a UUID, or exactly one placeholder over the triggering row such as \"{{new.invoice_id}}\"; omit 'recordId' to "
        + "create a record.";

    private const string NoLedger = "This composition keeps no command ledger, so 'entity.update' cannot be written exactly once.";

    private const string NoLedgerFix = "Use a relational provider (UseSqlite, UsePostgreSql).";

    private const string NoHost = "This descriptor is compiled without a host, so 'entity.update' has nothing to write through.";

    private const string NoHostFix = "Apply the descriptor through a host composed with AddAlvo and a relational provider.";
}
