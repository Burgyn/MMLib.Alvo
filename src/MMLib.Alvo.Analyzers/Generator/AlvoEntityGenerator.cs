using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using MMLib.Alvo.Analyzers.Generator;
using System.Collections.Immutable;
using System.Text;

namespace MMLib.Alvo.Analyzers;

/// <summary>
/// Generates, for every additional file marked <c>AlvoDescriptor="true"</c>, a typed wrapper class, its enums and its patch
/// per entity, and the project's role constants.
/// </summary>
/// <remarks>
/// Roslyn instantiates it from the <c>MMLib.Alvo</c> package's <c>analyzers/dotnet/cs</c> folder; it is not an API to call.
/// Each file is read into an equatable model once, so an edit that leaves the model equal reuses every cached step.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class AlvoEntityGenerator : IIncrementalGenerator
{
    private const string DefaultNamespace = "Alvo.Generated";

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var rootNamespace = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : DefaultNamespace);

        var files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, ct) => DescriptorFile.From(pair.Left, pair.Right.GetOptions(pair.Left), ct))
            .Where(static file => file is not null)
            .Select(static (file, _) => file!.Value)
            .WithTrackingName(Steps.Files);

        var models = files
            .Combine(rootNamespace)
            .Select(static (pair, _) => new DescriptorRead(pair.Left.Path, DescriptorReader.Read(pair.Left.Path, pair.Left.Text, pair.Left.Namespace ?? pair.Right)))
            .WithTrackingName(Steps.Models);

        context.RegisterSourceOutput(models.Collect(), static (spc, reads) => Emit(spc, reads));
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<DescriptorRead> reads)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var read in reads.OrderBy(static r => r.Path, StringComparer.Ordinal))
        {
            if (read.Result.Model is { } model)
            {
                EmitModel(context, model, taken);
            }
            else
            {
                context.ReportDiagnostic(Diagnostic.Create(GeneratorDiagnostics.Unreadable, Where(read), read.Result.File, read.Result.Error));
            }
        }
    }

    private static Location Where(DescriptorRead read)
    {
        if (read.Result.Line <= 0)
        {
            return Location.None;
        }

        var position = new LinePosition(read.Result.Line - 1, read.Result.Column - 1);

        return Location.Create(read.Path, default, new LinePositionSpan(position, position));
    }

    private static void EmitModel(SourceProductionContext context, DescriptorModel model, HashSet<string> taken)
    {
        var roles = RolesEmitter.ClassName(model);
        AddOnce(context, taken, model, roles, [roles], () => RolesEmitter.Emit(model));
        foreach (var entity in model.Entities)
        {
            AddOnce(context, taken, model, entity.ClassName, [.. CSharpNames.TypeNames(entity)], () => EntityEmitter.Emit(model, entity));
        }
    }

    private static void AddOnce(
        SourceProductionContext context, HashSet<string> taken, DescriptorModel model, string hint, string[] typeNames, Func<string> source)
    {
        var qualified = typeNames.Select(name => $"{model.Namespace}.{name}").ToArray();
        if (qualified.FirstOrDefault(taken.Contains) is { } clash)
        {
            context.ReportDiagnostic(Diagnostic.Create(GeneratorDiagnostics.Collision, Location.None, model.File, clash));
            return;
        }

        taken.UnionWith(qualified);
        context.AddSource($"{model.Namespace}.{hint}.g.cs", SourceText.From(source(), Encoding.UTF8));
    }

    /// <summary>The tracking names of the generator's cached steps, for cache tests.</summary>
    internal static class Steps
    {
        /// <summary>The marked files, read.</summary>
        public const string Files = "AlvoDescriptorFiles";

        /// <summary>The files' models.</summary>
        public const string Models = "AlvoDescriptorModels";
    }
}
