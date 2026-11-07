namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>
/// The source of one entity: a <c>sealed partial class</c> wrapping its record (<c>IAlvoEntity</c>), an enum per enum field,
/// and a typed patch of the fields a caller may write — every type name <c>global::</c>-qualified.
/// </summary>
internal static class EntityEmitter
{
    private const string Get = GeneratedText.Data + "AlvoRecordExtensions.Get";

    internal static string Emit(DescriptorModel model, EntityModel entity)
    {
        var writer = new SourceWriter();
        GeneratedText.Header(writer, model, entity.ClassName);
        WrapperEmitter.Emit(writer, model, entity);
        foreach (var field in entity.Fields)
        {
            if (field.EnumTypeName is not null)
            {
                Enum(writer, entity, field);
            }
        }

        PatchEmitter.Emit(writer, model, entity);

        return writer.ToString();
    }

    /// <summary>The C# type a property of <paramref name="field"/> has: the field's CLR type, nullable unless the field never holds null.</summary>
    internal static string PropertyType(DescriptorModel model, FieldModel field) =>
        field.Nullable ? Nullable(model, field) : ClrType(model, field);

    /// <summary>The field's CLR type made nullable: what a parameter that may leave the field out takes.</summary>
    internal static string Nullable(DescriptorModel model, FieldModel field) => ClrType(model, field) + "?";

    /// <summary>The field's CLR type, as the applied schema's own table maps its kind; an enum field's generated enum.</summary>
    internal static string ClrType(DescriptorModel model, FieldModel field) => field.Kind switch
    {
        FieldKind.String => "string",
        FieldKind.Integer => "long",
        FieldKind.Decimal => "decimal",
        FieldKind.Boolean => "bool",
        FieldKind.Date => "global::System.DateOnly",
        FieldKind.DateTime => "global::System.DateTimeOffset",
        FieldKind.Uuid => "global::System.Guid",
        _ => Qualified(model, field.EnumTypeName!),
    };

    internal static string Qualified(DescriptorModel model, string typeName) => $"global::{model.Namespace}.{typeName}";

    /// <summary>The expression a property of <paramref name="field"/> reads with; a virtual field reads null when the record does not carry it.</summary>
    internal static string Read(DescriptorModel model, FieldModel field)
    {
        var read = $"{Get}<{PropertyType(model, field)}>(Record, {GeneratedText.Literal(field.Name)})";

        return field.Origin == FieldOrigin.Virtual
            ? $"Record.Values.ContainsKey({GeneratedText.Literal(field.Name)}) ? {read} : null"
            : read;
    }

    private static void Enum(SourceWriter writer, EntityModel entity, FieldModel field)
    {
        writer.Line()
            .Line($"/// <summary>Values of field '{GeneratedText.Xml(field.Name)}' of entity '{GeneratedText.Xml(entity.Name)}'.</summary>")
            .Line(GeneratedText.GeneratedCode)
            .Line($"public enum {field.EnumTypeName}")
            .Open();
        foreach (var value in field.EnumValues)
        {
            writer.Line($"/// <summary>The value '{GeneratedText.Xml(value.Value)}'.</summary>")
                .Line($"[{GeneratedText.Data}AlvoEnumValue({GeneratedText.Literal(value.Value)})] {value.MemberName},");
        }

        writer.Close();
    }
}
