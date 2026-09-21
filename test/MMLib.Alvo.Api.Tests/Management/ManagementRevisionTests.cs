using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>GET {m}/projects/{p}/revisions</c> and <c>.../revisions/{n}</c> — the configuration history.
/// </summary>
/// <remarks>
/// <see cref="Migrations.DescriptorVersion"/> has carried <c>Author</c>, <c>Reason</c> and
/// <c>RolledBackFrom</c> since it was written and has had no consumer anywhere in the product. These two
/// routes are the first, and one revision's body is the export of a past state.
/// </remarks>
public class ManagementRevisionTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    [Fact]
    public async Task The_history_is_append_only_and_ordered_oldest_first()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var revisions = await (await world.SendAsync(
            HttpMethod.Get, $"{ManagedFleet.Routes}/revisions", _ops)).ReadJsonArrayAsync();

        revisions.Count.ShouldBe(1, "the boot appended exactly one revision, and history is never mutated");
        revisions[0]["revision"]!.GetValue<int>().ShouldBe(1);
        revisions[0].ContainsKey("rolledBackFrom").ShouldBeTrue(
            "a revision that restored nothing still reports the field, so a reader never has to guess");
        revisions[0]["rolledBackFrom"].ShouldBeNull("this revision is a first apply, not a rollback");
        revisions[0]["createdAt"]!.GetValue<DateTimeOffset>().ShouldBeGreaterThan(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task One_revision_is_the_export_of_a_past_state()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var body = await (await world.SendAsync(
            HttpMethod.Get, $"{ManagedFleet.Routes}/revisions/1", _ops)).ReadJsonObjectAsync();

        body["version"]!["revision"]!.GetValue<int>().ShouldBe(1);
        body["descriptorJson"]!.GetValue<string>().ShouldBe(
            await File.ReadAllTextAsync(ManagedFleet.DescriptorPath, TestContext.Current.CancellationToken),
            "one revision's body is the descriptor that was applied at it, not a rendering of it");
    }

    [Fact]
    public async Task A_revision_that_was_never_appended_is_404()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/revisions/99", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
        (await response.ReadProblemDetailAsync()).ShouldContain("99");
    }

    /// <summary>
    /// A revision number that is not a number is refused at route matching, so no delegate has to validate
    /// it.
    /// </summary>
    /// <remarks>
    /// <b>The empty body is the half that makes this discriminating.</b> A 404 alone would be satisfied by
    /// the service refusing it too — the assertion that nothing matched is that Alvo minted no problem
    /// document at all, which is what the <c>:int</c> constraint buys and a hand-written parse would not.
    /// </remarks>
    [Fact]
    public async Task A_revision_number_that_is_not_a_number_never_reaches_the_service()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/revisions/latest", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadTextAsync()).ShouldBeEmpty(
            "the route constraint refused it at matching, so nothing in Alvo ever looked at it");
    }
}
