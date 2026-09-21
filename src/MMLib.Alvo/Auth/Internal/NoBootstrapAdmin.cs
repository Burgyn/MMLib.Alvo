namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The default <see cref="IAlvoBootstrapAdmin"/>: there is no bootstrap administrator.
/// </summary>
/// <remarks>
/// Registered with <c>TryAddSingleton</c>, so installing <c>MMLib.Alvo.Identity</c> supersedes it
/// and a host without it keeps the closed answer. The alternative — leaving the service
/// unregistered — would make the consuming gate resolve <see langword="null"/> and decide, per call
/// site, what a missing answer means; one registered "no" is the same decision taken once.
/// </remarks>
internal sealed class NoBootstrapAdmin : IAlvoBootstrapAdmin
{
    /// <inheritdoc/>
    public bool IsBootstrapAdmin(UserId user) => false;
}
