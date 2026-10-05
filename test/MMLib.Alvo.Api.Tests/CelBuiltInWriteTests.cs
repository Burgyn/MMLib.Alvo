using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>The built-ins of spec §5, evaluated by the Data API's own write path.</summary>
public sealed class CelBuiltInWriteTests
{
    private static TestApiKey Writer { get; } = new("frames-writer", ["writer"], ["frames:read", "frames:write"]);

    private static Task<AlvoApiWorld> StartAsync() =>
        AlvoApiWorld.FromDescriptorAsync("built-in-functions.alvo.json", [Writer], new AlvoApiWorldSetup(MapAlvoProblemDetails: true));

    [Fact]
    public async Task A_mutate_normalises_and_cuts_the_written_text()
    {
        await using var world = await StartAsync();

        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["code"] = "  wtu 123 456 789x  ", ["owner_email"] = "jana@kros.sk" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var row = await created.ReadJsonObjectAsync();
        row["code"]!.GetValue<string>().ShouldBe("WTU123456789X");
        row["label"]!.GetValue<string>().ShouldBe("WTU123456789", "cut to the label's 12 characters");
    }

    [Fact]
    public async Task A_mutate_rounds_money_to_cents_and_prefixes_a_code()
    {
        await using var world = await StartAsync();

        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["code"] = " wtu 1 ", ["price"] = 10.25m });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var row = await created.ReadJsonObjectAsync();
        row["gross"]!.GetValue<decimal>().ShouldBe(12.30m, "10.25 * 1.2 = 12.300, rounded to cents");
        row["tag"]!.GetValue<string>().ShouldBe("FR-WTU 1");
    }

    [Fact]
    public async Task A_null_operand_leaves_the_value_null_and_the_condition_quiet()
    {
        await using var world = await StartAsync();

        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer, body: new JsonObject { ["ratio"] = 50 });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, "50 / null is null, and a condition reading null does not fire");
        var row = await created.ReadJsonObjectAsync();
        row["gross"].ShouldBeNull("null price, null gross");
        row["tag"].ShouldBeNull("null code, null tag");
    }

    /// <summary>
    /// The HTTP binder hands the interpreter an integer field as a whole number, so <c>/</c> truncates (pre-flight
    /// F9-1): 21 / 2 is 10, not 10.5, and the <c>&gt; 10</c> reject stays quiet.
    /// </summary>
    [Fact]
    public async Task An_integer_division_over_http_truncates_toward_zero()
    {
        await using var world = await StartAsync();

        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["ratio"] = 21, ["divisor"] = 2 });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, "21 / 2 is 10 between integers, which is not over 10");

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["ratio"] = 22, ["divisor"] = 2 });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "22 / 2 is 11, over 10");
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("The ratio is too high.");
    }

    /// <summary>The fail-open spec D-7 closes: a zero divisor in a reject condition refuses the write, never skips the reject.</summary>
    [Fact]
    public async Task A_division_by_zero_in_a_condition_fails_the_write_closed()
    {
        await using var world = await StartAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["code"] = "DIV0", ["ratio"] = 50, ["divisor"] = 0 });

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldEndWith("/errors/function-failed");
        problem["detail"]!.GetValue<string>().ShouldBe("The CEL function '_/_' failed: the divisor is zero. Nothing was written.");

        using var listed = await world.SendAsync(HttpMethod.Get, "/api/frames", Writer);
        (await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("DIV0", Case.Sensitive, "nothing was written");
    }

    [Fact]
    public async Task A_condition_with_a_text_test_refuses_the_write()
    {
        await using var world = await StartAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["code"] = "A1", ["owner_email"] = "Someone@EXAMPLE.com" });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Use the owner's real email address.");
    }

    [Fact]
    public async Task A_substring_past_the_end_of_the_data_fails_the_write_closed()
    {
        await using var world = await StartAsync();
        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer, body: new JsonObject { ["code"] = "A1" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.ReadJsonObjectAsync())["id"]!.ToString();

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/frames/{id}", Writer, body: new JsonObject { ["code"] = "B2" });

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldEndWith("/errors/function-failed");
        problem["detail"]!.GetValue<string>().ShouldContain("substring");
        problem["detail"]!.GetValue<string>().ShouldNotContain("B2", Case.Sensitive, "never the caller's data");
    }
}
