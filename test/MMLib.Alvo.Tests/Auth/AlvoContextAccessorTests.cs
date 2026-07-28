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
