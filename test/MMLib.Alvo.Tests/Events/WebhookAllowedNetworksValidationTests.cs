using Microsoft.Extensions.Configuration;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The webhook egress opt-in is bound from configuration and refused at startup when an entry is not a
/// network, so a typo cannot silently leave an internal receiver unreachable — or read as "allow nothing".
/// </summary>
public sealed class WebhookAllowedNetworksValidationTests
{
    /// <summary>The list binds from its indexed configuration keys, in either address family.</summary>
    [Fact]
    public void The_allowed_networks_bind_from_configuration()
    {
        var options = Bound(new()
        {
            [$"{AlvoEventOptionsConfiguration.WebhookAllowedNetworksKey}:0"] = "10.0.0.0/8",
            [$"{AlvoEventOptionsConfiguration.WebhookAllowedNetworksKey}:1"] = "fd00::/8",
        });

        options.WebhookAllowedNetworks.ShouldBe(["10.0.0.0/8", "fd00::/8"]);
        Configuration(null).Validate(null, options).Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// An entry that is not CIDR notation — including one with host bits set past its prefix — is refused,
    /// naming the key, the environment-variable spelling and the offending value.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.1/8")]
    [InlineData("internal.example.com")]
    [InlineData("10.0.0.0/33")]
    public void An_entry_that_is_not_a_network_is_refused(string entry)
    {
        var options = new AlvoEventOptions();
        options.WebhookAllowedNetworks.Add(entry);

        var result = Configuration(null).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(entry);
        result.FailureMessage.ShouldContain(AlvoEventOptionsConfiguration.WebhookAllowedNetworksKey);
        result.FailureMessage.ShouldContain("Alvo__Events__WebhookAllowedNetworks__0");
    }

    /// <summary>With nothing configured, nothing non-public is allowed: the opt-in is empty by default.</summary>
    [Fact]
    public void The_opt_in_is_empty_by_default() => new AlvoEventOptions().WebhookAllowedNetworks.ShouldBeEmpty();

    private static AlvoEventOptions Bound(Dictionary<string, string?> values)
    {
        var options = new AlvoEventOptions();
        Configuration(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).Configure(options);

        return options;
    }

    private static AlvoEventOptionsConfiguration Configuration(IConfiguration? configuration) => new(configuration);
}
