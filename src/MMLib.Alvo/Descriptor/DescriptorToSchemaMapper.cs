using MMLib.Alvo.Api.Internal;
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

        var tenancy = ResolveTenancy(e.Tenancy, tenancyEnabled);
        bool audit = e.Audit == true;
        bool softDelete = EnsureSoftDeleteIsImplementable(name, e);
        AddManagedColumns(fields, e, tenancy, audit, softDelete);

        var indexes = (e.Indexes ?? [])
            .Select(i => new IndexSchema(i.Fields, i.Unique == true)).ToList();

        return new EntitySchema
        {
            Name = name,
            RenamedFrom = e.RenamedFrom,
            Storage = EntityStorage.Physical,
            Tenancy = tenancy,
            SoftDelete = softDelete,
            Audit = audit,
            Fields = fields,
            Indexes = indexes,
        };
    }

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
    /// Refuses <c>softDelete</c>, which is <b>declared in the frozen descriptor schema and not implemented
    /// in F3</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema promises "DELETE becomes a soft delete, and reads/list/get/rollup auto-exclude
    /// soft-deleted rows. A restore operation is provided." None of that exists: the data path
    /// hard-deletes the row and lists a row whose <c>deleted_at</c> is set. Measured on real PostgreSQL,
    /// where <c>DeleteAsync</c> removed a row from a <c>softDelete: true</c> entity outright — irrecoverable
    /// data loss where the contract promises recoverability.
    /// </para>
    /// <para>
    /// So it is refused at <b>apply</b> time, loudly, exactly as <c>computed</c> is: failing closed on a
    /// descriptor beats silently destroying rows, and Alvo's own rule is that a bad descriptor fails at save
    /// rather than per request. Implementing it is deliberately <em>not</em> in scope here — soft delete
    /// changes what every read means, and that interacts with the policy predicate.
    /// </para>
    /// </remarks>
    private static bool EnsureSoftDeleteIsImplementable(string name, EntityDescriptor e) => e.SoftDelete == true
        ? throw new InvalidDataException(
            $"Entity '{name}' declares 'softDelete', which is not supported yet: the delete path would " +
            "hard-delete the row and reads would not exclude it, losing data the descriptor promises is " +
            "recoverable. Remove 'softDelete' or track the soft-delete implementation issue.")
        : false;

    /// <summary>
    /// Appends one framework-managed column <b>unless the descriptor already declares that name</b>.
    /// </summary>
    /// <remarks>
    /// Appending unconditionally is what made a descriptor naming a managed column produce two
    /// <see cref="FieldSchema"/> entries with one name, so every later operation on that entity died with
    /// <c>An item with the same key has already been added</c> from the first code that keyed on the field
    /// list — which made declaring <c>readOnly</c> on a managed column, the documented way to protect one,
    /// break the entity instead of protecting it. <c>id</c> was the only column guarded this way; the guard
    /// is now the shape every managed column goes through.
    /// <para>
    /// This de-duplicates by name only. A descriptor that declares a managed name with a <em>different
    /// type</em> still wins the mapping, which is a reserved-name question the descriptor validator owns
    /// (see <c>docs/architecture/data-path.md</c>), not something a mapper may silently override.
    /// </para>
    /// </remarks>
    private static void AddManagedColumn(
        List<FieldSchema> fields, EntityDescriptor e, string name, Func<string, FieldSchema> column)
    {
        if (!e.Fields.ContainsKey(name))
        {
            fields.Add(column(name));
        }
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

    private static TenancyMode? ResolveTenancy(EntityTenancy? entityTenancy, bool tenancyEnabled) => entityTenancy switch
    {
        EntityTenancy.Global => TenancyMode.Global,
        EntityTenancy.Scoped => TenancyMode.Scoped,
        _ => tenancyEnabled ? TenancyMode.Scoped : null,
    };

    private static FieldSchema MapField(
        string name, FieldDescriptor f, IReadOnlyDictionary<string, string> formats)
    {
        EnsureEveryDeclaredFeatureIsHonoured(name, f);

        return new()
        {
            Name = name,
            Type = MapType(f.Type),
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
    /// Refuses, at apply, every field-level feature the frozen schema declares and this build does
    /// <b>not</b> implement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Silently discarding a schema-declared feature is the defect class this repo has now closed four
    /// times</b>, and the shape of the closure is settled: <c>softDelete</c> and <c>computed</c> are refused
    /// here and reported by <c>DescriptorValidator</c>, because a descriptor that asks for behaviour it does
    /// not get is a lie the author cannot see. Each of the three added here sat in the identical position —
    /// parsed onto <see cref="FieldDescriptor"/>, dropped by this mapper, nothing raised:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///   <b><c>validation</c></b> is the worst of the three, because it fails <em>open on caller input</em>:
    ///   <c>validation: "value >= 0"</c> with <c>-5</c> in the body was answered 201. A field the author
    ///   believes is constrained is not.
    ///   </item>
    ///   <item>
    ///   <b><c>default</c></b> is honoured nowhere — no DDL default is emitted, and the value is dropped
    ///   before any writer sees it — so a <c>required</c> field with a <c>default</c> is not merely
    ///   un-defaulted, it is an INSERT of NULL into a NOT NULL column. Refusing the descriptor is the only
    ///   answer that is neither a silent wrong value nor a store-level 500 per request.
    ///   </item>
    ///   <item>
    ///   <b><c>rollup</c></b> promises a framework-maintained aggregate. Unmaintained, it reads as a
    ///   permanently null column that looks like data.
    ///   </item>
    /// </list>
    /// <para>
    /// Each refusal names what to do instead, because "not supported" without an alternative sends an agent
    /// looking for a flag to flip.
    /// </para>
    /// </remarks>
    /// <param name="name">The field being mapped.</param>
    /// <param name="f">Its descriptor.</param>
    /// <exception cref="InvalidDataException">The field declares a feature this build does not honour.</exception>
    private static void EnsureEveryDeclaredFeatureIsHonoured(string name, FieldDescriptor f)
    {
        if (f.Computed is not null)
        {
            throw Unhonoured(
                name,
                "computed",
                "computed fields require the CEL→SQL compiler arriving in #21",
                "Remove 'computed' or track #21.");
        }

        if (f.Rollup is not null)
        {
            throw Unhonoured(
                name,
                "rollup",
                "nothing maintains the aggregate, so the column would read as permanently null",
                "Remove 'rollup' and compute the aggregate in a query for now; rollups are deferred past F3.");
        }

        if (f.Validation is not null)
        {
            throw Unhonoured(
                name,
                "validation",
                "the expression is never evaluated, so a value it forbids is accepted",
                "Remove 'validation'. Until it is implemented, enforce the rule in a before-hook, or express "
                + "it with a facet the API does validate — 'maxLength', 'precision'/'scale', enum 'values' "
                + "or a 'format'.");
        }

        if (f.Default is not null)
        {
            throw Unhonoured(
                name,
                "default",
                "the value is not written by anything and no column default is emitted, so the field is "
                + "simply null — and on a 'required' field that is an INSERT of NULL into a NOT NULL column",
                "Remove 'default' and send the value explicitly on create.");
        }
    }

    /// <summary>
    /// The one wording for "the descriptor asks for something this build does not do", so four features
    /// cannot be refused four different ways.
    /// </summary>
    /// <param name="field">The field declaring it.</param>
    /// <param name="feature">The descriptor key.</param>
    /// <param name="consequence">What silently happens instead, stated concretely.</param>
    /// <param name="fix">What to do instead.</param>
    private static InvalidDataException Unhonoured(
        string field, string feature, string consequence, string fix) => new(
        $"Field '{field}' declares '{feature}', which is not supported yet: {consequence}. {fix}");

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
            + $"({string.Join(", ", BuiltInFormats.Order(StringComparer.Ordinal))}) nor a format the "
            + "descriptor's top-level 'formats' block declares"
            + (declared.Count == 0
                ? ". Declare it there, or use a built-in."
                : $". Declared formats: {string.Join(", ", declared)}."));
    }

    /// <summary>
    /// The built-in format names, <b>read from the one authority</b> rather than restated here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The names and the patterns they mean are one fact, and <c>Api.Internal.FormatCatalog</c> owns it. A
    /// second copy here was exactly the drift it looks like: deleting a pattern from the catalogue left this
    /// list still accepting the name, so the format resolved to nothing and validated nothing — a clean
    /// build, a green suite, and caller input silently unchecked. Reaching across a feature boundary for a
    /// shared list is the move <c>DescriptorValidator</c> already makes for <c>ReservedQueryKeys</c>, and for
    /// the same stated reason: one list is the point.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> extended with extra names. The F2 design records "extending the built-in
    /// <c>format</c> enum with more values" as additive-but-not-in-v1, so a build that accepted <c>url</c> or
    /// <c>uuid</c> as built-ins would silently accept a descriptor the published schema's enum branch does not
    /// list, and a descriptor written against it would stop validating on any other build.
    /// </para>
    /// </remarks>
    internal static IEnumerable<string> BuiltInFormats => FormatCatalog.BuiltIns.Keys;

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
