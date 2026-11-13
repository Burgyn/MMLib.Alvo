using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// What the applied descriptor says about signing in: which providers it lists, and who may create an account
/// (<c>auth.signUp</c>) — compiled with the rest of the policy, so the exchange reads the descriptor that is applied.
/// </summary>
/// <param name="ProjectName">The descriptor's <c>name</c>.</param>
/// <param name="Providers">The listed providers; empty when the descriptor lists none.</param>
/// <param name="Mode">The declared sign-up mode, or <see langword="null"/> when <c>auth.signUp</c> is absent.</param>
/// <param name="AllowedEmailDomains">The declared domains, lower-cased; empty admits any domain.</param>
internal sealed record SignInPolicy(
    string ProjectName,
    IReadOnlySet<AuthProvider> Providers,
    SignUpMode? Mode,
    IReadOnlyList<string> AllowedEmailDomains)
{
    /// <summary>The policy of no applied descriptor: no provider, so nobody signs in.</summary>
    internal static SignInPolicy None { get; } = new(string.Empty, new HashSet<AuthProvider>(), null, []);

    /// <summary>Builds the policy <paramref name="descriptor"/> declares.</summary>
    /// <param name="descriptor">The descriptor being compiled.</param>
    internal static SignInPolicy From(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var auth = descriptor.Auth;
        return new(
            descriptor.Name,
            new HashSet<AuthProvider>(auth?.Providers ?? []),
            auth?.SignUp?.Mode,
            [.. (auth?.SignUp?.AllowedEmailDomains ?? []).Select(domain => domain.ToLowerInvariant())]);
    }

    /// <summary>Whether the applied descriptor lists <paramref name="provider"/> — default-deny for every other.</summary>
    /// <param name="provider">The provider a sign-in arrives through.</param>
    internal bool Lists(AuthProvider provider) => Providers.Contains(provider);
}
