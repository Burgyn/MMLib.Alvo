namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>The source of a project's role constants: <c>{Project}Roles</c>, the built-in roles first, then the declared ones.</summary>
internal static class RolesEmitter
{
    internal static string ClassName(DescriptorModel model) => CSharpNames.Roles(model.Project);

    internal static string Emit(DescriptorModel model)
    {
        var className = ClassName(model);
        var writer = new SourceWriter();
        GeneratedText.Header(writer, model);
        writer.Line()
            .Line($"/// <summary>The roles of project '{GeneratedText.Xml(model.Project)}': the built-in ones, then those its descriptor declares.</summary>")
            .Line(GeneratedText.GeneratedCode)
            .Line($"public static class {className}")
            .Open();
        var used = new HashSet<string>(StringComparer.Ordinal) { className };
        for (var i = 0; i < model.Roles.Count; i++)
        {
            Role(writer, model.Roles[i], CSharpNames.Unique(CSharpNames.RoleMember(model.Roles[i], i), used), first: i == 0);
        }

        return writer.Close().ToString();
    }

    private static void Role(SourceWriter writer, string role, string member, bool first)
    {
        if (!first)
        {
            writer.Line();
        }

        writer.Line($"/// <summary>The role '{GeneratedText.Xml(role)}'.</summary>")
            .Line($"public const string {member} = {GeneratedText.Literal(role)};");
    }
}
