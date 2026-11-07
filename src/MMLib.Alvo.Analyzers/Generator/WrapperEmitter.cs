namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>The wrapper class of one entity: constructor, <c>Record</c>, one property per typed field, <c>Create</c> and <c>Patch</c>.</summary>
internal static class WrapperEmitter
{
    private const string Record = GeneratedText.Data + "AlvoRecord";

    internal static void Emit(SourceWriter writer, DescriptorModel model, EntityModel entity)
    {
        var name = GeneratedText.Xml(entity.Name);
        writer.Line()
            .Line($"/// <summary>A record of entity '{name}'. It wraps the record, so fields this class does not declare survive a round trip.</summary>")
            .Line($"/// <remarks>{Remarks(entity)}</remarks>")
            .Line(GeneratedText.GeneratedCode)
            .Line($"[{GeneratedText.Data}AlvoEntity({GeneratedText.Literal(entity.Name)})]")
            .Line($"public sealed partial class {entity.ClassName} : {GeneratedText.Data}IAlvoEntity")
            .Open();
        Constructor(writer, entity, name);
        foreach (var field in entity.Fields)
        {
            Property(writer, model, field);
        }

        CreateEmitter.Emit(writer, model, entity);
        writer.Line()
            .Line("/// <summary>A patch of the fields a caller may write.</summary>")
            .Line($"public static {EntityEmitter.Qualified(model, CSharpNames.Patch(entity.ClassName))} Patch() => new();")
            .Close();
    }

    private static string Remarks(EntityModel entity)
    {
        var untyped = entity.Untyped.Count == 0 ? "none" : string.Join(", ", entity.Untyped.Select(field => $"'{GeneratedText.Xml(field)}'"));
        var reachable = entity.Untyped.Count == 0
            ? "Fields reachable only as <c>Record[\"field\"]</c>: none."
            : $"Fields reachable only as <c>Record[\"field\"]</c>, and written only through <c>Record.With</c>: {untyped}.";

        return entity.Description is { } description ? $"{GeneratedText.Xml(description)} {reachable}" : reachable;
    }

    private static void Constructor(SourceWriter writer, EntityModel entity, string name) => writer
        .Line("/// <summary>Wraps <paramref name=\"record\"/> without copying it.</summary>")
        .Line($"/// <param name=\"record\">A record of '{name}', from a read, an event or a hook.</param>")
        .Line($"public {entity.ClassName}({Record} record)")
        .Open()
        .Line("global::System.ArgumentNullException.ThrowIfNull(record);")
        .Line("Record = record;")
        .Close()
        .Line()
        .Line("/// <summary>Gets the record this instance wraps, every field included.</summary>")
        .Line($"public {Record} Record {{ get; }}");

    private static void Property(SourceWriter writer, DescriptorModel model, FieldModel field)
    {
        writer.Line().Line($"/// <summary>Field '{GeneratedText.Xml(field.Name)}'{Origin(field.Origin)}.</summary>");
        if (field.Description is { } description)
        {
            writer.Line($"/// <remarks>{GeneratedText.Xml(description)}</remarks>");
        }

        writer.Line($"[{GeneratedText.Data}AlvoField({GeneratedText.Literal(field.Name)})]")
            .Line($"public {EntityEmitter.PropertyType(model, field)} {field.PropertyName} => {EntityEmitter.Read(model, field)};");
    }

    private static string Origin(FieldOrigin origin) => origin switch
    {
        FieldOrigin.Computed => " (computed by the store)",
        FieldOrigin.Rollup => " (aggregated by the store)",
        FieldOrigin.Sequence => " (numbered by the store)",
        FieldOrigin.Virtual => " (filled by the host's enricher; null when the record does not carry it)",
        _ => string.Empty,
    };
}
