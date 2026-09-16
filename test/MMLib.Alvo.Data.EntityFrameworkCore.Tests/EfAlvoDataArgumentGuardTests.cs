using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What <see cref="EfAlvoData"/> refuses before it does anything: a dependency it was handed as
/// <see langword="null"/>, and a caller argument the port's own contract forbids.
/// </summary>
/// <remarks>
/// A dependency guard is not ceremony here. Six of the nine constructor arguments are only ever
/// <em>stored</em>, so without the guard the type constructs cleanly and fails much later, inside a
/// request, as a <see cref="NullReferenceException"/> off this port's failure contract — which is the
/// difference between a registration bug a host sees at startup and a 500 a caller sees in production.
/// </remarks>
public class EfAlvoDataArgumentGuardTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    [Theory]
    [InlineData("policy")]
    [InlineData("evaluator")]
    [InlineData("hooks")]
    [InlineData("predicates")]
    [InlineData("fields")]
    [InlineData("dialect")]
    [InlineData("contexts")]
    [InlineData("time")]
    [InlineData("options")]
    public void A_null_dependency_is_refused_and_named(string dependency) =>
        Should.Throw<ArgumentNullException>(() => Construct(dependency)).ParamName.ShouldBe(dependency);

    [Fact]
    public async Task A_list_read_refuses_a_null_query() =>
        (await Should.ThrowAsync<ArgumentNullException>(
            async () => await Port().QueryAsync(null!, AlvoDataFixtures.Caller))).ParamName.ShouldBe("query");

    [Fact]
    public async Task A_list_read_refuses_a_null_context() =>
        (await Should.ThrowAsync<ArgumentNullException>(
            async () => await Port().QueryAsync(new AlvoQuery { Entity = AlvoDataFixtures.Vehicle.Name }, null!)))
            .ParamName.ShouldBe("context");

    /// <summary>
    /// A blank entity name is a malformed argument, not a denial: it never reaches the policy engine, which
    /// would answer it with the same <see cref="AlvoAuthorizationException"/> an unknown entity gets and so
    /// lose the distinction a caller needs to fix their own call.
    /// </summary>
    /// <param name="entity">The blank name under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_get_refuses_a_blank_entity_name(string? entity) =>
        await Should.ThrowAsync<ArgumentException>(
            async () => await Port().GetAsync(entity!, Guid.NewGuid(), AlvoDataFixtures.Caller));

    [Fact]
    public async Task A_get_refuses_a_null_context() =>
        (await Should.ThrowAsync<ArgumentNullException>(
            async () => await Port().GetAsync(AlvoDataFixtures.Vehicle.Name, Guid.NewGuid(), null!)))
            .ParamName.ShouldBe("context");

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A fully wired port whose every dependency is real — these tests are about the arguments a
    /// <em>caller</em> supplies, so nothing may be refused for a reason the harness itself introduced.
    /// </summary>
    private EfAlvoData Port() => Construct(nulled: null);

    /// <summary>
    /// Constructs the port with <paramref name="nulled"/> passed as <see langword="null"/>, or with every
    /// argument supplied when it is <see langword="null"/> itself.
    /// </summary>
    private EfAlvoData Construct(string? nulled) => new(
        Unless<IPolicyEngine>(nulled, "policy"),
        Unless<IPredicateEvaluator>(nulled, "evaluator"),
        Unless<IBeforeHookRunner>(nulled, "hooks"),
        Unless<IPredicateRenderer>(nulled, "predicates"),
        nulled == "fields" ? null! : new TestFieldSqlRenderer(),
        nulled == "dialect" ? null! : new TestSqlDialect(),
        nulled == "contexts" ? null! : Contexts(),
        nulled == "time" ? null! : TimeProvider.System,
        nulled == "options" ? null! : new AlvoOptions());

    private T Unless<T>(string? nulled, string parameter)
        where T : class => nulled == parameter ? null! : _services.GetRequiredService<T>();

    /// <summary>
    /// A factory over the canonical fixture entity. It never opens a connection in this file — every test
    /// here is refused before a context is created — so the configuration only has to be well formed.
    /// </summary>
    private static AlvoDataContextFactory Contexts() => new(
        new SingleEntitySchemaRegistry(),
        static options => options.UseSqlite("Data Source=:memory:", static sqlite => sqlite.UseRelationalNulls()));

    private sealed class SingleEntitySchemaRegistry : ISchemaRegistry
    {
        public SchemaModel GetSchema() => new([AlvoDataFixtures.Vehicle]);
    }
}
