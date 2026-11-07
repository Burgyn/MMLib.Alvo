using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MMLib.Alvo.Analyzers.Generator;
using System.Reflection;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>The generator's cache: an unrelated edit reuses every step, a descriptor edit re-reads only that file.</summary>
public sealed class AlvoEntityGeneratorIncrementalTests
{
    private static readonly string _helpDesk = Descriptors.HelpDesk;

    [Fact]
    public void An_unrelated_edit_reuses_every_cached_step()
    {
        var (driver, compilation) = GeneratorDriverHarness.Create(("help-desk.alvo.json", _helpDesk, "true"));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated {}", cancellationToken: TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        var run = driver.GetRunResult().Results.Single();

        run.TrackedSteps[AlvoEntityGenerator.Steps.Models].SelectMany(step => step.Outputs)
            .ShouldAllBe(output => output.Reason == IncrementalStepRunReason.Cached || output.Reason == IncrementalStepRunReason.Unchanged);
        run.TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(step => step.Outputs)
            .ShouldAllBe(output => output.Reason == IncrementalStepRunReason.Cached);
    }

    [Fact]
    public void Editing_one_descriptor_rereads_only_that_file()
    {
        var other = Descriptors.With("""{ "notes": { "fields": { "body": { "type": "string" } } } }""", name: "notes");
        var (driver, compilation, texts) = GeneratorDriverHarness.CreateWithTexts(
            GeneratorDriverHarness.RootNamespace, new InputFile("help-desk.alvo.json", _helpDesk), new InputFile("notes.alvo.json", other));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        var edited = new GeneratorDriverHarness.InMemoryText("notes.alvo.json", other.Replace("\"body\"", "\"text\"", StringComparison.Ordinal));
        driver = driver.ReplaceAdditionalText(texts[1], edited)
            .RunGenerators(compilation, TestContext.Current.CancellationToken);

        driver.GetRunResult().Results.Single().TrackedSteps[AlvoEntityGenerator.Steps.Models].SelectMany(step => step.Outputs)
            .Select(output => output.Reason).ShouldBe([IncrementalStepRunReason.Cached, IncrementalStepRunReason.Modified], ignoreOrder: true);
    }

    [Fact]
    public void The_model_holds_no_symbol_or_compilation() => Allowed(typeof(DescriptorModel), []);

    private static void Allowed(Type type, HashSet<Type> seen)
    {
        if (!seen.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract"))
        {
            var held = property.PropertyType;
            var item = held.IsGenericType && held.GetGenericTypeDefinition() == typeof(EquatableArray<>) ? held.GetGenericArguments()[0] : held;
            if (item == typeof(string) || item == typeof(bool) || item == typeof(int?) || item == typeof(int) || item.IsEnum)
            {
                continue;
            }

            item.Namespace.ShouldBe(typeof(DescriptorModel).Namespace, $"{type.Name}.{property.Name} holds {held}");
            item.GetMethod("<Clone>$").ShouldNotBeNull($"{type.Name}.{property.Name} holds {held}, which is not a model record");
            Allowed(item, seen);
        }
    }
}
