using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Load.Host;
using MMLib.Alvo.TestHost;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// The load host's calibration hooks run exactly for their own reference prefix, so each k6 shape measures the hook it is
/// named after — a `when` that matched every write would make all four shapes the same number.
/// </summary>
public sealed class CalibrationHooksTests
{
    [Fact]
    public async Task Each_prefix_runs_exactly_its_own_hook()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "calibration-desk.alvo.json")).AddCalibrationHooks(),
            cancellationToken: ct);
        var dispatcher = AlvoTestHost.Caller("dispatcher");
        var customer = (Guid)(await alvo.Data.CreateAsync("customers", new Dictionary<string, object?> { ["name"] = "Ada" }, dispatcher, cancellationToken: ct))["id"]!;

        foreach (var reference in new[] { "WO-80000001", "WO-81000001", "WO-82000001", "WO-83000001" })
        {
            await alvo.Data.CreateAsync("work_orders", new Dictionary<string, object?> { ["reference"] = reference, ["customer_id"] = customer }, dispatcher, cancellationToken: ct);
        }

        alvo.Hooks.AssertRan("calibration-noop", times: 1);
        alvo.Hooks.AssertRan("calibration-read", times: 1);
        alvo.Hooks.AssertRan("calibration-after", times: 1);
    }

    [Fact]
    public async Task The_read_hook_fails_the_write_when_it_finds_no_customer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "calibration-desk.alvo.json")).AddCalibrationHooks(),
            cancellationToken: ct);

        await Should.ThrowAsync<Exception>(() => alvo.Data.CreateAsync(
            "work_orders", new Dictionary<string, object?> { ["reference"] = "WO-82000002", ["customer_id"] = Guid.NewGuid() },
            AlvoTestHost.Caller("dispatcher"), cancellationToken: ct));
    }
}
