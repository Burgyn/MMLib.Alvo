namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>What a <c>beforeCreate</c> hook's patch does to the row an insert actually carries.</summary>
public class EfAlvoDataCreateHookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A hook that patches a field to <see langword="null"/> clears it, rather than leaving the caller's own
    /// value standing.
    /// </summary>
    /// <remarks>
    /// The patched candidate is built as a new bag, and a field the patch nulled is left out of it — the
    /// insert's own rule for "store nothing here", since the bag's value type cannot hold a null. Keep the
    /// entry instead and the hook's null is silently discarded: the row is stored with the value the author
    /// asked to remove, which is the wrong-stored-value class a <c>mutate</c> exists to prevent rather than
    /// to cause.
    /// </remarks>
    [Fact]
    public async Task A_hook_that_nulls_a_field_clears_it_instead_of_keeping_the_callers_value()
    {
        await using var world = await WritePathWorld.StartAsync(
            WritePathFixture.Descriptor(), WritePathFixture.Schema());
        world.Hooks.Patch = _ => new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = null };

        var created = await world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("visible"), world.Caller, cancellationToken: Ct);

        created["title"].ShouldBeNull();
        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" WHERE "title" IS NULL""")).ShouldBe(1L);
    }
}
