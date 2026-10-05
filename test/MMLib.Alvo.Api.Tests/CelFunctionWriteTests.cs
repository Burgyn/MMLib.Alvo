using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>A host function inside a before-hook: it shapes the row, and when it fails nothing is written.</summary>
public sealed class CelFunctionWriteTests
{
    private static JsonObject Contact(string? phone) => phone is null ? new JsonObject() : new JsonObject { ["phone"] = phone };

    [Fact]
    public async Task A_host_function_in_a_before_hook_mutate_shapes_the_stored_row()
    {
        await using var world = await CelFunctionsWorld.StartAsync(phone => string.Concat(phone.Where(c => char.IsAsciiDigit(c) || c == '+')));

        using var created = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact("+421 900 123 456"));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.ReadJsonObjectAsync())["phone_normalized"]!.GetValue<string>().ShouldBe("+421900123456");
    }

    [Fact]
    public async Task A_throwing_host_function_refuses_the_write_and_stores_nothing()
    {
        await using var world = await CelFunctionsWorld.StartAsync(_ => throw new FormatException("secret-detail-from-the-host"));

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact("x"));

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldContain("https://alvo.dev/errors/function-failed");
        text.ShouldContain("normalizePhone");
        text.ShouldNotContain("secret-detail-from-the-host");
        text.ShouldNotContain(nameof(FormatException));
        using var list = await world.SendAsync(HttpMethod.Get, "/api/contacts", CelFunctionsWorld.Writer);
        (await list.ReadJsonObjectAsync())["items"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_function_that_answers_null_stores_null()
    {
        await using var world = await CelFunctionsWorld.StartAsync(_ => null);

        using var created = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact("x"));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.ReadJsonObjectAsync())["phone_normalized"].ShouldBeNull();
    }

    [Fact]
    public async Task A_missing_phone_is_never_passed_to_a_function_that_takes_no_null()
    {
        var calls = 0;
        await using var world = await CelFunctionsWorld.StartAsync(phone => { Interlocked.Increment(ref calls); return phone; });

        using var created = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact(null));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        calls.ShouldBe(0);
    }
}
