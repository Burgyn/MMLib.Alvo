using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Who shares a working copy with whom, and how long one is kept.
/// </summary>
/// <remarks>
/// <b>Both facts are about the key, not the value.</b> A working copy is an operator's unapplied
/// descriptor, so two operators sharing one is a defect of the same kind as two tenants sharing a
/// row — and the reserved all-zero <see cref="UserId"/> is the value that quietly produces it,
/// because it is what <see cref="AlvoContext.Anonymous"/> carries and therefore what every caller
/// the resolver refuses arrives with.
/// </remarks>
public class WorkingCopyStoreTests
{
    [Fact]
    public void One_operator_gets_one_copy_across_screens()
    {
        var store = Store(out _);
        var eva = UserId.New();

        store.For(eva).ShouldBeSameAs(
            store.For(eva),
            "one descriptor and one apply means one copy per operator, composed over several screens");
    }

    [Fact]
    public void Two_operators_never_share_a_copy()
    {
        var store = Store(out _);

        store.For(UserId.New()).ShouldNotBeSameAs(store.For(UserId.New()));
    }

    [Fact]
    public void A_caller_with_no_identity_shares_a_copy_with_nobody()
    {
        var store = Store(out _);
        var nobody = AlvoContext.Anonymous.User;

        store.For(nobody).ShouldNotBeSameAs(
            store.For(nobody),
            "the reserved all-zero id is what every refused caller carries, so keying on it would "
            + "hand two unrelated operators the same unapplied descriptor");
    }

    [Fact]
    public void A_copy_nobody_touches_is_not_kept_for_the_life_of_the_process()
    {
        var store = Store(out var clock);
        var eva = UserId.New();
        var first = store.For(eva);

        clock.Advance(WorkingCopyStore.IdleWindow + TimeSpan.FromMinutes(1));

        store.For(eva).ShouldNotBeSameAs(
            first, "nothing but an apply removes an entry, so without an idle window the store only grows");
    }

    [Fact]
    public void Working_on_a_copy_keeps_it()
    {
        var store = Store(out var clock);
        var eva = UserId.New();
        var first = store.For(eva);

        for (var step = 0; step < 4; step++)
        {
            clock.Advance(WorkingCopyStore.IdleWindow - TimeSpan.FromHours(1));
            store.For(eva).ShouldBeSameAs(
                first, "an editor left open over a night is a working copy, not an abandoned one");
        }
    }

    [Fact]
    public void An_applied_copy_is_forgotten()
    {
        var store = Store(out _);
        var eva = UserId.New();
        var first = store.For(eva);

        store.Forget(eva);

        store.For(eva).ShouldNotBeSameAs(first);
    }

    private static WorkingCopyStore Store(out Clock clock)
    {
        clock = new Clock();
        return new WorkingCopyStore(clock);
    }

    /// <summary>
    /// A clock the test moves.
    /// </summary>
    /// <remarks>
    /// Six lines rather than a dependency on a fake-clock package: the store asks this type one
    /// question, and the idle window is measured in days — so a test that had to wait for real time
    /// is the only alternative, and that one is not a test.
    /// </remarks>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
