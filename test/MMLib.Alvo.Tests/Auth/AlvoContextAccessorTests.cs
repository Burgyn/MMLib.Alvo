using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using System.Collections.Frozen;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// The ambient caller: it must reach the code a request flows into, and it must be genuinely gone
/// afterwards. The second half is why the accessor holds its value behind a mutable box instead of in
/// the <see cref="AsyncLocal{T}"/> directly, and it is invisible from an HTTP test — a leaked caller
/// only shows up on the *next* flow that captured the same execution context.
/// </summary>
public class AlvoContextAccessorTests
{
    [Fact]
    public void AddAlvo_resolves_the_ambient_caller_accessor()
    {
        var services = new ServiceCollection();
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAlvoContextAccessor>().ShouldNotBeNull();
    }

    /// <summary>
    /// Outside a request it reads <see langword="null"/>, and that means "no caller was published" — never
    /// "anonymous". The post-commit paths (the outbox dispatcher, after-hooks, automation actions) run
    /// exactly here, which is why they pass an explicit <see cref="AlvoContext"/> to <c>IAlvoData</c>
    /// rather than consulting this, and why the accessor's own documentation says so.
    /// </summary>
    [Fact]
    public void A_freshly_resolved_accessor_reads_null_outside_a_request()
        => Accessor().Principal.ShouldBeNull();

    /// <summary>
    /// Last writer wins, and the outer caller is not restored — the documented behaviour, asserted so the
    /// documentation cannot drift from it. A push/pop scope is deliberately not built: nothing nests today,
    /// and an unreachable control is the defect class this PR keeps closing.
    /// </summary>
    [Fact]
    public void Publishing_over_a_published_caller_replaces_it_and_does_not_restore_the_outer_one()
    {
        var accessor = Accessor();
        var outer = Principal();
        var inner = Principal();

        accessor.Principal = outer;
        accessor.Principal = inner;
        accessor.Principal.ShouldBe(inner);

        accessor.Principal = null;
        accessor.Principal.ShouldBeNull("nesting is not supported, so the outer caller does not come back");
    }

    [Fact]
    public async Task A_published_caller_is_visible_to_the_code_the_flow_continues_into()
    {
        var accessor = Accessor();
        var principal = Principal();

        accessor.Principal = principal;
        await Task.Yield();

        accessor.Principal.ShouldBe(principal);
    }

    /// <summary>
    /// The reason for the box. A flow that captured the execution context <em>before</em> the clear must
    /// not still see the caller: assigning <see langword="null"/> to an
    /// <see cref="AsyncLocal{T}"/> only clears it for the current context, so without the indirection
    /// this captured continuation would resume holding the previous request's identity.
    /// </summary>
    [Fact]
    public async Task Clearing_the_caller_clears_it_for_a_flow_that_captured_it_first()
    {
        var accessor = Accessor();
        var gate = new TaskCompletionSource();
        accessor.Principal = Principal();

        var captured = Task.Run(async () =>
        {
            await gate.Task;
            return accessor.Principal;
        });

        accessor.Principal = null;
        gate.SetResult();

        (await captured).ShouldBeNull();
        accessor.Principal.ShouldBeNull();
    }

    private static IAlvoContextAccessor Accessor()
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        return services.BuildServiceProvider().GetRequiredService<IAlvoContextAccessor>();
    }

    private static AlvoPrincipal Principal() => new()
    {
        Context = AlvoContext.Anonymous,
        Scopes = FrozenSet<ApiKeyScope>.Empty,
        KeyId = "key",
    };
}
