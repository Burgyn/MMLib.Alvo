using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>HX-12 over HTTP on every engine: an overlapping range is a 409 at the range's start, never a 500.</summary>
public abstract partial class DataApiEngineTests
{
    private static readonly DateTimeOffset _slot = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Acceptance (3) over HTTP: ten concurrent, pairwise-overlapping bookings of one room answer one 201 and nine 409s,
    /// each of type conflict with one violation <c>{ "/starts_at", "exclusion" }</c>.
    /// </summary>
    [Fact]
    public async Task Overlapping_bookings_are_409_at_the_range_pointer_under_concurrent_writers_on_every_engine()
    {
        await using var world = await StartExclusionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var posts = Enumerable.Range(0, 10).Select(async minute =>
        {
            await start.Task;
            using var response = await world.SendAsync(
                HttpMethod.Post, "/api/bookings", _clerk, body: Booking(_slot.AddMinutes(minute), _slot.AddMinutes(60 + minute)));
            return (response.StatusCode, Type: await ProblemTypeOrNullAsync(response), Violations: await ViolationsOrEmptyAsync(response));
        }).ToList();
        start.SetResult();
        var outcomes = await Task.WhenAll(posts);

        outcomes.Count(outcome => outcome.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        foreach (var refused in outcomes.Where(outcome => outcome.StatusCode != HttpStatusCode.Created))
        {
            (refused.StatusCode, refused.Type).ShouldBe((HttpStatusCode.Conflict, AlvoProblemTypes.Conflict));
            refused.Violations.ShouldBe([("/starts_at", "exclusion")]);
        }

        (await world.CountRowsAsync("bookings")).ShouldBe(1);
    }

    /// <summary>Review Focus 2 over HTTP: an inverted booking is the end field's validation — 422 at <c>/ends_at</c>.</summary>
    [Fact]
    public async Task An_inverted_booking_is_422_at_its_end_on_every_engine()
    {
        await using var world = await StartExclusionAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/bookings", _clerk, body: Booking(_slot.AddHours(1), _slot));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await refused.ReadViolationsAsync()).ShouldBe([("/ends_at", "check")]);
    }

    private static JsonObject Booking(DateTimeOffset from, DateTimeOffset to) => new()
    {
        ["room"] = "r1",
        ["starts_at"] = from.ToString("O", CultureInfo.InvariantCulture),
        ["ends_at"] = to.ToString("O", CultureInfo.InvariantCulture),
        ["status"] = "held",
    };

    private static async Task<string?> ProblemTypeOrNullAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode ? null : await response.ReadProblemTypeAsync();

    private static async Task<IReadOnlyList<(string Pointer, string Code)>> ViolationsOrEmptyAsync(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Conflict ? await response.ReadViolationsAsync() : [];

    private Task<AlvoApiWorld> StartExclusionAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "exclusion-bookings.alvo.json",
            [_clerk],
            new AlvoApiWorldSetup(MapOpenApiDocument: true, MapAlvoProblemDetails: true),
            Engine);
}
