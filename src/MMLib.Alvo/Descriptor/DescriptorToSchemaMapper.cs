using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Schema;
using System.Text.RegularExpressions;
using SchemaFieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// Maps a parsed <see cref="AlvoDescriptor"/> to the public <see cref="SchemaModel"/>,
/// injecting the framework-managed columns <see cref="AlvoManagedColumns"/> names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which columns are managed is not decided here.</b> <see cref="AlvoManagedColumns"/> is the
/// authority, and this mapper injects exactly what it reports for an entity's traits — because the
/// write guard that <em>refuses</em> a caller-supplied managed column is <see langword="internal"/> to a
/// driver package and cannot see this file. When the two each carried their own list, this one grew to
/// six columns and the guard's stayed at two.
/// </para>
/// <para>
/// <c>src/MMLib.Alvo.Testing/Data/AlvoDataAdversarialTests.cs</c>'s private <c>BuildFixture</c>
/// hand-mirrors this mapper's managed-column injection (it cannot reference this internal
/// type — that project depends only on <c>MMLib.Alvo.Abstractions</c>). Its mirror only covers
/// field type, <c>Required</c>/<c>Nullable</c>, and the managed columns; it does not replicate
/// <c>Indexed</c>, <c>MaxLength</c>, enum values, or a <c>Ref</c> target. A future change here
/// (a different id/tenant_id shape) does not propagate there automatically — check that fixture when
/// changing this method.
/// </para>
/// </remarks>
internal static class DescriptorToSchemaMapper
{
    public static SchemaModel Map(AlvoDescriptor d)
    {
        bool tenancyEnabled = d.Tenancy?.Enabled == true;
        var formats = DeclaredFormats(d);
        var entities = d.Entities
            .Where(kvp => IsPhysical(kvp.Value))
            .Select(kvp => MapEntity(kvp.Key, kvp.Value, tenancyEnabled, formats))
            .ToList();
        return new SchemaModel(entities);
    }

