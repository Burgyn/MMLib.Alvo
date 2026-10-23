using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Who drives the outbox dispatcher is configuration: bound from <c>Alvo:Events:DispatchMode</c>, the background loop by
/// default, and refused at startup — with the same sentence — whether the value is misspelled or a number that names no
/// mode (spec 2026-10-14 §4.15).
/// </summary>
public sealed class AlvoEventDispatchModeTests
{
    [Fact]
    public void The_background_loop_drives_the_dispatcher_by_default() =>
        new AlvoEventOptions().DispatchMode.ShouldBe(AlvoEventDispatchMode.Background);

    [Fact]
    public void The_key_is_spelled_from_the_section_and_the_property() =>
        AlvoEventOptionsConfiguration.DispatchModeKey.ShouldBe("Alvo:Events:DispatchMode");

    [Theory]
    [InlineData("Manual")]
    [InlineData("manual")]
    public void The_mode_binds_from_configuration(string configured)
    {
        var options = Bound(new() { [AlvoEventOptionsConfiguration.DispatchModeKey] = configured });

        options.DispatchMode.ShouldBe(AlvoEventDispatchMode.Manual);
        Configuration(null).Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Manul")]
    [InlineData("")]
    public void A_misspelled_mode_is_refused_naming_the_key_and_both_choices(string configured)
    {
        var refusal = Should.Throw<OptionsValidationException>(
            () => Bound(new() { [AlvoEventOptionsConfiguration.DispatchModeKey] = configured }));

        refusal.Message.ShouldContain($"'{configured}'");
        ShouldNameTheFix(refusal.Message);
    }

    [Fact]
    public void A_number_that_names_no_mode_is_refused_the_same_way()
    {
        var options = Bound(new() { [AlvoEventOptionsConfiguration.DispatchModeKey] = "7" });

        var result = Configuration(null).Validate(null, options);

        result.Failed.ShouldBeTrue();
        ShouldNameTheFix(result.FailureMessage);
    }

    private static void ShouldNameTheFix(string message)
    {
        message.ShouldContain("Alvo:Events:DispatchMode");
        message.ShouldContain("Alvo__Events__DispatchMode");
        message.ShouldContain("'Background'");
        message.ShouldContain("'Manual'");
    }

    private static AlvoEventOptions Bound(Dictionary<string, string?> values)
    {
        var options = new AlvoEventOptions();
        Configuration(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).Configure(options);

        return options;
    }

    private static AlvoEventOptionsConfiguration Configuration(IConfiguration? configuration) => new(configuration);
}
