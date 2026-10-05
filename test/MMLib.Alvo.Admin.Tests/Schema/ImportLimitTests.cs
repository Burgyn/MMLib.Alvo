using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The circuit receives a realistic descriptor whole, and what it would not receive is refused at the box before it is
/// sent, never by a circuit that closes without a word (#316).
/// </summary>
public sealed class ImportLimitTests
{
    [Fact]
    public void Adding_the_dashboard_raises_the_circuits_receive_limit_to_the_import_ceiling()
        => CircuitReceiveLimit(new ServiceCollection().AddAlvoAdmin()).ShouldBe(ImportLimit.CircuitReceiveBytes);

    [Fact]
    public void A_host_that_turned_the_dashboard_off_keeps_signalrs_own_limit()
        => CircuitReceiveLimit(new ServiceCollection().AddAlvoAdmin(admin => admin.Enabled = false)).ShouldBe(32 * 1024);

    [Fact]
    public void Only_the_circuit_hub_is_raised()
    {
        using var provider = new ServiceCollection().AddAlvoAdmin().BuildServiceProvider();

        provider.GetRequiredService<IOptions<HubOptions>>().Value.MaximumReceiveMessageSize.ShouldBe(32 * 1024);
    }

    [Fact]
    public void A_host_that_already_allows_more_keeps_it()
    {
        var services = new ServiceCollection();
        services.AddSignalR(hub => hub.MaximumReceiveMessageSize = 64 * 1024 * 1024);

        CircuitReceiveLimit(services.AddAlvoAdmin()).ShouldBe(64 * 1024 * 1024);
    }

    [Fact]
    public void A_host_that_lifted_the_limit_keeps_it_lifted()
    {
        var services = new ServiceCollection();
        services.AddSignalR(hub => hub.MaximumReceiveMessageSize = null);

        CircuitReceiveLimit(services.AddAlvoAdmin()).ShouldBeNull();
    }

    [Fact]
    public void The_largest_paste_the_box_lets_through_still_fits_one_circuit_message()
    {
        /* Every character of a box at the character ceiling escaped to two bytes ('"' as \", 'č' as UTF-8) is still
           under the byte ceiling, so a descriptor of Latin text meets the character ceiling first and only. */
        (2L * ImportLimit.MaxChars + 2).ShouldBeLessThanOrEqualTo(ImportLimit.MaxSentBytes);
        (ImportLimit.MaxSentBytes + ImportLimit.EnvelopeBytes).ShouldBe(ImportLimit.CircuitReceiveBytes);
    }

    [Fact]
    public void The_bike_workshop_example_is_far_inside_the_ceiling()
        => (File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json")).Length * 10).ShouldBeLessThan(ImportLimit.MaxChars);

    [Theory]
    [InlineData("1200000 1200002", "1,200,000 characters")]
    [InlineData("400000 2400002", "2,344 KB")]
    public void A_refusal_says_the_paste_and_names_both_ceilings(string measured, string paste)
    {
        var refusal = ImportLimit.Refusal(measured);

        refusal.ShouldContain(paste);
        refusal.ShouldContain("1,000,000 characters");
        refusal.ShouldContain("1,984 KB");
        refusal.ShouldContain("not loaded");
    }

    [Fact]
    public void A_text_that_reached_the_circuit_is_measured_as_alvo_js_measures_it()
        => ImportLimit.Measure("a\"č").ShouldBe("3 7", "the two enclosing quotes, a, the escaped quote (2) and č (2)");

    [Fact]
    public void A_refusal_with_no_measurement_still_names_the_ceiling()
        => ImportLimit.Refusal(null).ShouldContain("1,000,000 characters");

    private static long? CircuitReceiveLimit(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var hub = typeof(Microsoft.AspNetCore.Components.Server.CircuitOptions).Assembly
            .GetType("Microsoft.AspNetCore.Components.Server.ComponentHub", throwOnError: true)!;
        var options = provider.GetRequiredService(typeof(IOptions<>).MakeGenericType(typeof(HubOptions<>).MakeGenericType(hub)));
        var value = (HubOptions)options.GetType().GetProperty(nameof(IOptions<object>.Value))!.GetValue(options)!;
        return value.MaximumReceiveMessageSize;
    }
}
