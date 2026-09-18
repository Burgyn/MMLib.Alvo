using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The filter every management route carries: it runs the gate, and a caller who matches no level
/// never reaches the handler.
/// </summary>
public class ManagementAccessEndpointFilterTests
{
    [Fact]
    public async Task A_caller_who_matches_no_level_is_refused_and_the_handler_never_runs()
    {
        var handlerRan = false;

        var outcome = await InvokeAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("sales"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => { handlerRan = true; return "ok"; });

        handlerRan.ShouldBeFalse("a gate that runs after the handler has already disclosed the answer");
        (await StatusOfAsync(outcome)).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        var outcome = await InvokeAsync(
            ManagementOperation.GetSchema,
            caller: null,
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => "ok");

        (await StatusOfAsync(outcome)).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_caller_at_the_required_level_reaches_the_handler()
    {
        var handlerRan = false;

        var outcome = await InvokeAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("manager"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => { handlerRan = true; return "ok"; });

        handlerRan.ShouldBeTrue();
        outcome.ShouldBe("ok");
    }

    [Fact]
    public async Task A_developer_is_refused_the_settings_surface()
    {
        var outcome = await InvokeAsync(
            ManagementOperation.ManageApiKeys,
            ManagementCallers.Caller("editor"),
            ManagementCallers.Levels(developer: "'editor' in @user.roles"),
            () => "ok");

        (await StatusOfAsync(outcome)).ShouldBe(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// <b>The slug is the published one every policy refusal carries.</b> <c>data-api.md</c> names
    /// <c>forbidden</c> as that slug, and minting a management-only spelling would give an agent two
    /// types to branch on for one class of refusal.
    /// </summary>
    [Fact]
    public async Task The_refusal_carries_the_published_forbidden_slug()
    {
        var outcome = await InvokeAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("sales"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => "ok");

        var document = await DocumentWrittenByAsync((IResult)outcome!);
        document["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.UriOf(AlvoProblemTypes.Forbidden));
    }

    /// <summary>
    /// <b>The refusal names neither the level the caller holds nor the level the operation needs.</b> A
    /// message naming the gap would let a caller map the project's whole access block one request at a
    /// time, which is a fingerprint of the configuration — the reason <c>ScopeRefused</c> withholds the
    /// same thing one subsystem over.
    /// <para>
    /// <b>Equality between two differently-placed callers is the assertion</b>, rather than a list of
    /// forbidden words: a viewer refused the settings surface and a stranger refused a read differ in
    /// their own level, in the level the operation needs, and in the roles they hold — so a message that
    /// disclosed any of the three would make these two bodies differ. The role check beside it is the
    /// direct statement of the same thing, and names only roles, because "admin" is a substring of
    /// ordinary prose and a word list would pin the wording rather than the property.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_refusal_tells_two_differently_placed_callers_exactly_the_same_thing()
    {
        var refusedViewer = await DetailOfAsync(await InvokeAsync(
            ManagementOperation.ManageApiKeys,
            ManagementCallers.Caller("sales"),
            ManagementCallers.Levels(viewer: "'sales' in @user.roles"),
            () => "ok"));

        var refusedStranger = await DetailOfAsync(await InvokeAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("editor"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => "ok"));

        refusedViewer.ShouldBe(refusedStranger);
        foreach (var role in (string[])["manager", "editor", "sales"])
        {
            refusedViewer.ShouldNotContain(role, Case.Insensitive);
        }
    }

    private static async ValueTask<object?> InvokeAsync(
        ManagementOperation operation, AlvoContext? caller, Access levels, Func<object?> next)
    {
        var filter = new ManagementAccessEndpointFilter(
            operation, ManagementCallers.Evaluator(levels), new PublishedCaller(caller));

        var context = EndpointFilterInvocationContext.Create(new DefaultHttpContext());
        return await filter.InvokeAsync(context, _ => ValueTask.FromResult(next()));
    }

    private static async Task<int> StatusOfAsync(object? outcome)
    {
        var context = await ExecutedAsync((IResult)outcome!);
        return context.Response.StatusCode;
    }

    private static async Task<string> DetailOfAsync(object? outcome) =>
        (await DocumentWrittenByAsync((IResult)outcome!))["detail"]!.GetValue<string>();

    private static async Task<JsonObject> DocumentWrittenByAsync(IResult result)
    {
        var context = await ExecutedAsync(result);
        context.Response.Body.Position = 0;

        return (await JsonSerializer.DeserializeAsync<JsonObject>(
            context.Response.Body, cancellationToken: TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Executes the result the way a live endpoint would.
    /// </summary>
    /// <remarks>
    /// Against a container rather than a bare context, and that is the point of executing it at all:
    /// ASP.NET Core's own <c>ProblemHttpResult</c> resolves <c>IProblemDetailsService</c> and the JSON
    /// options from the request's services, so this measures the bytes a caller receives, written by the
    /// same writer every live endpoint uses. The same shape
    /// <c>MMLib.Alvo.Api.Tests.ProblemDetailsTests.SlugWrittenByAsync</c> uses, written locally because
    /// that is a different assembly.
    /// </remarks>
    /// <param name="result">The result to execute.</param>
    private static async Task<DefaultHttpContext> ExecutedAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };

        await result.ExecuteAsync(context);
        return context;
    }

    /// <summary>
    /// The caller this invocation runs as, or none.
    /// </summary>
    /// <remarks>
    /// A stub rather than the framework's <c>AlvoContextAccessor</c>, which holds its value in an
    /// <see cref="AsyncLocal{T}"/> shared by every instance — correct in a request pipeline, and a way for
    /// two facts running in parallel to publish over each other here.
    /// </remarks>
    /// <param name="caller">The caller to publish, or <see langword="null"/> for an unauthenticated request.</param>
    private sealed class PublishedCaller(AlvoContext? caller) : IAlvoContextAccessor
    {
        /// <inheritdoc/>
        public AlvoPrincipal? Principal { get; set; } = caller is null ? null : new AlvoPrincipal
        {
            Context = caller,
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "probe",
        };
    }
}
