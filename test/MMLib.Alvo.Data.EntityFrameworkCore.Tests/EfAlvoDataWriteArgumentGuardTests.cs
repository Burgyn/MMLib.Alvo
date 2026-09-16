namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Where each write's own argument guards sit in the order: <b>before</b> the policy is resolved, and before
/// the idempotency record is written.
/// </summary>
/// <remarks>
/// <para>
/// A test that only asserts "a null payload throws" cannot see these guards at all — delete one and the very
/// next layer raises the same exception type a moment later, from inside a context the write had already
/// opened. What distinguishes them is <em>precedence</em>: a broken call is answered as a broken call even
/// when the entity is one the caller may not touch, so a malformed request never has to be explained as a
/// denial, and an unusable idempotency key is refused rather than filed.
/// </para>
/// <para>
/// The entity these use is deliberately one the schema does not declare, which is how a deleted guard becomes
/// visible: the policy engine denies it, so the mutant answers <see cref="AlvoAuthorizationException"/> where
/// the guard answers the caller's own mistake.
/// </para>
/// </remarks>
public class EfAlvoDataWriteArgumentGuardTests
{
    /// <summary>The verbs whose argument guards are identical and are therefore checked together.</summary>
    public enum Verb
    {
        /// <summary><c>CreateAsync</c>.</summary>
        Create,

        /// <summary><c>UpdateAsync</c>.</summary>
        Update,

        /// <summary><c>ReplaceAsync</c>.</summary>
        Replace,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unknown => "ghost";

    [Theory]
    [InlineData(Verb.Create)]
    [InlineData(Verb.Update)]
    [InlineData(Verb.Replace)]
    public async Task A_blank_entity_name_is_the_callers_mistake_and_not_a_denial(Verb verb)
    {
        await using var world = await StartAsync();

        var refusal = await Should.ThrowAsync<ArgumentException>(
            () => Invoke(verb, world, "   ", Guid.NewGuid(), Payload(verb), world.Caller));

        refusal.ParamName.ShouldBe("entity");
    }

    [Theory]
    [InlineData(Verb.Create)]
    [InlineData(Verb.Update)]
    [InlineData(Verb.Replace)]
    public async Task A_null_payload_is_refused_before_the_entity_is_even_resolved(Verb verb)
    {
        await using var world = await StartAsync();

        var refusal = await Should.ThrowAsync<ArgumentNullException>(
            () => Invoke(verb, world, Unknown, Guid.NewGuid(), values: null!, world.Caller));

        refusal.ParamName.ShouldBe("values");
    }

    /// <summary>
    /// A blank idempotency key is refused rather than filed. Unguarded, the write succeeds and every caller
    /// sending a blank key shares one record — <see cref="AlvoIdempotency.EnsureUsableKey"/>'s own argument,
    /// which only holds while every verb actually calls it.
    /// </summary>
    [Theory]
    [InlineData(Verb.Create)]
    [InlineData(Verb.Update)]
    [InlineData(Verb.Replace)]
    public async Task A_blank_idempotency_key_is_refused_rather_than_recorded(Verb verb)
    {
        await using var world = await StartAsync();
        var id = await SeededRowAsync(world);

        var refusal = await Should.ThrowAsync<ArgumentException>(() => Invoke(
            verb, world, WritePathFixture.Entity, id, Payload(verb), world.Caller,
            new AlvoIdempotency(string.Empty, "fingerprint")));

        refusal.ParamName.ShouldBe("key");
    }

    private static Task<WritePathWorld> StartAsync() =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema());

    /// <summary>The row the update and replace verbs address, so an unguarded call reaches a real write.</summary>
    private static async Task<Guid> SeededRowAsync(WritePathWorld world)
    {
        var seeded = await world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("seed"), world.Caller, cancellationToken: Ct);

        return (Guid)seeded["id"]!;
    }

    /// <summary>A payload that verb would otherwise accept — only a create may carry the tenant.</summary>
    private static Dictionary<string, object?> Payload(Verb verb) => verb == Verb.Create
        ? WritePathFixture.CreatePayload("guarded")
        : WritePathFixture.Payload("guarded");

    private static Task Invoke(
        Verb verb, WritePathWorld world, string entity, Guid id, IReadOnlyDictionary<string, object?> values,
        AlvoContext context, AlvoIdempotency? idempotency = null) => verb switch
        {
            Verb.Create => world.Data.CreateAsync(entity, values, context, idempotency, Ct),
            Verb.Update => world.Data.UpdateAsync(entity, id, values, context, null, idempotency, Ct),
            Verb.Replace => world.Data.ReplaceAsync(entity, id, values, context, null, idempotency, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(verb)),
        };
}
