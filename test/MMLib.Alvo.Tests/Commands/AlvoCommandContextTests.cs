using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using NSubstitute;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// The request's command context (Ruling 5): one per request, unstarted until MapAlvoCommand's filter starts it, and every
/// member refused while it is unstarted — an endpoint no Alvo filter ran on fails closed rather than answering as anyone.
/// </summary>
public sealed class AlvoCommandContextTests
{
    private static readonly AlvoContext _staff = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unstarted_context_refuses_every_member()
    {
        var context = AlvoCommandContext.Pending(new DefaultHttpContext());

        Should.Throw<InvalidOperationException>(() => context.Name).Message.ShouldContain("MapAlvoCommand");
        Should.Throw<InvalidOperationException>(() => context.Caller);
        Should.Throw<InvalidOperationException>(() => context.Data);
        Should.Throw<InvalidOperationException>(() => context.Record);
        await Should.ThrowAsync<InvalidOperationException>(() => context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct));
    }

    [Fact]
    public async Task One_request_binds_one_context_and_another_request_its_own()
    {
        var http = new DefaultHttpContext();

        var bound = await AlvoCommandContext.BindAsync(http, null!);

        AlvoCommandContext.Pending(http).ShouldBeSameAs(bound);
        AlvoCommandContext.Pending(new DefaultHttpContext()).ShouldNotBeSameAs(bound);
    }

    [Fact]
    public void A_context_starts_once()
    {
        var context = AlvoCommandContext.Pending(new DefaultHttpContext());
        context.Start(Run());

        Should.Throw<InvalidOperationException>(() => context.Start(Run())).Message.ShouldContain("already started");
        context.Name.ShouldBe("quote_stay");
    }

    private static CommandRun Run()
    {
        var time = new FakeTimeProvider();
        var command = new CommandRegistration("quote_stay", 1, CommandAccess.Of("true"), null, "/commands/quote", null, null);

        return new CommandRun(new CommandInvocation(command, _staff, null, null), Substitute.For<IAlvoSession>(), null, new CommandCompletion(time));
    }
}
