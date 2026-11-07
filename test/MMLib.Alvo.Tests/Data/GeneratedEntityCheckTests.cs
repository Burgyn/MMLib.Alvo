using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Tests.Migrations;

using System.CodeDom.Compiler;
using System.Reflection;

[assembly: AssemblyMetadata("MMLib.Alvo.Analyzers.Project:MMLib.Alvo.Tests.Data.GeneratedDepot", "depots")]
[assembly: AssemblyMetadata("MMLib.Alvo.Analyzers.Project:MMLib.Alvo.Tests.Data.GeneratedDiscontinuedDepot", "depots")]
[assembly: AssemblyMetadata("MMLib.Alvo.Analyzers.Project:MMLib.Alvo.Tests.Data.GeneratedWarehouse", "warehouses")]

namespace MMLib.Alvo.Tests.Data;

[GeneratedCode("MMLib.Alvo.Analyzers", "1.0")]
[AlvoEntity("depots")]
public sealed class GeneratedDepot(AlvoRecord record) : IAlvoEntity
{
    public AlvoRecord Record { get; } = record;

    [AlvoField("city")]
    public string? City => Record.Get<string?>("city");
}

[GeneratedCode("MMLib.Alvo.Analyzers", "1.0")]
[AlvoEntity("depots")]
public sealed class GeneratedDiscontinuedDepot(AlvoRecord record) : IAlvoEntity
{
    public AlvoRecord Record { get; } = record;

    [AlvoField("city")]
    public string? City => Record.Get<string?>("city");

    [AlvoField("discontinued_on")]
    public DateOnly? DiscontinuedOn => Record.Get<DateOnly?>("discontinued_on");
}

[GeneratedCode("MMLib.Alvo.Analyzers", "1.0")]
[AlvoEntity("warehouses")]
public sealed class GeneratedWarehouse(AlvoRecord record) : IAlvoEntity
{
    public AlvoRecord Record { get; } = record;
}

[AlvoEntity("depots")]
public sealed class HandWrittenDepot(AlvoRecord record) : IAlvoEntity
{
    public AlvoRecord Record { get; } = record;
}

[GeneratedCode("Other", "1.0")]
[AlvoEntity("depots")]
public sealed class OtherGeneratorsDepot(AlvoRecord record) : IAlvoEntity
{
    public AlvoRecord Record { get; } = record;
}

/// <summary>Which classes count as the typed-entity generator's, and when one no longer fits a schema.</summary>
public sealed class GeneratedEntityCheckTests
{
    private static readonly Type[] _generated = [typeof(GeneratedDepot), typeof(GeneratedDiscontinuedDepot), typeof(GeneratedWarehouse)];

    [Fact]
    public void A_field_missing_from_the_live_schema_is_a_problem()
    {
        var stale = GeneratedEntityCheck.Stale([typeof(GeneratedDiscontinuedDepot)], BootServiceWorld.SchemaOf(BootServiceWorld.City)).ShouldHaveSingleItem();

        stale.Shape.Type.ShouldBe(typeof(GeneratedDiscontinuedDepot));
        stale.Problems.ShouldHaveSingleItem().Message.ShouldContain("discontinued_on");
    }

    [Fact]
    public void A_removed_entity_is_a_problem() =>
        GeneratedEntityCheck.Stale([typeof(GeneratedWarehouse)], BootServiceWorld.SchemaOf(BootServiceWorld.City))
            .ShouldHaveSingleItem().Problems.ShouldHaveSingleItem().Message.ShouldContain("'warehouses'");

    [Fact]
    public void A_fitting_class_has_none() =>
        GeneratedEntityCheck.Stale([typeof(GeneratedDepot)], BootServiceWorld.SchemaOf(BootServiceWorld.City)).ShouldBeEmpty();

    [Fact]
    public void A_hand_written_wrapper_is_not_discovered() =>
        GeneratedEntityCheck.Discover(typeof(GeneratedEntityCheckTests).Assembly).ShouldNotContain(typeof(HandWrittenDepot));

    [Fact]
    public void Another_generators_class_is_not_discovered() =>
        GeneratedEntityCheck.Discover(typeof(GeneratedEntityCheckTests).Assembly).ShouldNotContain(typeof(OtherGeneratorsDepot));

    [Fact]
    public void Discovery_follows_references_to_assemblies_that_reference_the_core() =>
        GeneratedEntityCheck.Discover(typeof(GeneratedEntityCheckTests).Assembly).ShouldBe(_generated, ignoreOrder: true);

    [Fact]
    public void No_root_discovers_nothing() =>
        GeneratedEntityCheck.Discover(null).ShouldBeEmpty();

    [Fact]
    public void Only_the_booting_project_s_classes_are_checked() =>
        GeneratedEntityCheck.OfProject(_generated, "depots").ShouldBe([typeof(GeneratedDepot), typeof(GeneratedDiscontinuedDepot)], ignoreOrder: true);

    [Fact]
    public void Every_shipped_package_is_excluded_from_discovery_by_its_exact_name()
    {
        var src = Path.Combine(RepositoryRoot.Find(), "src");
        var packable = Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(project => !File.ReadAllText(project).Contains("<IsPackable>false</IsPackable>", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension);

        GeneratedEntityCheck.ShippedPackages.ShouldBe(packable!, ignoreOrder: true);
    }
}
