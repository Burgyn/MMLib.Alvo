using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>A command key's scope: command, tenant, user and credential (Ruling 34).</summary>
public sealed class CommandKeysTests
{
    private static readonly CommandRegistration _quote = new("quote_stay", 1, CommandAccess.Of("true"), null, "/q", null, null);
    private static readonly AlvoContext _user = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    [Fact]
    public void Two_keys_of_one_user_file_one_idempotency_key_apart() =>
        CommandKeys.ScopeOf(_quote, _user, "key-a").ShouldNotBe(CommandKeys.ScopeOf(_quote, _user, "key-b"));

    [Fact]
    public void One_user_in_two_tenants_files_one_idempotency_key_apart_even_under_one_key_id() =>
        CommandKeys.ScopeOf(_quote, _user with { Tenant = new TenantId(Guid.NewGuid()) }, "key-a")
            .ShouldNotBe(CommandKeys.ScopeOf(_quote, _user with { Tenant = new TenantId(Guid.NewGuid()) }, "key-a"));

    [Fact]
    public void A_caller_with_no_credential_files_under_the_dash() =>
        CommandKeys.ScopeOf(_quote, _user, null).ShouldEndWith("/-");

    [Fact]
    public void A_repeated_header_and_an_anonymous_caller_are_refused_before_any_key_is_made()
    {
        var repeated = new DefaultHttpContext();
        repeated.Request.Headers.Append(CommandKeys.Header, "a");
        repeated.Request.Headers.Append(CommandKeys.Header, "b");
        var anonymous = new DefaultHttpContext();
        anonymous.Request.Headers[CommandKeys.Header] = "a";

        CommandKeys.TryRead(repeated.Request, _quote, _user, "k", "fp", out var first).ShouldNotBeNull();
        CommandKeys.TryRead(anonymous.Request, _quote, AlvoContext.Anonymous, null, "fp", out var second).ShouldNotBeNull();
        first.ShouldBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public void A_usable_header_makes_a_key_scoped_to_the_command_the_caller_and_the_credential()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[CommandKeys.Header] = "order-7";

        CommandKeys.TryRead(http.Request, _quote, _user, "key-a", "fp", out var key).ShouldBeNull();

        key.ShouldBe(new CommandKey($"command:quote_stay/{AlvoIdempotency.IdentityOf(_user)}/key-a", "order-7", "fp"));
    }

    [Fact]
    public void No_header_is_not_sent_and_makes_no_key()
    {
        var request = new DefaultHttpContext().Request;

        CommandKeys.Sent(request).ShouldBeFalse("no fingerprint is computed for a request that sent no key");
        CommandKeys.TryRead(request, _quote, _user, "key-a", "fp", out var key).ShouldBeNull();
        key.ShouldBeNull();
    }

    [Fact]
    public void A_header_even_an_empty_one_is_sent()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[CommandKeys.Header] = string.Empty;

        CommandKeys.Sent(http.Request).ShouldBeTrue();
    }
}
