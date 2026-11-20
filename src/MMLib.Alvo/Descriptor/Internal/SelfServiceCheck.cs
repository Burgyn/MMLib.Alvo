namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The apply-time refusals of <c>tenancy.selfService</c> (spec §3.1): self-service tenants are tenants, invitations need
/// a shared tenant to invite into, and a tenant role is an application role every membership role is drawn from.
/// </summary>
/// <remarks>
/// <b>Every cross-key rule is this check's, none is the schema's</b>, so each refusal is reported once, at its own
/// pointer and with a fix — the lesson of <see cref="SignUpCheck"/>, whose schema conditional reported the same fault
/// twice until the validator learned to translate it. All refusals are collected, so an author fixes them in one
/// round trip. A role named in <c>personalTenant.roles</c> or <c>creatorRoles</c> that <c>assignableRoles</c> lists is
/// not refused again when that <c>assignableRoles</c> entry is itself refused: the fault is reported where it lies.
/// </remarks>
internal static class SelfServiceCheck
{
    private const string Root = "/tenancy/selfService";

    /// <summary>Every refusal <paramref name="descriptor"/>'s <c>tenancy.selfService</c> earns; empty when it has none.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Tenancy?.SelfService is not { } selfService)
        {
            return [];
        }

        var assignable = (selfService.AssignableRoles ?? []).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. WithoutTenancy(descriptor),
            .. InvitationsWithoutCreateTenants(selfService),
            .. AssignableRoleErrors(selfService.AssignableRoles ?? [], descriptor.Auth?.Roles ?? []),
            .. OutsideAssignable(selfService.PersonalTenant?.Roles, $"{Root}/personalTenant/roles", assignable),
            .. OutsideAssignable(selfService.CreateTenants?.CreatorRoles, $"{Root}/createTenants/creatorRoles", assignable),
        ];
    }

    private static IEnumerable<DescriptorValidationError> WithoutTenancy(AlvoDescriptor descriptor)
    {
        if (descriptor.Tenancy?.Enabled != true)
        {
            yield return Error(
                Root,
                "tenancy.selfService needs tenancy.enabled: true — self-service tenants are tenants.",
                "Set \"tenancy\": { \"enabled\": true } beside selfService, or remove selfService.");
        }
    }

    private static IEnumerable<DescriptorValidationError> InvitationsWithoutCreateTenants(TenancySelfService selfService)
    {
        if (selfService.Invitations == true && selfService.CreateTenants is null)
        {
            yield return Error(
                $"{Root}/invitations",
                "invitations let owners invite people into shared tenants, and without createTenants no shared tenant can exist.",
                "Add \"createTenants\": { \"creatorRoles\": [ … ] }, or remove invitations.");
        }
    }

    private static IEnumerable<DescriptorValidationError> AssignableRoleErrors(
        IReadOnlyList<string> assignable, IReadOnlyList<string> declared)
    {
        for (var i = 0; i < assignable.Count; i++)
        {
            if (AssignableRoleError(assignable[i], $"{Root}/assignableRoles/{i}", declared) is { } error)
            {
                yield return error;
            }
        }
    }

    private static DescriptorValidationError? AssignableRoleError(string role, string pointer, IReadOnlyList<string> declared)
    {
        if (RoleCatalog.BuiltInOnly.TryGet(role, out _))
        {
            return Error(
                pointer,
                $"'{role}' is a built-in role, and a tenant role must be an application role from auth.roles.",
                $"Remove '{role}' from assignableRoles, and name an application role declared in auth.roles instead.");
        }

        return declared.Contains(role, StringComparer.Ordinal)
            ? null
            : Error(pointer, $"'{role}' is not declared in auth.roles.", $"Add '{role}' to auth.roles, or remove it from assignableRoles.");
    }

    private static IEnumerable<DescriptorValidationError> OutsideAssignable(
        IReadOnlyList<string>? roles, string pointer, HashSet<string> assignable)
    {
        for (var i = 0; i < (roles?.Count ?? 0); i++)
        {
            var role = roles![i];
            if (!assignable.Contains(role))
            {
                yield return Error(
                    $"{pointer}/{i}",
                    $"'{role}' is not in tenancy.selfService.assignableRoles, and a membership can only ever carry a tenant role.",
                    $"Add '{role}' to assignableRoles, or remove it here.");
            }
        }
    }

    private static DescriptorValidationError Error(string pointer, string message, string fix) =>
        new(pointer, message, fix, DescriptorValidationSeverity.Error);
}
