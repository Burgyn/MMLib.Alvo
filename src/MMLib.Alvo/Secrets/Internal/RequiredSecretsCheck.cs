using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Secrets.Internal;

/// <summary>
/// Refuses a boot that lacks a secret the host declared with <c>AddRequiredSecret</c>: each value is read only to test it for
/// emptiness and dropped, and a store that cannot be read fails closed, naming its exception type and never its message.
/// </summary>
/// <param name="required">Every declared secret, one per call.</param>
/// <param name="secrets">The secret store the boot reads.</param>
internal sealed class RequiredSecretsCheck(IEnumerable<RequiredSecret> required, ISecretStore secrets)
{
    /// <summary>Whether the operator can add a missing secret in this process, so a dashboard-first boot stands down instead of exiting.</summary>
    internal bool CanBeFixedInProcess => secrets.CanWrite;

    /// <summary>Throws when a required secret is absent, empty or unreadable; reads names, keeps no value.</summary>
    /// <exception cref="AlvoStartupRefusedException">A required secret is missing, or the store failed.</exception>
    internal async Task RefuseAsync(CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var name in required.Select(secret => secret.Name).Distinct())
        {
            if (!await PresentAsync(name, ct).ConfigureAwait(false))
            {
                missing.Add(name.Value);
            }
        }

        if (missing.Count > 0)
        {
            throw Missing(missing);
        }
    }

    private async Task<bool> PresentAsync(SecretName name, CancellationToken ct)
    {
        try
        {
            return !string.IsNullOrEmpty(await secrets.GetAsync(name, ct).ConfigureAwait(false));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw Unreadable(name, failure.GetType().FullName ?? failure.GetType().Name);
        }
    }

    private static AlvoStartupRefusedException Missing(List<string> names) => new(
        $"Alvo cannot start: this host requires the secret(s) '{string.Join("', '", names)}', which the secret store does not hold.",
        "Set each one under Alvo:Secrets:Values:<name>, or in the dashboard under Settings → Secrets, then start again.");

    private static AlvoStartupRefusedException Unreadable(SecretName name, string type) => new(
        $"Alvo cannot start: the secret store failed ({type}) while checking the required secret '{name.Value}', so it cannot be confirmed present.",
        "Check the secret store's configuration and reachability.");
}
