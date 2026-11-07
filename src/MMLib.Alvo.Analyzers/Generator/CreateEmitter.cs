namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>
/// An entity's <c>Create(...)</c>: one parameter per field a caller may write — required ones without a default first, the
/// rest optional and nullable — and, on a scoped entity, <c>tenantId</c> last.
/// </summary>
internal static class CreateEmitter
{
    private const string TenantId = "tenant_id";

    internal static void Emit(SourceWriter writer, DescriptorModel model, EntityModel entity)
    {
        var parameters = Parameters(model, entity);
        writer.Line()
            .Line($"/// <summary>A new '{GeneratedText.Xml(entity.Name)}' record for a create; a null argument is left out of the create.</summary>")
            .Line($"public static {EntityEmitter.Qualified(model, entity.ClassName)} Create({string.Join(", ", parameters.Select(p => p.Declaration))}) =>")
            .Line($"    new(new {GeneratedText.Data}AlvoRecord(new global::System.Collections.Generic.Dictionary<string, object?>(global::System.StringComparer.Ordinal)")
            .Line("    {");
        if (parameters.Count > 0)
        {
            writer.Line($"        {string.Join(" ", parameters.Select(p => $"[{GeneratedText.Literal(p.Field)}] = {p.Name},"))}");
        }

        writer.Line("    }));");
    }

    private static List<(string Field, string Name, string Declaration)> Parameters(DescriptorModel model, EntityModel entity)
    {
        var writable = entity.Fields.Where(field => field.CallerWritable).ToList();
        var parameters = writable.Where(IsMandatory)
            .Select(field => (field.Name, field.ParameterName, $"{EntityEmitter.PropertyType(model, field)} {field.ParameterName}"))
            .Concat(writable.Where(field => !IsMandatory(field))
                .Select(field => (field.Name, field.ParameterName, $"{EntityEmitter.Nullable(model, field)} {field.ParameterName} = null")))
            .ToList();
        if (entity.Fields.Any(field => field.Name == TenantId && field.Origin == FieldOrigin.Managed))
        {
            parameters.Add((TenantId, "tenantId", "global::System.Guid? tenantId = null"));
        }

        return parameters;
    }

    private static bool IsMandatory(FieldModel field) => field.Required && !field.HasDefault;
}
