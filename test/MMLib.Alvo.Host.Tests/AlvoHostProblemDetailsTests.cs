using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// #119 in the pipeline it was filed about. The core's own suite proves the handler renders Alvo's
/// <c>type</c>; this proves the <em>standalone host</em> registered it — which is the half a fact over an
/// embedded fixture cannot see, and the reason #119 said the product could be wrong while the fact stayed
/// green.
/// </summary>
public class AlvoHostProblemDetailsTests
{
    [Fact]
    public async Task The_host_registers_alvos_exception_handler()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        var handlers = world.ExceptionHandlerTypeNames();

        handlers.ShouldContain(
            "AlvoExceptionHandler",
            "without it a 500 from this host carries an RFC 9110 status-code URI in `type` (#119)");
    }

    /// <summary>
    /// #119's whole claim, over the real standalone pipeline and a failure nobody arranged: the response
    /// carries Alvo's <c>type</c> and nothing about the exception, <b>and</b> the exception itself reached the
    /// host's logging with its stack trace intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>log</b> half is asserted here and nowhere else, and it is the half whose loss would be worse
    /// than the defect #119 fixes: a handler that renders a tidy document and forgets to log turns an
    /// operator's only record of a broken invariant into a constant sentence. It fails the moment the
    /// handler's <c>Failed(...)</c> call is dropped.
    /// </para>
    /// <para>
    /// The failure is a duplicate <c>code</c> on an entity whose descriptor declares it <c>unique</c> —
    /// <c>data-api.md</c>'s "one 500 <em>is</em> caller-reachable", and the only family-5 failure a real
    /// store can be driven into without substituting one. The core's suite uses a faulting
    /// <c>IAlvoData</c> instead, which is why both facts exist: that one measures the writer, this one
    /// measures the composition.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_real_500_is_logged_with_its_stack_trace_and_rendered_as_alvos_own_type()
    {
        await using var world = await AlvoHostWorld.StartAsync();
        var warehouse = new JsonObject { ["code"] = "DUPLICATE", ["city"] = "Košice" };
        using var first = await world.SendAsync(HttpMethod.Post, "/api/warehouses", warehouse);
        first.StatusCode.ShouldBe(
            HttpStatusCode.Created, "the first row must really be stored, or the second one violates nothing");

        using var response = await world.SendAsync(HttpMethod.Post, "/api/warehouses", warehouse);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        JsonNode.Parse(body)!["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/internal");
        body.ShouldNotContain("DUPLICATE", Case.Sensitive, "the caller's own value is not echoed back by a 500");

        world.Logs.Entries.ShouldContain(
            entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error && entry.Exception != null,
            "#119 is 'logs and renders' — a document that cost the stack trace is the worse defect");
    }

    /// <summary>
    /// The guard that answering 500s did not start rewriting the refusals that already had a body. Registering
    /// <c>AddProblemDetails()</c> in a host is exactly the change that could, and a 422 answered as
    /// <c>text/plain</c> — or with the framework's status-code <c>type</c> — would fail this.
    /// </summary>
    [Fact]
    public async Task An_ordinary_refusal_is_still_the_data_apis_own_problem_document()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/warehouses?limit=0", body: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        JsonNode.Parse(body)!["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/malformed-query");
    }
}
