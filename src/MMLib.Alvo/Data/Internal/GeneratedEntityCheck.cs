using MMLib.Alvo.Schema;

using System.CodeDom.Compiler;
using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// Finds the classes the typed-entity generator emitted, and tells which no longer fit a schema — the same mapping check a
/// typed read applies (<see cref="ShapeMapping"/>), run at boot so a stale class is named before its first use.
/// </summary>
/// <remarks>
/// A generated class carries <c>[GeneratedCode("MMLib.Alvo.Analyzers", …)]</c>, <see cref="AlvoEntityAttribute"/> and
/// <see cref="IAlvoEntity"/>, and its assembly names the project it came from in an <see cref="AssemblyMetadataAttribute"/>
/// keyed <c>MMLib.Alvo.Analyzers.Project:{class}</c>. Discovery walks the entry assembly and every referenced assembly that
/// references the core; the shipped packages and the framework are never loaded. A class in an assembly loaded only at run
/// time (a plugin) is not found.
/// </remarks>
internal static class GeneratedEntityCheck
{
    internal const string GeneratorName = "MMLib.Alvo.Analyzers";

    internal const string ProjectMarker = GeneratorName + ".Project:";

    /// <summary>The shipped packages: they hold no generated class, so discovery never loads them.</summary>
    internal static IReadOnlySet<string> ShippedPackages { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "MMLib.Alvo", "MMLib.Alvo.Abstractions", "MMLib.Alvo.Admin", "MMLib.Alvo.Ai", "MMLib.Alvo.Data.EntityFrameworkCore",
        "MMLib.Alvo.Data.PostgreSql", "MMLib.Alvo.Data.Sqlite", "MMLib.Alvo.Identity", "MMLib.Alvo.TestHost",
    };

    /// <summary>The generated classes in <paramref name="root"/> and the assemblies it references that reference the core.</summary>
    internal static IReadOnlyList<Type> Discover(Assembly? root)
    {
        if (root is null)
        {
            return [];
        }

        var core = typeof(GeneratedEntityCheck).Assembly.GetName().Name;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>([root]);
        var found = new List<Type>();
        while (pending.TryPop(out var assembly))
        {
            if (seen.Add(assembly.FullName ?? assembly.GetName().Name!))
            {
                found.AddRange(GeneratedIn(assembly));
                PushReferencing(assembly, core, pending);
            }
        }

        return found;
    }

    /// <summary>The classes of <paramref name="types"/> generated from the project named <paramref name="project"/>.</summary>
    internal static IReadOnlyList<Type> OfProject(IEnumerable<Type> types, string project) =>
        [.. types.Where(type => string.Equals(ProjectOf(type), project, StringComparison.Ordinal))];

    /// <summary>Each class of <paramref name="types"/> that does not fit <paramref name="schema"/>, with why.</summary>
    internal static IReadOnlyList<(RecordShape Shape, IReadOnlyList<ShapeProblem> Problems)> Stale(IEnumerable<Type> types, SchemaModel schema) =>
        [.. types.Select(RecordShape.Of).Select(shape => (shape, Problems(shape, schema))).Where(stale => stale.Item2.Count > 0)];

    private static IReadOnlyList<ShapeProblem> Problems(RecordShape shape, SchemaModel schema) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, shape.Entity, StringComparison.Ordinal)) is { } entity
            ? [.. ShapeMapping.Problems(shape, entity, fromEventImages: false)]
            : [new($"The live schema has no entity '{shape.Entity}'.", "Regenerate from the current descriptor.")];

    private static string? ProjectOf(Type type) =>
        type.Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(metadata => string.Equals(metadata.Key, ProjectMarker + type.FullName, StringComparison.Ordinal))?.Value;

    private static void PushReferencing(Assembly assembly, string? core, Stack<Assembly> pending)
    {
        foreach (var loaded in assembly.GetReferencedAssemblies().Where(Candidate).Select(TryLoad).OfType<Assembly>())
        {
            if (loaded.GetReferencedAssemblies().Any(name => name.Name == core))
            {
                pending.Push(loaded);
            }
        }
    }

    private static bool Candidate(AssemblyName name) =>
        name.Name is { } simple
        && !ShippedPackages.Contains(simple)
        && !simple.StartsWith("System", StringComparison.Ordinal)
        && !simple.StartsWith("Microsoft", StringComparison.Ordinal)
        && simple is not ("netstandard" or "mscorlib");

    private static Assembly? TryLoad(AssemblyName name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (Exception failure) when (failure is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static IEnumerable<Type> GeneratedIn(Assembly assembly) =>
        LoadableTypes(assembly).Where(type => type.IsClass
            && typeof(IAlvoEntity).IsAssignableFrom(type)
            && type.GetCustomAttribute<AlvoEntityAttribute>() is not null
            && type.GetCustomAttribute<GeneratedCodeAttribute>()?.Tool == GeneratorName);

    private static Type[] LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return [.. partial.Types.OfType<Type>()];
        }
    }
}
