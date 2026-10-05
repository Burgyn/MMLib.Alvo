using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Expressions.Internal;
using System.Net;
using System.Reflection;
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

    /// <summary>A world whose function passes until <c>failing</c> is switched on, so a row can exist first.</summary>
    private static async Task<(AlvoApiWorld World, Func<bool> Arm)> StartArmableAsync()
    {
        var failing = false;
        var world = await CelFunctionsWorld.StartAsync(phone => failing ? throw new FormatException("secret-detail-from-the-host") : phone);
        return (world, () => failing = true);
    }

    private static async Task<Guid> CreateAsync(AlvoApiWorld world, string phone)
    {
        using var created = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact(phone));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static async Task ShouldBeFunctionFailedAsync(HttpResponseMessage refused)
    {
        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldContain("https://alvo.dev/errors/function-failed");
        text.ShouldNotContain("secret-detail-from-the-host");
    }

    private static async Task<string?> PhoneOfAsync(AlvoApiWorld world, Guid id)
    {
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/contacts/{id}", CelFunctionsWorld.Writer);
        return read.StatusCode == HttpStatusCode.OK ? (await read.ReadJsonObjectAsync())["phone"]?.GetValue<string>() : null;
    }

    [Fact]
    public async Task A_failing_function_in_an_update_reject_condition_refuses_the_update_and_keeps_the_row()
    {
        var (world, arm) = await StartArmableAsync();
        await using var _ = world;
        var id = await CreateAsync(world, "111");
        arm();

        using var refused = await world.SendAsync(
            HttpMethod.Patch, $"/api/contacts/{id}", CelFunctionsWorld.Writer, body: Contact("222"));

        await ShouldBeFunctionFailedAsync(refused);
        (await PhoneOfAsync(world, id)).ShouldBe("111");
    }

    [Fact]
    public async Task A_failing_function_in_a_delete_reject_condition_refuses_the_delete_and_keeps_the_row()
    {
        var (world, arm) = await StartArmableAsync();
        await using var _ = world;
        var id = await CreateAsync(world, "111");
        arm();

        using var refused = await world.SendAsync(HttpMethod.Delete, $"/api/contacts/{id}", CelFunctionsWorld.Writer);

        await ShouldBeFunctionFailedAsync(refused);
        (await PhoneOfAsync(world, id)).ShouldBe("111");
    }

    [Fact]
    public async Task A_batch_with_one_failing_row_writes_none_of_its_rows()
    {
        await using var world = await CelFunctionsWorld.StartAsync(
            phone => phone == "bad" ? throw new FormatException("secret-detail-from-the-host") : phone);

        using var refused = await world.SendAsync(
            HttpMethod.Post,
            "/api/contacts/batch",
            CelFunctionsWorld.Writer,
            body: new JsonObject { ["rows"] = new JsonArray(Contact("good"), Contact("bad")) });

        await ShouldBeFunctionFailedAsync(refused);
        (await world.CountRowsAsync("contacts")).ShouldBe(0);
    }

    /// <summary>
    /// The outbox assertion can see a row (a committed create leaves exactly one, via the descriptor's after-create
    /// webhook), so a refused write that leaves the count where it was is a measurement and not a vacuous zero.
    /// </summary>
    [Fact]
    public async Task A_refused_write_leaves_no_row_and_no_outbox_entry()
    {
        var (world, arm) = await StartArmableAsync();
        await using var _ = world;
        await CreateAsync(world, "111");
        (await world.CountRowsAsync("alvo_outbox")).ShouldBe(1, "a committed create must leave one outbox row");
        arm();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact("x"));

        await ShouldBeFunctionFailedAsync(refused);
        (await world.CountRowsAsync("contacts")).ShouldBe(1);
        (await world.CountRowsAsync("alvo_outbox")).ShouldBe(1);
    }

    [Fact]
    public async Task A_reject_gate_still_refuses_an_integer_within_its_parameter()
    {
        await using var world = await CelFunctionsWorld.StartGateAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/orders", CelFunctionsWorld.OrdersWriter, body: new JsonObject { ["qty"] = 500 });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await world.CountRowsAsync("orders")).ShouldBe(0);
    }

    /// <summary>
    /// The fail-open the final review reproduced: an Int past <see cref="int"/> used to marshal to null, the call to
    /// null, the condition to false — and the reject never fired, so the row was stored.
    /// </summary>
    [Fact]
    public async Task An_integer_that_does_not_fit_the_gate_function_fails_the_write_instead_of_passing_the_reject()
    {
        await using var world = await CelFunctionsWorld.StartGateAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/orders", CelFunctionsWorld.OrdersWriter, body: new JsonObject { ["qty"] = 3_000_000_000L });

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldContain("https://alvo.dev/errors/function-failed");
        text.ShouldContain("tooMany");
        text.ShouldContain("parameter 'n' (Int32)");
        text.ShouldNotContain("3000000000");
        (await world.CountRowsAsync("orders")).ShouldBe(0);
    }

    [Fact]
    public void A_function_failure_is_found_inside_a_wrapper_of_any_kind()
    {
        var failure = new CelFunctionException("f", isHost: true, new FormatException("host"));
        var other = new InvalidOperationException("other");

        FunctionFailure(failure).ShouldBeSameAs(failure);
        FunctionFailure(new TargetInvocationException(failure)).ShouldBeSameAs(failure);
        FunctionFailure(new AggregateException(failure)).ShouldBeSameAs(failure);
        FunctionFailure(new AggregateException(other, failure)).ShouldBeSameAs(failure);
        FunctionFailure(new AggregateException(new AggregateException(other, failure))).ShouldBeSameAs(failure);
        FunctionFailure(new InvalidOperationException("o", new InvalidOperationException("i", failure))).ShouldBeSameAs(failure);
        FunctionFailure(new AggregateException(other, new InvalidOperationException("x"))).ShouldBeNull();
    }

    private static CelFunctionException? FunctionFailure(Exception exception) => AlvoExceptionHandler.FunctionFailure(exception);
}
