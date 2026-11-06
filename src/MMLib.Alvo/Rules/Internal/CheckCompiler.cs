using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Compiles an entity's lookup checks: each one's lookup must be a ref of the entity to a declared physical entity,
/// and its condition, compiled in the Rule profile over that one relation and never lifted, becomes the body of a
/// single forward <see cref="CelRelation"/> — so a negation inside it stays inside the <c>EXISTS</c> and fails closed
/// when the looked-up row is invisible.
/// </summary>
/// <remarks>
/// A check reads no caller (<c>@user</c>, <c>@tenant</c>): it judges the written row and the row it points at, and who
/// may write is the rules' question. It must read its lookup, or it is a <c>validation</c> written in the wrong place.
/// </remarks>
internal static class CheckCompiler
{
    private const string NoCallerFix = "Move the condition into the entity's rules.";

    private const string ValidationFix = "Use field 'validation' for a condition over this row alone.";

    /// <summary>
    /// The compiled checks of one entity, each with its place in <c>checks</c>, in declaration order; every refusal is
    /// appended to the scope's errors.
    /// </summary>
    /// <param name="checks">The entity's declared checks, or <see langword="null"/>.</param>
    /// <param name="scope">The entity and the apply's compiler, catalog and error list.</param>
    internal static IReadOnlyList<(int Index, DescriptorCheck Check)> CompileAll(IReadOnlyList<EntityCheck>? checks, CheckScope scope)
    {
        var compiled = new List<(int Index, DescriptorCheck Check)>();
        for (var index = 0; index < (checks?.Count ?? 0); index++)
        {
            if (Compile(index, checks![index], scope) is { } check)
            {
                compiled.Add((index, check));
            }
        }

        return compiled;
    }

    /// <summary>One check, or <see langword="null"/> when it is refused.</summary>
    /// <param name="index">The check's place in the entity's <c>checks</c>.</param>
    /// <param name="check">The declared check.</param>
    /// <param name="scope">The entity and the apply's compiler, catalog and error list.</param>
    internal static DescriptorCheck? Compile(int index, EntityCheck check, CheckScope scope)
    {
        var path = $"/entities/{scope.Entity.Name}/checks/{index}";
        if (Target(check.Lookup, scope, $"{path}/lookup") is not { } target)
        {
            return null;
        }

        var body = Body(check, target, scope, $"{path}/expr");
        return body is null
            ? null
            : new DescriptorCheck(
                check.Lookup,
                new CelRelation(CelRelationKind.Forward, target, AlvoManagedColumns.Id, check.Lookup, check.Lookup, body),
                check.Message,
                Reads(check.Lookup, body));
    }

    /// <summary>The checks a <c>storage: dynamic</c> entity declares, each refused: it has no table to read (#41).</summary>
    /// <param name="descriptor">The descriptor being applied.</param>
    /// <param name="errors">The apply's error list.</param>
    internal static void RefuseDynamic(AlvoDescriptor descriptor, List<DescriptorValidationError> errors) =>
        errors.AddRange(
            from entity in descriptor.Entities
            where !DescriptorToSchemaMapper.IsPhysical(entity.Value)
            from index in Enumerable.Range(0, entity.Value.Checks?.Count ?? 0)
            select Error(
                $"/entities/{entity.Key}/checks/{index}",
                $"'{entity.Key}' declares 'storage: dynamic', so its lookup checks have no table to read until #41.",
                $"Keep '{entity.Key}' physical, or remove its checks."));

    private static EntitySchema? Target(string lookup, CheckScope scope, string path)
    {
        var field = scope.Entity.Fields.FirstOrDefault(candidate => candidate.Name == lookup);
        if (field?.Reference is not { } reference)
        {
            scope.Errors.Add(Error(
                path,
                $"'{lookup}' is not a reference on '{scope.Entity.Name}', so a lookup check has no row to read.",
                $"Name a 'ref' field of '{scope.Entity.Name}' ({List(RefsOf(scope.Entity))}) as the lookup."));
            return null;
        }

        if (scope.Relations.Entity(reference.TargetEntity) is { } target)
        {
            return target;
        }

        var refusal = scope.Relations.Refusal(scope.Entity.Name, lookup, AlvoManagedColumns.Id);
        scope.Errors.Add(Error(path, refusal.Message, "Look up a ref to a declared, physical entity."));
        return null;
    }

