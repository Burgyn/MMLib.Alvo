using MMLib.Alvo.Events;

using static MMLib.Alvo.Abstractions.Tests.Events.SampleEvents;

namespace MMLib.Alvo.Abstractions.Tests.Events;

/// <summary>
/// The envelope's own guards. Everything about how it is spelled on the wire belongs to
/// <see cref="AlvoEventJsonTests"/>; this class is only about what the type refuses to be constructed as.
/// </summary>
public class AlvoEventTests
{
    [Fact]
    public void An_event_refuses_a_time_that_is_not_utc()
    {
        var refusal = Should.Throw<ArgumentException>(() => Sample() with
        {
            Time = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.FromHours(-2)),
        });

        refusal.Message.ShouldContain("UTC");
    }

    /// <summary>
    /// <c>StoredInstant</c> is internal to the EF driver, so this is where the same rule is enforceable at
    /// the envelope's own boundary. Without it an offset would reach the wire, and two engines would order
    /// the same instant differently (<c>docs/architecture/data-path.md</c>, <em>Every timestamp is one
    /// instant</em>).
    /// </summary>
    [Fact]
    public void An_event_accepts_a_utc_time()
        => (Sample() with { Time = DateTimeOffset.UtcNow }).Time.Offset.ShouldBe(TimeSpan.Zero);

    [Fact]
    public void The_payload_version_defaults_to_the_current_one_so_no_producer_can_forget_it()
        => Sample().PayloadVersion.ShouldBe(AlvoEvent.CurrentPayloadVersion);

    /// <summary>
    /// The chain is empty in PR5a because nothing yet runs a data action <em>because of</em> an event, and
    /// an absent cause must read as absent rather than as a self-reference.
    /// </summary>
    [Fact]
    public void A_causation_id_is_absent_by_default_rather_than_the_events_own_id()
    {
        var subject = Sample() with { CausationId = null };

        subject.CausationId.ShouldBeNull();
        subject.ChainDepth.ShouldBe(0);
    }

    /// <summary>
    /// Record equality is what the round-trip fact rests on, and a list member would otherwise compare by
    /// reference — so two envelopes carrying the same changed columns would be unequal.
    /// </summary>
    [Fact]
    public void Two_envelopes_carrying_the_same_values_are_equal_including_the_changed_list()
    {
        var one = Sample();
        var other = Sample() with { Data = Sample().Data with { Changed = ["status"] } };

        other.ShouldBe(one);
        other.GetHashCode().ShouldBe(one.GetHashCode());
    }

    [Fact]
    public void Two_envelopes_differing_only_in_the_changed_list_are_not_equal()
        => (Sample() with { Data = Sample().Data with { Changed = ["make"] } }).ShouldNotBe(Sample());
}
