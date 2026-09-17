using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The factory's two jobs, neither of which needs a database: handing each context the provider it was
/// configured with, and minting a fresh model token whenever the applied schema is replaced. The token is
/// what <see cref="AlvoModelCacheKeyFactory"/> puts in EF's model cache key, so getting it wrong means a
/// runtime apply is invisible — a field added by an apply would never be queried and a removed one still
/// would be, with no error anywhere.
/// </summary>
public class AlvoDataContextFactoryTests
{
    /// <summary>
    /// The configuration the factory was built with reaches every context it creates. Without it the context
    /// has no provider at all, so nothing it is asked for can be answered.
    /// </summary>
    [Fact]
    public void Every_context_is_created_with_the_provider_the_factory_was_configured_with()
    {
        using var context = Factory(new ReplaceableRegistry(Schema())).Create();

        context.Database.ProviderName.ShouldBe("Microsoft.EntityFrameworkCore.Sqlite");
        context.Model.FindEntityType(AlvoDataFixtures.Vehicle.Name).ShouldNotBeNull();
    }

    /// <summary>
    /// Two contexts over one applied model share a token, so EF builds the model once and serves it to both.
    /// A token minted per operation would rebuild the whole model on every request.
    /// </summary>
    [Fact]
    public void Two_contexts_over_one_applied_model_share_a_token()
    {
        var factory = Factory(new ReplaceableRegistry(Schema()));

        using var first = factory.Create();
        using var second = factory.Create();

        second.ModelToken.ShouldBe(first.ModelToken);
    }

    /// <summary>
    /// A replaced applied model mints a new token, which is the whole point: EF caches one model per context
    /// CLR type, so without a new token an entity added by a runtime apply would never be queried.
    /// </summary>
    [Fact]
    public void A_replaced_applied_model_mints_a_new_token()
    {
        var registry = new ReplaceableRegistry(Schema());
        var factory = Factory(registry);
        using var before = factory.Create();

        registry.Current = new SchemaModel([AlvoDataFixtures.Vehicle, Trailer()]);
        using var after = factory.Create();

        after.ModelToken.ShouldNotBe(before.ModelToken);
    }

    /// <summary>
    /// Keyed on reference identity rather than on content, so an apply that produces a model carrying the
    /// very same entities still mints a new token. A deep hash of every entity and field is the alternative,
    /// and it would be paid on every operation.
    /// </summary>
    [Fact]
    public void A_replacement_carrying_the_same_entities_is_still_a_new_model()
    {
        var registry = new ReplaceableRegistry(Schema());
        var factory = Factory(registry);
        using var before = factory.Create();
        var replacement = Schema();
        replacement.Entities.ShouldBe(registry.Current.Entities);

        registry.Current = replacement;

        using var after = factory.Create();
        after.ModelToken.ShouldNotBe(before.ModelToken);
    }

    [Fact]
    public void Both_dependencies_are_required()
    {
        Should.Throw<ArgumentNullException>(() => new AlvoDataContextFactory(null!, Sqlite));
        Should.Throw<ArgumentNullException>(
            () => new AlvoDataContextFactory(new ReplaceableRegistry(Schema()), null!));
    }

    private static AlvoDataContextFactory Factory(ISchemaRegistry schemas) => new(schemas, Sqlite);

    private static void Sqlite(DbContextOptionsBuilder options)
        => options.UseSqlite("Data Source=:memory:", static sqlite => sqlite.UseRelationalNulls());

    /// <summary>A fresh applied model over the canonical fixture — a new object on every call.</summary>
    private static SchemaModel Schema() => new([AlvoDataFixtures.Vehicle]);

    /// <summary>A second entity, so a replaced model can differ by content and not only by identity.</summary>
    private static EntitySchema Trailer() => new()
    {
        Name = "trailer",
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    /// <summary>A registry whose applied model can be replaced, the way an apply replaces it.</summary>
    private sealed class ReplaceableRegistry(SchemaModel schema) : ISchemaRegistry
    {
        internal SchemaModel Current { get; set; } = schema;

        public SchemaModel GetSchema() => Current;
    }
}
