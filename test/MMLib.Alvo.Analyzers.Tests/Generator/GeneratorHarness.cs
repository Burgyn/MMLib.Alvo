using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MMLib.Alvo.Analyzers.Generator;
using System.Collections.Immutable;
using System.Reflection;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>
/// Emits a descriptor's sources, compiles them — with any probe source — against the running process's own assemblies, the
/// real core among them, and loads the result. Offline and deterministic, as <see cref="AnalyzerHarness"/> is.
/// </summary>
internal static class GeneratorHarness
{
    internal const string Namespace = "HelpDesk";

    private static readonly Lazy<ImmutableArray<MetadataReference>> _references = new(() =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ]);

    /// <summary>The running process's own assemblies, the real core among them.</summary>
    internal static ImmutableArray<MetadataReference> References => _references.Value;

    internal static DescriptorModel Model(string descriptor, string @namespace = Namespace)
    {
        var result = DescriptorReader.Read("descriptor.alvo.json", descriptor, @namespace);
        result.Error.ShouldBeNull();

        return result.Model.ShouldNotBeNull();
    }

    internal static string[] Sources(DescriptorModel model) =>
        [.. model.Entities.Select(entity => EntityEmitter.Emit(model, entity)), RolesEmitter.Emit(model)];

    internal static CSharpCompilation Compile(IEnumerable<string> sources)
    {
        var trees = sources.Select((source, index) => CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Diagnose),
            path: $"Generated{index}.cs",
            cancellationToken: TestContext.Current.CancellationToken));

        return CSharpCompilation.Create(
            $"Generated{Guid.NewGuid():N}",
            trees,
            _references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>The warnings and errors compiling <paramref name="sources"/> reports.</summary>
    internal static IReadOnlyList<Diagnostic> Problems(IEnumerable<string> sources) =>
    [
        .. Compile(sources).GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning),
    ];

    /// <summary>Compiles the descriptor's sources and <paramref name="probes"/>, warning-free, and loads the assembly.</summary>
    internal static Assembly Load(string descriptor, params string[] probes)
    {
        var compilation = Compile([.. Sources(Model(descriptor)), .. probes]);
        compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .ShouldBeEmpty("generated code must compile without a warning");
        using var image = new MemoryStream();
        compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success.ShouldBeTrue();

        return Assembly.Load(image.ToArray());
    }

    /// <summary>Calls a static method of a probe class, unwrapping what it throws.</summary>
    internal static object? Run(Assembly assembly, string method, params object?[] arguments)
    {
        try
        {
            return assembly.GetType("Probe", throwOnError: true)!.GetMethod(method)!.Invoke(null, arguments);
        }
        catch (TargetInvocationException thrown) when (thrown.InnerException is { } inner)
        {
            throw inner;
        }
    }
}
