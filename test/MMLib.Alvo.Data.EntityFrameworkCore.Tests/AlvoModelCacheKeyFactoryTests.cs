using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The cache key itself: what it separates, and what it refuses. EF caches exactly one model per
/// <see cref="DbContext"/> CLR type, so every fact here is about the token that makes a re-applied
/// descriptor build a new model instead of silently serving the first schema the process ever saw.
/// </summary>
public class AlvoModelCacheKeyFactoryTests
{
    /// <summary>
    /// Two contexts over the same shape but a different applied schema are two models. Without this the
    /// field a runtime apply added would be invisible and a removed one would still be queried.
    /// </summary>
    [Fact]
    public void A_re_applied_schema_is_keyed_apart_from_the_one_before_it()
    {
        using var before = Context(Guid.NewGuid());
        using var after = Context(Guid.NewGuid());

        Key(after).ShouldNotBe(Key(before));
    }

    /// <summary>
    /// The other half of the same claim: one applied schema keeps one cached model, so the key is not
    /// merely unique per context instance (which would rebuild the model on every request).
    /// </summary>
    [Fact]
    public void One_applied_schema_keeps_one_cached_model()
    {
        var token = Guid.NewGuid();
        using var first = Context(token);
        using var second = Context(token);

        Key(second).ShouldBe(Key(first));
    }

    /// <summary>A design-time model is a different model, exactly as EF's own default key says.</summary>
    [Fact]
    public void A_design_time_model_is_keyed_apart_from_the_runtime_one()
    {
        using var context = Context(Guid.NewGuid());

        Key(context, designTime: true).ShouldNotBe(Key(context, designTime: false));
    }

    /// <summary>
    /// No context, no token: refused as the missing argument it is rather than dereferenced on the way
    /// into the refusal message.
    /// </summary>
    [Fact]
    public void A_missing_context_is_refused_as_a_missing_argument()
        => Should.Throw<ArgumentNullException>(() => new AlvoModelCacheKeyFactory().Create(null!, designTime: false))
            .ParamName.ShouldBe("context");

    /// <summary>
    /// Another context type is refused rather than defaulted — a shared fallback token would make two
    /// context types silently share one cached model, which is the exact bug this factory exists to prevent.
    /// </summary>
    [Fact]
    public void A_foreign_context_type_is_refused_rather_than_given_a_fallback_token()
    {
        using var foreign = new PlainContext(Options());

        Should.Throw<ArgumentException>(() => Key(foreign)).ParamName.ShouldBe("context");
    }

    /// <summary>
    /// The refusal names both types — the one that arrived and the one this factory serves — because it is
    /// raised on a wiring mistake nobody can debug from "invalid argument" alone.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_type_that_arrived_and_the_type_it_serves()
    {
        using var foreign = new PlainContext(Options());

        var refusal = Should.Throw<ArgumentException>(() => Key(foreign));

        refusal.Message.ShouldContain(typeof(PlainContext).ToString());
        refusal.Message.ShouldContain(nameof(AlvoDataContext));
    }

    private static object Key(DbContext context, bool designTime = false) =>
        new AlvoModelCacheKeyFactory().Create(context, designTime);

    private static AlvoDataContext Context(Guid modelToken) => new(Options(), Schema, modelToken);

    private static DbContextOptions Options()
    {
        var options = new DbContextOptionsBuilder();
        options.UseSqlite("Data Source=:memory:");
        return options.Options;
    }

    private static SchemaModel Schema { get; } = new([
        new EntitySchema
        {
            Name = "accounts",
            Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
        },
    ]);

    /// <summary>A context this factory does not serve, so the refusing branch has a real caller.</summary>
    private sealed class PlainContext(DbContextOptions options) : DbContext(options);
}
