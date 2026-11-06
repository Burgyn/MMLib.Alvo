namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>An entity's typed patch: <c>IAlvoPatch</c> of its class, with one <c>Set{Property}</c> per field a caller may write.</summary>
internal static class PatchEmitter
{
    internal static void Emit(SourceWriter writer, DescriptorModel model, EntityModel entity)
    {
        var patch = CSharpNames.Patch(entity.ClassName);
        var contract = $"{GeneratedText.Data}IAlvoPatch<{EntityEmitter.Qualified(model, entity.ClassName)}>";
        writer.Line()
            .Line($"/// <summary>A typed patch of '{GeneratedText.Xml(entity.Name)}': one setter per field a caller may write.</summary>")
            .Line(GeneratedText.GeneratedCode)
            .Line($"public sealed partial class {patch} : {contract}")
            .Open()
            .Line($"private readonly {contract} _patch = {GeneratedText.Data}AlvoPatch.For<{EntityEmitter.Qualified(model, entity.ClassName)}>();")
            .Line()
            .Line("/// <summary>Gets the fields set so far, by field name.</summary>")
            .Line("public global::System.Collections.Generic.IReadOnlyDictionary<string, object?> Values => _patch.Values;");
        foreach (var field in entity.Fields.Where(field => field.CallerWritable))
        {
            Setter(writer, model, field, EntityEmitter.Qualified(model, patch));
        }

        ExplicitSet(writer, model, entity, contract);
        writer.Close();
    }

    private static void Setter(SourceWriter writer, DescriptorModel model, FieldModel field, string patch) => writer
        .Line()
        .Line($"/// <summary>Sets field '{GeneratedText.Xml(field.Name)}'.</summary>")
        .Line("/// <param name=\"value\">The new value.</param>")
        .Line("/// <returns>This patch.</returns>")
        .Line($"public {patch} Set{field.PropertyName}({EntityEmitter.PropertyType(model, field)} value)")
        .Open()
        .Line($"_patch.Set(x => x.{field.PropertyName}, value);")
        .Line("return this;")
        .Close();

    private static void ExplicitSet(SourceWriter writer, DescriptorModel model, EntityModel entity, string contract) => writer
        .Line()
        .Line("/// <inheritdoc/>")
        .Line($"{contract} {contract}.Set<TValue>(")
        .Line($"    global::System.Linq.Expressions.Expression<global::System.Func<{EntityEmitter.Qualified(model, entity.ClassName)}, TValue>> field, TValue value)")
        .Open()
        .Line("_patch.Set(field, value);")
        .Line("return this;")
        .Close();
}
