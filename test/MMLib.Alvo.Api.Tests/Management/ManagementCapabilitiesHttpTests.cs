namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>GET {m}/projects/{p}/capabilities</c> over the wire — the half the unit facts cannot see.
/// </summary>
/// <remarks>
/// The unit facts hold the projection to the two tables; this holds the <em>serialisation</em> to the same
/// standard. A sentence that survived the projector and was mangled by a JSON encoder is the same defect one
/// layer later, and it is the only layer a client actually reads.
/// </remarks>
public class ManagementCapabilitiesHttpTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    [Fact]
    public async Task The_wire_payload_carries_the_prose_unchanged()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var body = await (await world.SendAsync(
            HttpMethod.Get, $"{ManagedFleet.Routes}/capabilities", _ops)).ReadJsonObjectAsync();
        var automation = body["warned"]!.AsArray()
            .Single(block => block!["block"]!.GetValue<string>() == "automation")!;

        automation["consequence"]!.GetValue<string>().ShouldBe(
            "no rule is ever evaluated, so no declared action runs — which looks exactly like a condition "
            + "that never matched",
            "serialisation must not touch the sentence either — this is the whole point of the endpoint");
    }

    [Fact]
    public async Task The_wire_payload_carries_all_three_lists()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var body = await (await world.SendAsync(
            HttpMethod.Get, $"{ManagedFleet.Routes}/capabilities", _ops)).ReadJsonObjectAsync();

        body["honoured"]!.AsArray().Select(name => name!.GetValue<string>()).ShouldContain("entities");
        body["warned"]!.AsArray().ShouldNotBeEmpty();
        body["refused"]!.AsArray().ShouldNotBeEmpty();
    }

    /// <summary>
    /// A refused feature reaches the wire with both halves a client needs to render it.
    /// </summary>
    /// <remarks>
    /// <b>The fix is what separates the two classes on screen.</b> A warned block is an absence; a refused
    /// feature is a control that must not exist, and the fix is the only thing a dashboard can offer in its
    /// place.
    /// </remarks>
    [Fact]
    public async Task A_refused_feature_arrives_with_its_consequence_and_its_fix()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var body = await (await world.SendAsync(
            HttpMethod.Get, $"{ManagedFleet.Routes}/capabilities", _ops)).ReadJsonObjectAsync();
        var refusal = body["refused"]!.AsArray()
            .Single(feature => feature!["slot"]!.GetValue<string>() == "field.default")!;

        refusal["consequence"]!.GetValue<string>().ShouldStartWith("Field 'default' is honoured as a literal");
        refusal["fix"]!.GetValue<string>().ShouldStartWith("Declare a literal default");
    }
}
