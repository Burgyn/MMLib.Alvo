using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A before-hook reject gated on a date field compared with a <c>timestamp()</c> literal, or with a datetime field,
/// fires on every engine (pre-flight S-1, F9-1 and C-3; #317).
/// </summary>
/// <remarks>
/// Run on every engine because the storage differs: a create's <c>new.due</c> comes from the request, while an
/// update's <c>old</c> image is read back out of storage — a <c>date</c> column on PostgreSQL, text on SQLite. Before
/// #317 the interpreter could not compare a <see cref="DateOnly"/> with an instant at all, answered <c>false</c>, and
/// every such reject was silently switched off. A date compares as midnight UTC of its day. These facts cover a date
/// against a <c>timestamp()</c> literal and against a datetime field; the storage path — a date read back out of the
/// engine on update — is <see cref="A_before_hook_reject_over_a_date_refuses_a_patch_that_moves_the_date"/>'s (#321),
/// so it is not proven twice.
/// </remarks>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _shipmentsWriter = new("shipments-writer", ["writer"], ["shipments:read", "shipments:write"]);

    [Theory]
    [InlineData("2025-12-31", HttpStatusCode.Forbidden)]
    [InlineData("2026-01-01", HttpStatusCode.Created)]
    public async Task A_reject_gated_on_a_date_before_a_timestamp_fires_on_every_engine(string due, HttpStatusCode expected)
    {
        await using var world = await StartHookDatesAsync();

        using var answer = await world.SendAsync(
            HttpMethod.Post, "/api/shipments", _shipmentsWriter, body: new JsonObject { ["due"] = due });

        var text = await answer.ReadTextAsync();
        answer.StatusCode.ShouldBe(expected, text);
        if (expected == HttpStatusCode.Forbidden)
        {
            text.ShouldContain("The due date is before 2026.");
            (await world.CountRowsAsync("shipments")).ShouldBe(0, "a rejected create stores nothing");
        }
    }

    [Theory]
    [InlineData("2026-03-10", HttpStatusCode.Forbidden)]
    [InlineData("2026-03-09", HttpStatusCode.Created)]
    public async Task A_reject_gated_on_a_date_after_a_datetime_fires_on_every_engine(string due, HttpStatusCode expected)
    {
        await using var world = await StartHookDatesAsync();

        using var answer = await world.SendAsync(
            HttpMethod.Post, "/api/shipments", _shipmentsWriter,
            body: new JsonObject { ["due"] = due, ["ship_by"] = "2026-03-09T12:00:00Z" });

        var text = await answer.ReadTextAsync();
        answer.StatusCode.ShouldBe(expected, $"midnight of {due} against noon of 2026-03-09: {text}");
        if (expected == HttpStatusCode.Forbidden)
        {
            text.ShouldContain("The due date is after the ship-by time.");
        }
    }

    private Task<AlvoApiWorld> StartHookDatesAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "hook-dates.alvo.json", [_shipmentsWriter], new AlvoApiWorldSetup(MapAlvoProblemDetails: true), Engine);
}