    /// <summary>
    /// The descriptor's declared <c>formats</c>, each one's pattern checked for being a regular expression
    /// at all — at <b>apply</b>, once, never per request.
    /// </summary>
    /// <remarks>
    /// The descriptor's JSON Schema types <c>pattern</c> as a plain string, so it cannot tell a regular
    /// expression from arbitrary text; the F2 design's own answer for that class of problem is the one used
    /// here and for <c>ref</c>: "caught fail-fast at apply, not by the schema". A pattern that only fails
    /// when the first caller supplies a value would turn a one-off descriptor mistake into a per-request
    /// 500, and it would fail <em>open</em> for every request that never reached the field.
    /// </remarks>
    private static Dictionary<string, string> DeclaredFormats(AlvoDescriptor d)
    {
        var formats = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, format) in d.Formats ?? new Dictionary<string, NamedFormat>(StringComparer.Ordinal))
        {
            EnsurePatternIsARegularExpression(name, format.Pattern);
            formats[name] = format.Pattern;
        }

        return formats;
    }

    private static void EnsurePatternIsARegularExpression(string name, string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, PatternSyntaxCheckTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Format '{name}' declares a pattern that is not a valid regular expression: "
                + $"{exception.Message} Fix the pattern in the descriptor's 'formats' block.",
                exception);
        }
    }

    /// <summary>
    /// The match timeout on the throwaway <see cref="Regex"/> built only to <em>parse</em> a pattern.
    /// </summary>
    /// <remarks>
    /// The instance is never matched against anything, so the value cannot affect behaviour — it is here
    /// because a <see cref="Regex"/> constructed without an explicit timeout is a finding in its own right,
    /// and this is one of the two places in the framework where an author-supplied pattern is compiled at
    /// all (the other, <c>Api.Internal.FormatCatalog</c>, is where the timeout does real work).
    /// </remarks>
    private static TimeSpan PatternSyntaxCheckTimeout => TimeSpan.FromMilliseconds(100);

    private static bool IsPhysical(EntityDescriptor e) => (e.Storage ?? StorageMode.Physical) == StorageMode.Physical;

    private static EntitySchema MapEntity(
        string name, EntityDescriptor e, bool tenancyEnabled, IReadOnlyDictionary<string, string> formats)
    {
        var fields = new List<FieldSchema>();
        AddManagedColumn(fields, e, AlvoManagedColumns.Id, IdColumn);

        foreach (var (fname, f) in e.Fields)
        {
            fields.Add(MapField(fname, f, formats));
        }

        EnsureEveryDeclaredFeatureIsHonoured(name, e, UnhonouredFeatures.OnAnEntity);

        var tenancy = ResolveTenancy(e.Tenancy, tenancyEnabled);
        bool audit = e.Audit == true;

        // softDelete is hardcoded false, and it is NOT a shortcut to tidy away: 'softDelete' is an entry in
        // UnhonouredFeatures.OnAnEntity, so EnsureEveryDeclaredFeatureIsHonoured above has already thrown for
        // any entity that declares it. Reading e.SoftDelete here would be a second answer to a question the
        // refusal has already closed — and one that could disagree with it.
        //
        // WHEN SOFT DELETE LANDS, both of these change together: delete the table entry, then thread
        // `e.SoftDelete == true` through here and into EntitySchema.SoftDelete below. Until then 'deleted_at'
        // is DOUBLY dead code — see AddManagedColumns' own remark, which is where the consequence bites.
        AddManagedColumns(fields, e, tenancy, audit, softDelete: false);

        var indexes = (e.Indexes ?? [])
            .Select(i => new IndexSchema(i.Fields, i.Unique == true)).ToList();

        return new EntitySchema
        {
            Name = name,

            // Carried onto the applied schema because the OpenAPI transformer publishes it and cannot see the
            // descriptor — see EntitySchema.Description. Dropping it here is what made the generated document
            // describe every entity as nothing at all.
            Description = e.Description,
            RenamedFrom = e.RenamedFrom,
            Storage = EntityStorage.Physical,
            Tenancy = tenancy,

            // Same hardcode, same reason, same day it changes — see the comment above AddManagedColumns.
            SoftDelete = false,
            Audit = audit,
            Fields = fields,
            Indexes = indexes,
        };
    }

    /// <summary>Injects the managed columns an entity's traits earn.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>deleted_at</c>'s branch is unreachable today, and doubly so.</b> <c>softDelete</c> is an entry in
    /// <see cref="UnhonouredFeatures.OnAnEntity"/>, so <c>MapEntity</c> refuses any entity declaring it before
    /// reaching here — <em>and</em> <c>MapEntity</c> passes <c>softDelete: false</c> unconditionally, so even
    /// with the table entry gone this branch would still never run. Two independent reasons, either of which is
    /// enough.
    /// </para>
    /// <para>
    /// <b>Why that is worth a comment rather than a deletion.</b> The branch is the shape soft delete inherits,
    /// exactly as PR2 kept the descriptor flag's shape while refusing the behaviour. But it means
    /// <c>AddManagedColumn(…, DeletedAt, …)</c> — including the refusal it now performs when an entity declares
    /// <c>deleted_at</c> itself — is covered by <b>no fact</b>, because no fact can drive it: every route to it
    /// is closed. So the day soft delete lands, this refusal path goes live and the suite will not notice.
    /// </para>
    /// <para>
    /// <b>What the implementer owes, then.</b> Removing the <c>softDelete</c> table entry and threading
    /// <c>e.SoftDelete</c> is not the whole change — it also needs a fact that an entity declaring its own
    /// <c>deleted_at</c> is refused, which is the fact <c>ManagedColumnNames</c>' per-name reason exists for and
    /// which today has nothing to assert against. <c>AlvoManagedColumnsTests</c> asserts the <em>authority</em>
    /// reports <c>deleted_at</c> for the trait; nothing asserts the mapper acts on it.
    /// </para>
    /// </remarks>
    /// <param name="fields">The mapped field list being built.</param>
    /// <param name="e">The entity descriptor.</param>
    /// <param name="tenancy">The resolved tenancy mode.</param>
    /// <param name="audit">Whether the entity declares <c>audit</c>.</param>
    /// <param name="softDelete">
    /// Whether the entity declares <c>softDelete</c> — always <see langword="false"/> today; see the remarks.
    /// </param>
    private static void AddManagedColumns(
        List<FieldSchema> fields, EntityDescriptor e, TenancyMode? tenancy, bool audit, bool softDelete)
    {
        if (tenancy == TenancyMode.Scoped)
        {
            AddManagedColumn(fields, e, AlvoManagedColumns.TenantId, TenantIdColumn);
        }

        if (audit)
        {
            AddManagedColumn(fields, e, AlvoManagedColumns.CreatedAt, RequiredInstantColumn);
            AddManagedColumn(fields, e, AlvoManagedColumns.CreatedBy, ActorColumn);
            AddManagedColumn(fields, e, AlvoManagedColumns.UpdatedAt, RequiredInstantColumn);
            AddManagedColumn(fields, e, AlvoManagedColumns.UpdatedBy, ActorColumn);
        }

        if (softDelete)
        {
            AddManagedColumn(fields, e, AlvoManagedColumns.DeletedAt, OptionalInstantColumn);
        }
    }

    /// <summary>
    /// Appends one framework-managed column, <b>refusing the entity outright if it declares that name</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This branch used to let the declaration win — inject only when the entity did not declare the name —
    /// and both of the defects <see cref="ManagedColumnNames"/> records came out of it: an audited entity
    /// declaring <c>updated_at</c> as <c>{"type":"string"}</c> applied cleanly and then failed every create
    /// with an internal parameter name in the response body, and one declaring it <c>hidden</c> applied
    /// cleanly and switched optimistic concurrency off in silence. Both are the same mistake — a
    /// caller-authored column standing in for one the framework writes — so both are refused here, at the one
    /// place the two paths meet.
    /// </para>
    /// <para>
    /// Appending unconditionally was the version before that, and it is worth keeping the reason recorded
    /// because refusing must not regress to it: two <see cref="FieldSchema"/> entries with one name made every
    /// later operation on the entity die with <c>An item with the same key has already been added</c> from the
    /// first code that keyed on the field list. Refusing produces neither a duplicate nor a silent override.
    /// </para>
    /// <para>
    /// <b>The validator reports this too, and that is the pair rather than a duplicate.</b>
    /// <c>DescriptorValidator</c>'s semantic pass names every offending field at once with a JSON path and a
    /// fix suggestion, which is the only form a dashboard or a CLI <c>validate</c> can show; this throw is the
    /// fail-closed belt for an apply that did not run the validator first, and it is why the two read one
    /// table.
    /// </para>
    /// </remarks>
    /// <param name="fields">The mapped field list being built.</param>
    /// <param name="e">The entity descriptor, consulted for whether it declares <paramref name="name"/>.</param>
    /// <param name="name">The managed column to inject.</param>
    /// <param name="column">Builds the managed column's schema.</param>
    /// <exception cref="InvalidDataException">The entity declares a field named <paramref name="name"/>.</exception>
    private static void AddManagedColumn(
        List<FieldSchema> fields, EntityDescriptor e, string name, Func<string, FieldSchema> column)
    {
        if (e.Fields.ContainsKey(name))
        {
            var (consequence, fix) = ManagedColumnNames.Refusing(name);
            throw new InvalidDataException(
                $"Field '{name}' is a framework-managed column and cannot be declared. {consequence} {fix}");
        }

        fields.Add(column(name));
    }

    private static FieldSchema IdColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.Uuid, Required = true };

    private static FieldSchema TenantIdColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.Uuid, Required = true, Indexed = true };

    private static FieldSchema RequiredInstantColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.DateTime, Required = true };

    private static FieldSchema OptionalInstantColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.DateTime, Nullable = true };

    private static FieldSchema ActorColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.Uuid, Nullable = true };

    private static TenancyMode? ResolveTenancy(EntityTenancy? entityTenancy, bool tenancyEnabled) =>
        ResolveTenancy(
            entityTenancy switch
            {
                EntityTenancy.Global => TenancyMode.Global,
                EntityTenancy.Scoped => TenancyMode.Scoped,
                _ => null,
            },
            tenancyEnabled);

    /// <summary>
    /// <b>The one rule for what an entity's tenancy resolves to</b>, given what it declared for itself and
    /// whether the project turns tenancy on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shared with <c>DescriptorValidator</c> deliberately.</b> That pass answers the same question from raw
    /// JSON — it must, because it runs before the descriptor is parseable — and for one commit it carried a
    /// line-for-line copy of this rule. The two answers are not independent: the validator uses it to decide
    /// whether an entity carries a framework-managed <c>tenant_id</c> and therefore whether declaring one is
    /// refused, while this mapper uses it to decide whether to <em>inject</em> one. Let them drift and the
    /// failure is a descriptor refused that would have been injected — or, worse, the reverse: accepted by the
    /// validator and then refused by the mapper, which is a structured error the dashboard never shows becoming
    /// an exception at apply.
    /// </para>
    /// <para>
    /// <b>Only the defaulting is shared; each pass parses its own representation.</b> Turning
    /// <see cref="EntityTenancy"/> or the JSON string <c>"scoped"</c> into a <see cref="TenancyMode"/> is
    /// irreducibly per-pass, so that stays where it is and the fact
    /// <c>DescriptorValidatorTests.The_validator_and_the_mapper_agree_on_which_entities_carry_a_tenant_id</c>
    /// ties the two parsings end to end. This method is what stops the <em>rule</em> needing that fact to catch
    /// it.
    /// </para>
    /// </remarks>
    /// <param name="declared">The entity's own declared tenancy, or <see langword="null"/> when it declares none.</param>
    /// <param name="projectTenancyEnabled">Whether the project's <c>tenancy.enabled</c> is on.</param>
    internal static TenancyMode? ResolveTenancy(TenancyMode? declared, bool projectTenancyEnabled) =>
        declared ?? (projectTenancyEnabled ? TenancyMode.Scoped : null);

    private static FieldSchema MapField(
        string name, FieldDescriptor f, IReadOnlyDictionary<string, string> formats)
    {
        EnsureEveryDeclaredFeatureIsHonoured(name, f, UnhonouredFeatures.OnAField);

        return new()
        {
            Name = name,
            Type = MapType(f.Type),

            // Same reason as the entity's, one level down: the published document's field descriptions come
            // from here, and nothing else can reach them.
            Description = f.Description,
            RenamedFrom = f.RenamedFrom,
            Required = f.Required == true,
            Unique = f.Unique == true,
            Nullable = f.Nullable ?? f.Required != true,
            MaxLength = f.MaxLength,
            Precision = f.Precision,
            Scale = f.Scale,
            EnumValues = f.Values,
            Reference = f.Entity is null ? null : new RefSchema(f.Entity, MapOnDelete(f.OnDelete)),
            Format = f.Format,
            FormatPattern = ResolveFormatPattern(name, f.Format, formats),
            Indexed = f.Index == true,
            // ComputedExpression intentionally not set — revived by #21 (CEL→SQL).
        };
    }

    /// <summary>
    /// Refuses, at apply, every feature <see cref="UnhonouredFeatures"/> records as declared by the frozen
    /// schema and not honoured by this build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Silently discarding a schema-declared feature is the defect class this repo has now closed five
    /// times</b>, and the shape of the closure is settled: refuse at apply, name the consequence, name the
    /// alternative. What is <em>not</em> settled by repetition is the list, which is why the list lives in one
    /// table both this pass and <c>DescriptorValidator</c> read — four hand-written copies of it let
    /// <c>validation</c> be dropped for a whole task, and a fifth copy would do it again.
    /// </para>
    /// <para>
    /// This walk is the guard an embedded host that never calls <c>IDescriptorValidator</c> still passes
    /// through, so it must exist beside the structured-error pass rather than behind it. It stops at the
    /// first match because an exception carries one message; reporting all of them is the validator's job.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The descriptor being checked — a field's or an entity's.</typeparam>
    /// <param name="name">The field or entity name, for the message.</param>
    /// <param name="descriptor">The parsed descriptor.</param>
    /// <param name="unhonoured">The table of features to refuse.</param>
    /// <exception cref="InvalidDataException">It declares a feature this build does not honour.</exception>
    private static void EnsureEveryDeclaredFeatureIsHonoured<T>(
        string name, T descriptor, IReadOnlyList<UnhonouredFeature<T>> unhonoured)
    {
        foreach (var feature in unhonoured.Where(feature => feature.IsDeclaredBy(descriptor)))
        {
            throw new InvalidDataException(
                $"'{name}' declares '{feature.Path}'. {feature.Consequence} {feature.Fix}");
        }
    }

    /// <summary>
    /// Resolves a field's <c>format</c> to the pattern that enforces it: <see langword="null"/> for a
    /// built-in (the framework owns those patterns) and the declared pattern for a named format. A name
    /// that is neither is refused here, at apply.
    /// </summary>
    /// <remarks>
    /// The frozen descriptor schema states the reason on <c>field.format</c> itself: "An unknown name is
    /// caught fail-fast at apply (like a ref to a missing entity), not by this schema." A typo'd format
    /// silently validates nothing, which is the fail-<em>open</em> direction — the same defect class as a
    /// mistyped <c>hidden</c> flag exposing the field it was meant to hide.
    /// </remarks>
    private static string? ResolveFormatPattern(
        string field, string? format, IReadOnlyDictionary<string, string> formats)
    {
        if (format is null || FormatCatalog.BuiltIns.ContainsKey(format))
        {
            return null;
        }

        if (formats.TryGetValue(format, out var pattern))
        {
            return pattern;
        }

        var declared = formats.Keys.Order(StringComparer.Ordinal).ToList();
        throw new InvalidDataException(
            $"Field '{field}' declares format '{format}', which is neither a built-in "
            + $"({string.Join(", ", FormatCatalog.BuiltIns.Keys.Order(StringComparer.Ordinal))}) nor a format the "
            + "descriptor's top-level 'formats' block declares"
            + (declared.Count == 0
                ? ". Declare it there, or use a built-in."
                : $". Declared formats: {string.Join(", ", declared)}."));
    }

    private static SchemaFieldType MapType(FieldType t) => t switch
    {
        FieldType.String => SchemaFieldType.String,
        FieldType.Text => SchemaFieldType.Text,
        FieldType.Integer => SchemaFieldType.Integer,
        FieldType.Decimal => SchemaFieldType.Decimal,
        FieldType.Boolean => SchemaFieldType.Boolean,
        FieldType.Date => SchemaFieldType.Date,
        FieldType.DateTime => SchemaFieldType.DateTime,
        FieldType.Uuid => SchemaFieldType.Uuid,
        FieldType.Json => SchemaFieldType.Json,
        FieldType.Enum => SchemaFieldType.Enum,
        FieldType.Ref => SchemaFieldType.Ref,
        _ => throw new InvalidDataException($"Unknown field type '{t}'."),
    };

    private static OnDelete MapOnDelete(OnDeleteAction? od) => od switch
    {
        OnDeleteAction.Cascade => OnDelete.Cascade,
        OnDeleteAction.SetNull => OnDelete.SetNull,
        _ => OnDelete.Restrict,
    };
}
