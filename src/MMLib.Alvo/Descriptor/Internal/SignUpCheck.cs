namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The apply-time refusal of an end-user provider without <c>auth.signUp</c>: listing one changes what
/// <c>authenticated</c> means — from a provisioned caller to anyone who can sign in — so who may create an account is
/// said out loud, never defaulted.
/// </summary>
/// <remarks>
/// The schema refuses the same descriptor first (an <c>if</c>/<c>then</c> on <c>auth</c>), and the validator reports that
/// schema failure as <see cref="Refusal"/> (<see cref="IsSchemaRefusal"/>) — a bare <c>required</c> keyword would not say
/// what either choice means. <see cref="Check"/> is the same refusal on the typed model, for a path that never ran
/// the schema.
/// </remarks>
internal static class SignUpCheck
{
    /// <summary>The providers Alvo honours for end users today — the ones that let a person create an account.</summary>
    internal static IReadOnlySet<AuthProvider> EndUserProviders { get; } = new HashSet<AuthProvider> { AuthProvider.Google };

    /// <summary>The pointer the refusal is reported at.</summary>
    internal const string Pointer = "/auth/signUp";

    /// <summary>The refusal's message.</summary>
    internal const string Message =
        "auth.providers lists google, which lets people sign up, but auth.signUp says nothing about who may.";

    /// <summary>The refusal's fix, naming both modes.</summary>
    internal const string Fix =
        "Add \"signUp\": { \"mode\": \"open\" } to let anyone with a Google account create one, or "
        + "\"signUp\": { \"mode\": \"invitationOnly\" } to admit only people an owner invited.";

    /// <summary>The refusal, with both choices spelled out.</summary>
    internal static DescriptorValidationError Refusal { get; } =
        new(Pointer, Message, Fix, DescriptorValidationSeverity.Error);

    /// <summary>The one finding, when the descriptor lists an end-user provider and declares no <c>auth.signUp</c>.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return ListsEndUserProvider(descriptor) && descriptor.Auth!.SignUp is null ? [Refusal] : [];
    }

    /// <summary>Whether a schema failure is the schema's own spelling of this refusal: <c>signUp</c> required on <c>auth</c>.</summary>
    /// <param name="instancePath">The failure's instance pointer, as the schema pass writes it (<c>#/auth</c>).</param>
    /// <param name="message">The failure's message.</param>
    internal static bool IsSchemaRefusal(string instancePath, string? message) =>
        instancePath is "#/auth" or "/auth"
        && message?.Contains("required property 'signUp'", StringComparison.Ordinal) == true;

    /// <summary>Whether <paramref name="descriptor"/> lists a provider a person can sign up through.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static bool ListsEndUserProvider(AlvoDescriptor descriptor) =>
        descriptor.Auth?.Providers?.Any(EndUserProviders.Contains) == true;
}
