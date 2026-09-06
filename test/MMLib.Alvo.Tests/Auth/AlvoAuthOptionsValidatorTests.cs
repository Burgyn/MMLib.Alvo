using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using System.Globalization;

namespace MMLib.Alvo.Tests.Auth;

public class AlvoAuthOptionsValidatorTests
{
    private static readonly AlvoAuthOptionsValidator _validator = new();

    /// <summary>
    /// A secret that clears <see cref="AlvoAuthOptionsValidator.MinimumSecretLength"/> — the floor #125 put
    /// under a dev key — so a fact about some <em>other</em> field fails for that field alone.
    /// </summary>
    private const string LongEnoughSecret = "s3cret-value-long-enough-for-the-floor";

    private static AlvoDevApiKey ValidKey(string keyId = "dev") => new()
    {
        KeyId = keyId,
        Secret = LongEnoughSecret,
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
            Secret = LongEnoughSecret,
            User = Guid.NewGuid(),
            Roles = { "authenticated" },
            Scopes = { "orders:reed" },
        }));

        using var provider = services.BuildServiceProvider();

        var exception = Should.Throw<AggregateException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        exception.Message.ShouldContain("orders:reed");
    }

    /// <summary>A dev secret is accepted at the floor and refused one character below it.</summary>
    /// <remarks>
    /// The boundary is asserted from both sides, because a floor asserted only from below passes for an
    /// off-by-one that refuses every secret ever generated.
    /// </remarks>
    [Theory]
    [InlineData(AlvoAuthOptionsValidator.MinimumSecretLength - 1, false)]
    [InlineData(AlvoAuthOptionsValidator.MinimumSecretLength, true)]
    public void A_dev_secret_is_accepted_only_at_or_above_the_length_floor(int length, bool expected)
    {
        var options = new AlvoAuthOptions();
        var key = ValidKey();
        key.Secret = new string('x', length);
        options.DevKeys.Add(key);

        _validator.Validate(name: null, options).Succeeded.ShouldBe(expected);
    }

    /// <summary>A short secret is refused, and the refusal is diagnosable.</summary>
    /// <remarks>
    /// Naming the key and the length is the whole value of the refusal: an operator staring at a startup
    /// failure has to know <em>which</em> of several configured keys is short.
    /// </remarks>
    [Fact]
    public void A_short_dev_secret_fails_naming_the_key_and_both_lengths()
    {
        var options = new AlvoAuthOptions();
        var key = ValidKey(keyId: "short-key");
        key.Secret = "password";
        options.DevKeys.Add(key);

        var result = _validator.Validate(name: null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("short-key");
        result.FailureMessage.ShouldContain("8 characters");
        result.FailureMessage.ShouldContain(
            AlvoAuthOptionsValidator.MinimumSecretLength.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>An unset secret keeps its own message rather than becoming a length complaint.</summary>
    /// <remarks>
    /// An empty secret is almost always an unset environment variable, so it keeps its own message rather
    /// than being folded into the length refusal — which would send that operator to lengthen a value they
    /// never set.
    /// </remarks>
    [Fact]
    public void An_empty_dev_secret_is_still_reported_as_empty_rather_than_as_too_short()
    {
        var options = new AlvoAuthOptions();
        var key = ValidKey(keyId: "unset-key");
        key.Secret = string.Empty;
        options.DevKeys.Add(key);

        var result = _validator.Validate(name: null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("empty Secret");
        result.FailureMessage.ShouldNotContain("at least");
    }
}
