using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Hooks;

[AlvoEntity("bookings")]
public sealed class HookBooking
{
    public Guid Id { get; init; }

    public required string Guest { get; init; }

    public long Nights { get; init; }
}

/// <summary>What a hook sees and records through its context (spec 2026-10-14 §4.4.1, D2).</summary>
public sealed class HookContextTests
{
    private static readonly AlvoContext _caller = AlvoContext.System(null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_typed_before_write_reads_its_images_as_the_host_type_and_has_no_old_record_on_a_create()
    {
        var write = new BeforeWrite<HookBooking>(Run(AlvoWriteOperation.Create, old: null, @new: Booking("Ada", 2)));

        (write.Operation, write.Old, write.New.Guest, write.New.Nights).ShouldBe((AlvoWriteOperation.Create, (HookBooking?)null, "Ada", 2L));
    }

    [Fact]
    public void A_typed_set_names_the_mapped_field_and_an_untyped_one_normalises_its_number()
    {
        var run = Run(AlvoWriteOperation.Update, Booking("Ada", 2), Booking("Ada", 3));
        var write = new BeforeWrite<HookBooking>(run);

        write.Set(x => x.Nights, 4L);
        write.Set("guest", "Grace");
        new BeforeWrite<AlvoRecord>(run).Set("nights", 5);

        run.Sets.ShouldBe(new Dictionary<string, object?> { ["nights"] = 5L, ["guest"] = "Grace" });
    }

    [Fact]
    public void A_typed_set_of_a_field_alvo_manages_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => new BeforeWrite<HookBooking>(Run(AlvoWriteOperation.Update, Booking("Ada", 2), Booking("Ada", 2))).Set(x => x.Id, Guid.NewGuid()));

    [Fact]
    public async Task Publish_goes_through_the_hook_s_transaction()
    {
        var transaction = new FakeHookTransaction(_caller);
        var write = new AfterWrite<AlvoRecord>(Run(AlvoWriteOperation.Create, null, null, saved: Booking("Ada", 2), transaction: transaction));

        await write.PublishAsync("bookings.audited", "bookings/1", null, Ct);

        transaction.Calls.ShouldBe(["publish bookings.audited"]);
    }

    [Fact]
    public void A_delete_context_always_has_the_row()
    {
        var write = new BeforeDelete<HookBooking>(Run(AlvoWriteOperation.Delete, Booking("Ada", 2), null));

        write.Old.Guest.ShouldBe("Ada");
    }

    [Fact]
    public void Each_kind_is_recorded_by_its_own_method()
    {
        var run = Run(AlvoWriteOperation.Create, null, Booking("Ada", 2));
        var write = new ValidateWrite<AlvoRecord>(run);

        write.Invalid("bad", "Bad.", "guest");
        write.Conflict("taken", "Taken.");
        write.Forbidden("no", "No.");

        run.Rejections.Winner()!.Kind.ShouldBe(AlvoWriteRejectionKind.Forbidden);
    }

    internal static AlvoRecord Booking(string guest, long nights) => new(new Dictionary<string, object?>
    {
        ["id"] = Guid.Parse("00000000-0000-0000-0000-0000000000b1"),
        ["guest"] = guest,
        ["nights"] = nights,
    });

    internal static HookRun Run(
        AlvoWriteOperation operation, AlvoRecord? old, AlvoRecord? @new, AlvoRecord? saved = null, IAlvoTransaction? transaction = null) =>
        new(
            "guard",
            new WriteInvocation { Entity = "bookings", Operation = operation, Caller = _caller, Now = DateTimeOffset.UnixEpoch, Old = old, New = @new, Saved = saved },
            new HashSet<string>(StringComparer.Ordinal) { "guest", "nights" },
            transaction ?? new FakeHookTransaction(_caller),
            data: null,
            CancellationToken.None);
}
