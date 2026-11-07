using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Secrets.Internal;

/// <summary>
/// Checks a descriptor's references to secrets — each endpoint's <c>secretRef</c>, each <c>http.call</c>'s
/// <c>headersSecretRef</c> — by name only: a value outside the name grammar is refused without being echoed (it may be the
/// secret pasted in the wrong place), and a well-formed name the store does not hold yet is a warning.
/// </summary>
/// <remarks>
/// A missing secret is not refused: nothing reads it until deliveries are signed, and a dashboard-first author adds the endpoint
/// before the secret. The store is asked for its names, never a value.
/// </remarks>
internal static class SecretReferenceCheck
{
    internal const string NotASecretName = "This value is not a secret name.";

    internal const string NotASecretNameFix =
        "A secret name is lower case, starts with a letter, and has at most 64 letters, digits, dots, dashes and underscores — "
        + "the name the secret is stored under, never the secret itself.";

    private const string SecretRefSlot = "secretRef";
    private const string HeadersSecretRefSlot = "headersSecretRef";

    /// <summary>Whether the pointer names a secret-reference slot, whose value no message may repeat.</summary>
    internal static bool IsReferenceSlot(string pointer) =>
        pointer.EndsWith($"/{SecretRefSlot}", StringComparison.Ordinal) || pointer.EndsWith($"/{HeadersSecretRefSlot}", StringComparison.Ordinal);

    /// <summary>The refusal of a secret-reference slot whose value is not a name, carrying the pointer and never the value.</summary>
    internal static DescriptorValidationError Refusal(string pointer) =>
        new(pointer, NotASecretName, NotASecretNameFix, DescriptorValidationSeverity.Error);

    /// <summary>Every reference whose value is outside the name grammar.</summary>
    internal static IEnumerable<DescriptorValidationError> Grammar(AlvoDescriptor descriptor) =>
        References(descriptor).Where(reference => !SecretName.TryParse(reference.Value, out _)).Select(reference => Refusal(reference.Pointer));

    /// <summary>A warning per well-formed reference the store does not hold — or one, when the store cannot be listed.</summary>
    internal static async Task<IReadOnlyList<DescriptorValidationError>> MissingAsync(
        AlvoDescriptor descriptor, ISecretStore secrets, CancellationToken ct)
    {
        var named = References(descriptor).Where(reference => SecretName.TryParse(reference.Value, out _)).ToList();
        if (named.Count == 0)
        {
            return [];
        }

        try
        {
            var held = (await secrets.ListNamesAsync(ct).ConfigureAwait(false)).Select(name => name.Value).ToHashSet(StringComparer.Ordinal);
            return [.. named.Where(reference => !held.Contains(reference.Value)).Select(Absent)];
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return [Unlisted(failure.GetType().FullName ?? failure.GetType().Name)];
        }
    }

    private static DescriptorValidationError Absent(SecretReference reference) => new(
        reference.Pointer,
        $"The secret '{reference.Value}' is not in the secret store yet. Deliveries are not signed in this build, so nothing fails today; signing will need it.",
        $"Set it under Alvo:Secrets:Values:{reference.Value}, or in the dashboard under Settings → Secrets.",
        DescriptorValidationSeverity.Warning);

    private static DescriptorValidationError Unlisted(string type) => new(
        "/",
        $"The secret store could not be listed ({type}), so secret references were not checked.",
        "Check the secret store's configuration and reachability.",
        DescriptorValidationSeverity.Warning);

    private static IEnumerable<SecretReference> References(AlvoDescriptor descriptor) =>
        [.. EndpointReferences(descriptor), .. HookReferences(descriptor), .. RuleReferences(descriptor)];

    private static IEnumerable<SecretReference> EndpointReferences(AlvoDescriptor descriptor) =>
        from endpoint in descriptor.Webhooks?.Endpoints ?? new Dictionary<string, WebhookEndpoint>()
        where endpoint.Value?.SecretRef is not null
        select new SecretReference($"/webhooks/endpoints/{endpoint.Key}/{SecretRefSlot}", endpoint.Value.SecretRef!);

    private static IEnumerable<SecretReference> HookReferences(AlvoDescriptor descriptor) =>
        from entity in descriptor.Entities
        from point in Points(entity.Value?.Hooks)
        from indexed in point.Hooks.Select((hook, index) => (hook, index))
        where indexed.hook?.Action is HttpCallAction { HeadersSecretRef: not null }
        select new SecretReference(
            $"/entities/{entity.Key}/hooks/{point.Name}/{indexed.index}/action/{HeadersSecretRefSlot}",
            ((HttpCallAction)indexed.hook.Action).HeadersSecretRef!);

    private static IEnumerable<SecretReference> RuleReferences(AlvoDescriptor descriptor) =>
        from rule in descriptor.Automation ?? new Dictionary<string, AutomationRule>()
        from indexed in (rule.Value?.Actions ?? []).Select((action, index) => (action, index))
        where indexed.action is HttpCallAction { HeadersSecretRef: not null }
        select new SecretReference(
            $"/automation/{rule.Key}/actions/{indexed.index}/{HeadersSecretRefSlot}", ((HttpCallAction)indexed.action).HeadersSecretRef!);

    private static IEnumerable<(string Name, IReadOnlyList<AfterHook> Hooks)> Points(EntityHooks? hooks) =>
        hooks is null
            ? []
            : [("afterCreate", hooks.AfterCreate ?? []), ("afterUpdate", hooks.AfterUpdate ?? []), ("afterDelete", hooks.AfterDelete ?? [])];

    /// <summary>One reference: where it is declared, and the value written there.</summary>
    private sealed record SecretReference(string Pointer, string Value);
}