    private static CelNode? Body(EntityCheck check, EntitySchema target, CheckScope scope, string path)
    {
        var compiled = scope.Compiler.Compile(check.Expr, CelProfile.Rule, scope.Entity, CatalogFor(check.Lookup, target, scope));
        if (!compiled.IsSuccess)
        {
            scope.Errors.AddRange(compiled.Errors.Select(error => Error(path, error.Message, error.FixSuggestion)));
            return null;
        }

        var root = compiled.Expression!.Root;
        if (Refusal(check.Lookup, root) is { } refused)
        {
            scope.Errors.Add(Error(path, refused.Message, refused.Fix));
            return null;
        }

        return CelRewriter.Replace(root, static node => node switch
        {
            CelHop hop => hop.Field,
            CelFieldRef outer => new CelOuterFieldRef(outer.FieldName, outer.Type),
            _ => null,
        });
    }

    /// <summary>Why a compiled condition is no lookup check: it reads a caller, another ref, or not its lookup.</summary>
    private static (string Message, string Fix)? Refusal(string lookup, CelNode root)
    {
        if (CallerRead(root) is { } caller)
        {
            return ($"A lookup check reads no caller: '{caller}' belongs in rules.", NoCallerFix);
        }

        if (OtherHop(lookup, root) is { } other)
        {
            return OtherRef(lookup, $"{other.Via}.{other.Field.FieldName}");
        }

        return CelRewriter.Any(root, node => node is CelHop)
            ? null
            : ($"A lookup check must read '{lookup}': write the condition over '{lookup}.<field>'.", ValidationFix);
    }

    private static string? CallerRead(CelNode root)
    {
        string? caller = null;
        CelRewriter.Any(root, node =>
        {
            caller ??= node is CelContextRef context ? (context.Value == CelContextValue.TenantId ? "@tenant" : "@user") : null;
            return caller is not null;
        });
        return caller;
    }

    private static CelHop? OtherHop(string lookup, CelNode root)
    {
        CelHop? other = null;
        CelRewriter.Any(root, node =>
        {
            other ??= node is CelHop hop && hop.Via != lookup ? hop : null;
            return other is not null;
        });
        return other;
    }

    /// <summary>
    /// The one relation a check may follow: its lookup's target, with no child to reach. A path over another ref, and an
    /// <c>exists</c>, are refused here, in the type checker, at their own position.
    /// </summary>
    private static CelRelationCatalog CatalogFor(string lookup, EntitySchema target, CheckScope scope) => new(
        new Dictionary<string, EntitySchema>(StringComparer.Ordinal) { [target.Name] = target },
        new Dictionary<(string Entity, string Name), (EntitySchema Child, string Via)>(),
        (entity, member, field) => field is null
            ? new CelCompilationError(
                $"A lookup check reads the one row '{lookup}' points at, so '{member}.exists(…)' has no rows to read.",
                "Count those rows with a rollup on this entity, or move the condition into a rule.",
                0)
            : IsRef(scope.Entity, member)
                ? AsCompilationError(OtherRef(lookup, $"{member}.{field}"))
                : scope.Relations.Refusal(entity, member, field))
    {
        LiftsForwardPaths = false,
    };

    private static CelCompilationError AsCompilationError((string Message, string Fix) refusal) => new(refusal.Message, refusal.Fix, 0);

    private static (string Message, string Fix) OtherRef(string lookup, string written) => (
        $"A lookup check follows '{lookup}' only, so '{written}' cannot be read here.",
        $"Declare another check whose lookup is '{written.Split('.')[0]}'.");

    /// <summary>The lookup and every field of this row the body reads: an update that changes none of them skips the check.</summary>
    private static HashSet<string> Reads(string lookup, CelNode body)
    {
        var reads = new HashSet<string>(StringComparer.Ordinal) { lookup };
        CelRewriter.Any(body, node =>
        {
            if (node is CelOuterFieldRef outer)
            {
                reads.Add(outer.FieldName);
            }

            return false;
        });
        return reads;
    }

    private static bool IsRef(EntitySchema entity, string member) => RefsOf(entity).Contains(member, StringComparer.Ordinal);

    private static IEnumerable<string> RefsOf(EntitySchema entity) =>
        entity.Fields.Where(field => field.Reference is not null).Select(field => field.Name);

    private static string List(IEnumerable<string> names) =>
        names.Order(StringComparer.Ordinal).ToList() is { Count: > 0 } ordered ? string.Join(", ", ordered) : "none";

    private static DescriptorValidationError Error(string path, string message, string? fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}

/// <summary>What one entity's checks compile against.</summary>
/// <param name="Entity">The entity the checks belong to.</param>
/// <param name="Compiler">The CEL compiler.</param>
/// <param name="Relations">The apply's relation catalog: the readable targets, and the refusal texts for the rest.</param>
/// <param name="Errors">The apply's error list.</param>
internal sealed record CheckScope(
    EntitySchema Entity, ICelCompiler Compiler, CelRelationCatalog Relations, List<DescriptorValidationError> Errors);
