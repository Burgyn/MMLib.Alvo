namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The apply-time refusal of <c>auth.accessTokens</c> (spec §3.1, S5 plan D8): a service token acts as a shared tenant,
/// so <c>service: true</c> needs <c>tenancy.selfService.createTenants</c> — the only way a shared tenant, and an owner to
/// mint one for it, can exist.
/// </summary>
/// <remarks>
/// <b>Recorded deviation</b> (S5 plan D8): stricter than §3.1's "Requires <c>selfService</c>", which this refusal
/// implies — a personal tenant has exactly one member forever, so a service token there would be a second actor its
/// owner never needs. The cross-key rule is this check's, none is the schema's, so the fault is reported once, with a
/// fix (the <see cref="SignUpCheck"/> lesson).
/// </remarks>
internal static class AccessTokensCheck
{
    private const string ServicePointer = "/auth/accessTokens/service";

    /// <summary>Every refusal <paramref name="descriptor"/>'s <c>auth.accessTokens</c> earns; empty when it has none.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Auth?.AccessTokens?.Service != true)
        {
            return [];
        }

        return descriptor.Tenancy?.SelfService switch
        {
            null => [WithoutSelfService],
            { CreateTenants: null } => [WithoutCreateTenants],
            _ => [],
        };
    }

    private static DescriptorValidationError WithoutSelfService { get; } = new(
        ServicePointer,
        "Service tokens act as a shared tenant, and without tenancy.selfService no tenant has an owner to mint one.",
        "Add \"tenancy\": { \"enabled\": true, \"selfService\": { \"createTenants\": { … }, \"assignableRoles\": [ … ] } }, or set service to false.",
        DescriptorValidationSeverity.Error);

    private static DescriptorValidationError WithoutCreateTenants { get; } = new(
        ServicePointer,
        "Service tokens act as a shared tenant, and without createTenants no shared tenant can exist.",
        "Add \"createTenants\": { \"creatorRoles\": [ … ] } to tenancy.selfService, or set service to false.",
        DescriptorValidationSeverity.Error);
}
