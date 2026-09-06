using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
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
    /// <b>The failure is a substituted store, and it did not used to be.</b> This drove a duplicate
    /// <c>code</c> on an entity declaring it <c>unique</c> — which was family 5 only because a database
    /// constraint was not mapped onto <c>IAlvoData</c>'s families at all. #138 made that a <c>409</c>, which
    /// is what it always was, so the last family-5 failure a real store could be driven into is gone and the
    /// probe has to substitute one. The sibling fact below now owns the real-store half, and it asserts the
    /// stronger thing: that this composition does <em>not</em> page an operator for it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_real_500_is_logged_with_its_stack_trace_and_rendered_as_alvos_own_type()
    {
        await using var world = await AlvoHostWorld.StartAsync(
            configure: builder => builder.Services.AddSingleton<IAlvoData>(new FaultingAlvoData()));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/warehouses", body: null);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Internal);
        (await response.ReadTextAsync()).ShouldNotContain(
            FaultingAlvoData.FailureMessage, Case.Sensitive, "a 500 reflects nothing about the failure");

        world.Logs.Entries.ShouldContain(
            entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error && entry.Exception != null,
            "#119 is 'logs and renders' — a document that cost the stack trace is the worse defect");
    }

    /// <summary>
    /// #138 over the real standalone pipeline: a duplicate on a <c>unique</c> field is the caller's request
    /// conflicting with stored state, so it is a <c>409</c> naming the field — and nobody is paged for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>no-Error-log</b> half is the one this fact owns alone, and it is a third of the defect rather
    /// than a nicety: a 500 for an ordinary caller mistake wakes whoever operates the instance, with a stack
    /// trace, for a request that was never going to succeed. It is asserted here because the composition —
    /// <c>AddAlvoProblemDetails()</c> plus <c>UseExceptionHandler()</c>, which the standalone host registers
    /// and an embedded one may not — is what decides whether the exception reaches the handler that logs.
    /// </para>
    /// <para>
    /// The value itself must still not come back: the field name is schema-owned and safe to name, the value
    /// is caller-supplied text and is not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_duplicate_on_a_unique_field_is_a_409_naming_the_field_and_pages_nobody()
    {
        await using var world = await AlvoHostWorld.StartAsync();
        var warehouse = new JsonObject { ["code"] = "DUPLICATE", ["city"] = "Košice" };
        using var first = await world.SendAsync(HttpMethod.Post, "/api/warehouses", warehouse);
        first.StatusCode.ShouldBe(
            HttpStatusCode.Created, "the first row must really be stored, or the second one violates nothing");

        using var response = await world.SendAsync(HttpMethod.Post, "/api/warehouses", warehouse);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        (await response.ReadViolationsAsync()).ShouldBe([("/code", "unique")]);
        (await response.ReadTextAsync()).ShouldNotContain(
            "DUPLICATE", Case.Sensitive, "the caller's own value is not echoed back by a refusal");

        world.Logs.Entries.ShouldNotContain(
            entry => Paged(entry),
            "a caller's ordinary mistake must not page whoever operates the instance. Errors logged: "
                + Describe(world.Logs.Entries));
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
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.MalformedQuery);
    }

    /// <summary>
    /// One Error line that says a <em>request</em> woke somebody — the exception handler's own, and nothing
    /// else in the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"No Error anywhere" is a different claim, and asserting it made this fact fail on other people's
    /// work.</b> A world is a whole standalone host, so it runs the outbox dispatcher, which polls the database
    /// once a second for as long as the fact lives. That pump is <em>designed</em> to log exactly one Error and
    /// stop when its queue cannot be reached — <c>A_queue_that_cannot_be_reached_stops_the_pump_loudly</c> is
    /// the fact that pins it down — so a transient store failure on a loaded CI runner put an Error in these
    /// logs and failed a fact about request handling. It did, on windows-latest, on a commit that changed no
    /// code.
    /// </para>
    /// <para>
    /// The narrowing costs nothing this fact was ever measuring: a 409 that regressed to a 500 reaches
    /// <c>AlvoExceptionHandler</c>, whose line this matches, and the sibling fact above asserts the same line
    /// from the other side.
    /// </para>
    /// </remarks>
    /// <param name="entry">One captured record.</param>
    private static bool Paged(LoggedRecord entry) =>
        entry.Level == Microsoft.Extensions.Logging.LogLevel.Error
        && entry.Message.Contains("failed to handle", StringComparison.Ordinal);

    /// <summary>Every Error the host logged, so a failure names what tripped it instead of only its predicate.</summary>
    /// <param name="entries">The world's captured records.</param>
    private static string Describe(IReadOnlyList<LoggedRecord> entries) =>
        string.Join(
            " | ",
            entries
                .Where(entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error)
                .Select(entry => $"{entry.Message} [{entry.Exception?.GetType().Name ?? "no exception"}]"));
}
