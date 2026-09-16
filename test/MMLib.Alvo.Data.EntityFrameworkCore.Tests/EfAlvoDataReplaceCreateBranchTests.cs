namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The create branch of a create-or-replace: the verdict reached over the candidate, the row key the path
/// named, and the second verdict a hook that moved that key earns.
/// </summary>
/// <remarks>
/// This branch is the one place on the write path where the row a caller gets is not the row the caller
/// named unless three separate decisions all hold: the candidate is judged before any hook runs, the key is
/// put back wherever a hook moved it, and the verdict is reached <em>again</em> over what the put-back
/// produced. Each is invisible from the others — a suite that only writes rows through happy paths sees none
/// of them.
/// </remarks>
public class EfAlvoDataReplaceCreateBranchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The candidate is judged against the create rule before anything is written, even when no hook runs.
    /// Unjudged, a row the entity's own <c>create</c> rule refuses is stored by naming an unused id.
    /// </summary>
    [Fact]
    public async Task A_candidate_the_create_rule_refuses_is_never_stored()
    {
        await using var world = await StartAsync(WritePathFixture.Descriptor(create: "owner_id == @user.id"));

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.ReplaceAsync(
            WritePathFixture.Entity, Guid.NewGuid(), WritePathFixture.Payload("foreign", Guid.NewGuid()),
            world.Caller, cancellationToken: Ct));

        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" """)).ShouldBe(0L);
    }

    /// <summary>
    /// A hook that moves the row key does not move the row: the path named it, the answer reports it and the
    /// idempotency record would file it, so the path wins and the key is put back.
    /// </summary>
    [Fact]
    public async Task A_hook_cannot_move_the_row_off_the_id_the_path_named()
    {
        await using var world = await StartAsync(WritePathFixture.Descriptor());
        var id = Guid.NewGuid();
        world.Hooks.Patch = _ => Patch(("id", Guid.NewGuid()));

        var result = await world.Data.ReplaceAsync(
            WritePathFixture.Entity, id, WritePathFixture.Payload("hooked"), world.Caller, cancellationToken: Ct);

        result.Row["id"].ShouldBe(id);
        Guid.Parse((string)(await world.ScalarAsync("""SELECT "id" FROM "note" """))!).ShouldBe(id);
    }

    /// <summary>
    /// Putting the key back re-opens the verdict, because the row about to be stored is no longer the row that
    /// was judged. Skip that second verdict and a hook can carry a candidate past <c>WITH CHECK</c> under a
    /// key the rule allows, after which the key is silently changed to one it does not.
    /// </summary>
    /// <remarks>
    /// The rule admits the candidate as the caller sent it (the named key, the sent title) and admits what the
    /// hook made of it (a different key, the hook's title) — and refuses the combination the put-back
    /// actually produces. Contrived by construction: the gap only exists for a row whose verdict depends on
    /// its own key, which is exactly the shape the second check is there for.
    /// </remarks>
    [Fact]
    public async Task The_verdict_is_reached_again_over_the_row_the_key_was_put_back_into()
    {
        await using var world = await StartAsync(WritePathFixture.Descriptor(create: KeyDependentRule));
        world.Hooks.Patch = _ => Patch(("id", Guid.NewGuid()), ("title", "hooked"));

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.ReplaceAsync(
            WritePathFixture.Entity, world.Caller.User.Value, WritePathFixture.Payload("seed"), world.Caller,
            cancellationToken: Ct));

        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" """)).ShouldBe(0L);
    }

    /// <summary>A <c>create</c> rule whose verdict depends on the row key as well as on a caller field.</summary>
    private static string KeyDependentRule =>
        "(id == @user.id && title == 'seed') || (id != @user.id && title == 'hooked')";

    private static Dictionary<string, object?> Patch(params (string Field, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Field, field => field.Value, StringComparer.Ordinal);

    private static Task<WritePathWorld> StartAsync(Descriptor.AlvoDescriptor descriptor) =>
        WritePathWorld.StartAsync(descriptor, WritePathFixture.Schema());
}
