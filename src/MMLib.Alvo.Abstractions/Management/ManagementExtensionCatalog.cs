namespace MMLib.Alvo.Management;

/// <summary>The answer of <see cref="IAlvoManagement.GetExtensionsAsync"/>: the host code registered with this instance.</summary>
/// <remarks>An envelope rather than a bare list, so it can grow additively.</remarks>
public sealed record ManagementExtensionCatalog
{
    /// <summary>Gets one entry per registered extension, by <c>Order</c> and then registration order.</summary>
    public required IReadOnlyList<ManagementExtension> Items { get; init; }
}
