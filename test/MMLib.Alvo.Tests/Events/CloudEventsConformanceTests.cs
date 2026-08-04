using CloudNative.CloudEvents;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The CloudEvents conformance oracle. <c>CloudNative.CloudEvents</c> is a <b>test-only</b> dependency
/// (plan decision D3): <c>Abstractions</c> may take no new external dependency, and nothing in the core
/// needs the SDK at run time, because Alvo serializes its own envelope for the outbox row and for webhook
/// delivery.
/// </summary>
/// <remarks>
/// What this proves and what it does not. It proves the attribute <b>names</b> and the wire
/// <b>specversion</b> against the SDK's own validation rather than against a reading of the spec, and it
/// proves that the SDK really rejects the three names the base design proposed — without that control, a
/// green run would only show the SDK is callable. It does <em>not</em> round-trip Alvo's JSON through the
/// SDK's formatter, because Alvo's JSON is not produced by the SDK; structural conformance is pinned by
/// <see cref="One_real_envelope_is_pinned_verbatim"/> and by
/// <c>MMLib.Alvo.Abstractions.Tests.Events.AlvoEventJsonTests</c>.
/// </remarks>
public class CloudEventsConformanceTests
{
    [Fact]
    public void Every_extension_name_is_one_the_cloudevents_sdk_itself_accepts()
    {
        foreach (var name in AlvoEventAttributes.Extensions)
        {
            Should.NotThrow(
                () => CloudEventAttribute.CreateExtension(name, CloudEventAttributeType.String),
                $"'{name}' must match [a-z0-9]+ (spec v1.0.2:173-175)");
        }
    }

    /// <summary>
    /// The oracle's own non-vacuity control: it must reject the three names the base design proposed, or a
    /// green run above would prove only that the SDK was called.
    /// </summary>
    [Theory]
    [InlineData("payload_version")]
    [InlineData("chain-depth")]
    [InlineData("old_record")]
    public void The_oracle_really_rejects_the_names_the_base_design_proposed(string illegal)
        => Should.Throw<Exception>(
            () => CloudEventAttribute.CreateExtension(illegal, CloudEventAttributeType.String));

    [Fact]
    public void Every_extension_name_stays_within_the_specs_twenty_character_advisory()
        => AlvoEventAttributes.Extensions.ShouldAllBe(name => name.Length <= 20);

    /// <summary>
    /// The standard attribute names are the SDK's, not this envelope's spelling of them — which is what
    /// catches <c>datacontentype</c> and every other near-miss a hand-written writer can ship.
    /// </summary>
    [Fact]
    public void Every_standard_attribute_name_is_one_the_sdk_knows_for_v1_0()
    {
        var known = CloudEventsSpecVersion.V1_0.AllAttributes.Select(attribute => attribute.Name).ToList();

        known.ShouldContain(
            "datacontenttype", "the oracle must really carry the standard names, or it proves nothing");
        foreach (var name in AlvoEventAttributes.Standard.Where(name => name != AlvoEventAttributes.SpecVersion))
        {
            known.ShouldContain(name);
        }
    }

    /// <summary>
    /// The wire value is <c>"1.0"</c>, taken from the SDK rather than from a reading of the spec — the
    /// version this design targets is v1.0.2 and writing that string would be the obvious mistake.
    /// </summary>
    [Fact]
    public void The_wire_spec_version_is_the_one_the_sdk_calls_v1_0()
        => AlvoEvent.SpecVersion.ShouldBe(CloudEventsSpecVersion.V1_0.VersionId);

    /// <summary>
    /// No extension name may collide with a standard attribute: the SDK refuses such an extension outright,
    /// and a colliding name would silently overwrite a context attribute in the flat JSON form.
    /// </summary>
    [Fact]
    public void No_extension_name_collides_with_a_standard_attribute()
        => AlvoEventAttributes.Extensions.ShouldNotContain(name => AlvoEventAttributes.Standard.Contains(name));

    [Fact]
    public Task One_real_envelope_is_pinned_verbatim() => Verify(AlvoEventJson.Write(SampleEnvelope()));

    private static AlvoEvent SampleEnvelope() => new()
    {
        Id = Guid.Parse("019fc77e-be7b-72e8-b7fd-ffd6f6306e3e"),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.vehicles.updated",
        Time = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero),
        Subject = "vehicles/3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        PartitionKey = "vehicles:3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        AuthType = AlvoEventAuthType.ApiKey,
        AuthId = "key-42",
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData
        {
            Record = new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
                ["make"] = "vw",
                ["status"] = "approved",
                ["price"] = 19.99m,
            }),
            OldRecord = new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
                ["make"] = "vw",
                ["status"] = "draft",
                ["price"] = 19.99m,
            }),
            Changed = ["status"],
        },
    };
}
