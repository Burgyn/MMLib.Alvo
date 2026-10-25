using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The operations routes' levels, as spec 2026-10-14 §4.8's table writes them: reads Viewer, changes Developer, switching code off Admin.</summary>
public sealed class OperationsLevelTests
{
    [Theory]
    [InlineData("ListDeliveries", "Viewer")]
    [InlineData("GetDelivery", "Viewer")]
    [InlineData("GetExtensionStats", "Viewer")]
    [InlineData("RedeliverDelivery", "Developer")]
    [InlineData("ReplayDeliveries", "Developer")]
    [InlineData("GetDeliveryFixture", "Developer")]
    [InlineData("PauseSubscriber", "Developer")]
    [InlineData("RunSchedule", "Developer")]
    [InlineData("SetExtensionEnabled", "Admin")]
    public void An_operations_route_requires_its_level(string operation, string level) =>
        ManagementOperations.RequiredLevel(Enum.Parse<ManagementOperation>(operation)).ToString().ShouldBe(level);
}
