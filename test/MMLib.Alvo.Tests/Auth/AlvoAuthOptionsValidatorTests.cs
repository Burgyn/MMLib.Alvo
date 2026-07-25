using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tests.Auth;

public class AlvoAuthOptionsValidatorTests
{
    private static readonly AlvoAuthOptionsValidator _validator = new();

    private static AlvoDevApiKey ValidKey(string keyId = "dev") => new()
    {
        KeyId = keyId,
        Secret = "s3cret",
        User = Guid.NewGuid(),
        Roles = { "authenticated" },
        Scopes = { "orders:read" },
    };

    [Fact]
    public void A_fully_valid_configuration_succeeds()
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(ValidKey());

        _validator.Validate(name: null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void An_unparseable_scope_fails_naming_the_offending_value()
    {
        var options = new AlvoAuthOptions();
        var key = ValidKey();
        key.Scopes.Add("orders:reed");
        options.DevKeys.Add(key);

        var result = _validator.Validate(name: null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("orders:reed");
    }

    [Fact]
    public void An_empty_KeyId_fails()
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(ValidKey(keyId: string.Empty));

        _validator.Validate(name: null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_Secret_fails_naming_the_key()
    {
        var options = new AlvoAuthOptions();
        var key = ValidKey();
        key.Secret = string.Empty;
        options.DevKeys.Add(key);

        var result = _validator.Validate(name: null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("dev");
    }

    [Fact]
    public void A_KeyId_containing_the_separator_fails()
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(ValidKey(keyId: "dev.other"));

        var result = _validator.Validate(name: null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("dev.other");
    }

    [Fact]
    public void Duplicate_KeyIds_fail_rather_than_silently_shadowing_one_another()
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(ValidKey());
        options.DevKeys.Add(ValidKey());

        var result = _validator.Validate(name: null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("dev");
    }

    [Fact]
    public void AddAlvo_fails_fast_at_startup_validation_for_a_misconfigured_dev_key()
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        services.Configure<AlvoAuthOptions>(options => options.DevKeys.Add(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = "s3cret",
            User = Guid.NewGuid(),
            Roles = { "authenticated" },
            Scopes = { "orders:reed" },
        }));

        using var provider = services.BuildServiceProvider();

        var exception = Should.Throw<AggregateException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        exception.Message.ShouldContain("orders:reed");
    }
}
