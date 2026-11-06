using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Reads;
using MMLib.Alvo.Reads.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Expressions;

using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Reads;

public interface IRegionDirectory
{
    string RegionOf(AlvoContext caller);
}

public sealed class DirectoryNarrowing(IRegionDirectory directory) : IQueryNarrowing
{
    public AlvoFilter? Narrow(QueryNarrowing query) =>
        new AlvoComparison("title", AlvoFilterOperator.Eq, directory.RegionOf(query.Caller));
}

public sealed class DisposableNarrowing : IQueryNarrowing, IDisposable
{
    internal static int Disposed { get; private set; }

    public AlvoFilter? Narrow(QueryNarrowing query) => new AlvoComparison("title", AlvoFilterOperator.Eq, query.Entity);

    public void Dispose() => Disposed++;
}

/// <summary>What registering a query narrowing records, and what the boot refuses about it before any descriptor.</summary>
public sealed class ReadExtensionRegistrationTests
{
    [Fact]
    public void A_narrowing_records_its_kind_entities_and_order_of_registration()
    {
        var registry = Registry(alvo => alvo
            .Entity("tickets", t => t.Narrow("first", (QueryNarrowing _) => null))
            .Entities(["tickets", "notes"], e => e.Narrow<DirectoryNarrowing>("second")));

        registry.ReadExtensions.Select(read => (read.Id, read.Kind, read.Invoker.IsClass)).ShouldBe(
            [("first", ReadExtensionKind.Narrowing, false), ("second", ReadExtensionKind.Narrowing, true)]);
        registry.ReadExtensions[1].Entities.ShouldBe(["tickets", "notes"]);
        registry.ReadExtensions[1].Invoker.TypeName.ShouldBe(typeof(DirectoryNarrowing).FullName);
        registry.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void A_narrowing_that_fits_binds()
    {
        var result = Compile(alvo => alvo.Entity("tickets", t => t.Narrow("open-only", (QueryNarrowing _) => null)));

        result.Problems.ShouldBeEmpty();
        result.Bindings.ReadExtension("open-only").ShouldNotBeNull().Id.ShouldBe("open-only");
    }

    [Fact]
    public void A_narrowing_whose_class_needs_an_unregistered_service_is_refused()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => call.Arg<Type>() != typeof(IRegionDirectory) && call.Arg<Type>() != typeof(DirectoryNarrowing));

        var problem = Checked(alvo => alvo.Entity("tickets", t => t.Narrow<DirectoryNarrowing>("by-region")), services).ShouldHaveSingleItem();

        (problem.Kind, problem.Locator).ShouldBe(("narrowing", "extensions/by-region/hook"));
        problem.Message.ShouldContain(typeof(IRegionDirectory).FullName!);
    }

    [Fact]
    public void A_narrowing_in_a_composition_without_a_relational_provider_is_refused()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => call.Arg<Type>() != typeof(IAlvoTransactions));

        Checked(alvo => alvo.Entity("tickets", t => t.Narrow("open-only", (QueryNarrowing _) => null)), services)
            .ShouldHaveSingleItem().Locator.ShouldBe("extensions/open-only/provider");
    }

    [Fact]
    public void A_typed_narrowing_whose_record_does_not_fit_is_refused_at_its_record()
    {
        var problems = Compile(alvo => alvo.Entity<TicketWithVat>(t => t.Narrow("vat-only", _ => ticket => ticket.VatId == "SK1"))).Problems;

        problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/vat-only/record");
    }

    [Fact]
    public void Two_read_extensions_with_one_id_are_refused()
    {
        var problems = Checked(alvo => alvo
            .Entity("tickets", t => t.Narrow("twice", (QueryNarrowing _) => null))
            .Entity("notes", n => n.Narrow("twice", (QueryNarrowing _) => null)), EveryService());

        var problem = problems.ShouldHaveSingleItem();
        (problem.Kind, problem.Locator).ShouldBe(("narrowing", "extensions/twice/id"));
    }

    [Fact]
    public async Task A_class_narrowing_is_built_from_the_read_s_services_and_disposed_after_the_call()
    {
        var narrowing = Registry(alvo => alvo.Entity("tickets", t => t.Narrow<DisposableNarrowing>("disposable"))).ReadExtensions.ShouldHaveSingleItem();
        using var services = new ServiceCollection().BuildServiceProvider();
        var before = DisposableNarrowing.Disposed;

        var filter = await narrowing.Invoker.Narrow!(services, new QueryNarrowing(AlvoContext.Anonymous, "tickets", MMLib.Alvo.Rules.DataOperation.List));

        filter.ShouldBe(new AlvoComparison("title", AlvoFilterOperator.Eq, "tickets"));
        DisposableNarrowing.Disposed.ShouldBe(before + 1);
    }

    [Fact]
    public void An_abstract_narrowing_class_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => Registry(alvo => alvo.Entity("tickets", t => t.Narrow<AbstractNarrowing>("abstract"))));

    private static IReadOnlyList<ExtensionProblem> Checked(Action<IAlvoBuilder> configure, IServiceProviderIsService services) =>
        ExtensionBindingCompiler.Check(Registry(configure), CelFixtures.Compiler, services).Problems;

    public abstract class AbstractNarrowing : IQueryNarrowing
    {
        public abstract AlvoFilter? Narrow(QueryNarrowing query);
    }
}
