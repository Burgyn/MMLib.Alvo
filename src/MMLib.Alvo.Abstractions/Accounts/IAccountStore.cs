using System.Text;

namespace MMLib.Alvo.Accounts;

/// <summary>
/// Where end-user accounts, their identities, their sessions and the refresh tokens of those sessions live — every write
/// the accounts spec calls "one store call, one transaction" is one member here. Implemented by the relational drivers;
/// <c>MMLib.Alvo.Accounts</c> reaches it through <c>InternalsVisibleTo</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, the ledger precedent</b> (<c>ICommandLedger</c>): one in-family producer and one in-family consumer, so
/// publishing it would be a commitment no third party has asked for. Making it public later is additive.
/// </para>
/// <para>
/// <b>Every instant is passed in</b>, and so is every id the caller has to know before the call (a session's, a refresh
/// token's, a key's). The store reads no clock, so the caller's <see cref="TimeProvider"/> decides every boundary and a
/// test pins them exactly; the one id it mints is a new account's, with <see cref="UserId.New"/>.
/// </para>
/// <para>
/// <b>An account is found by <c>(provider, subject)</c>, never by email</b> (spec §5.7): two identities asserting one
/// address are two accounts until a later slice links them deliberately.
/// </para>
/// </remarks>
internal interface IAccountStore
{
    /// <summary>
    /// Signs a verified identity in, in one transaction: finds or (when <see cref="SignInRequest.MayCreate"/>) creates its
    /// account, refuses a disabled one, starts a session keyed by the subject token's id and issues its first refresh
    /// token. Every refusal writes nothing.
    /// </summary>
    /// <param name="request">The identity, the sign-up decision, and the ids and instants the session starts with.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="session"/> belongs to <paramref name="account"/>, is not revoked, has not reached its
    /// absolute expiry at <paramref name="now"/>, and its account is not disabled — one statement.
    /// </summary>
    /// <param name="account">The account the access token names.</param>
    /// <param name="session">The session the access token names.</param>
    /// <param name="now">The instant to judge expiry at.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<bool> IsSessionLiveAsync(UserId account, Guid session, DateTimeOffset now, CancellationToken ct);

    /// <summary>The account and the providers it signs in with, or <see langword="null"/> when there is none.</summary>
    /// <param name="account">The account's id.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<AccountView?> GetAccountAsync(UserId account, CancellationToken ct);

    /// <summary>Replaces the account's display name; <see langword="false"/> when there is no such account.</summary>
    /// <param name="account">The account's id.</param>
    /// <param name="name">The new display name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<bool> SetDisplayNameAsync(UserId account, string name, CancellationToken ct);

    /// <summary>
    /// Rotates a refresh token, in one transaction (spec §5.5, D6): an unrotated token gets the request's child; a token
    /// rotated within the grace window answers the child it already has, so the family never forks; one rotated longer ago
    /// is reuse, and revokes its session (<see cref="SessionRevocations.Reuse"/>) before answering. An unknown token, an
    /// idle one, a revoked or expired session and a disabled account are each refused, identically.
    /// </summary>
    /// <param name="request">The token presented, the child to mint, the instant, the grace window and the idle limit.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<RefreshOutcome> RefreshAsync(RefreshRequest request, CancellationToken ct);

    /// <summary>
    /// Revokes <paramref name="session"/> for <paramref name="reason"/>; idempotent — a revoked session keeps its first
    /// instant and reason.
    /// </summary>
    /// <param name="session">The session's id.</param>
    /// <param name="reason">One of <see cref="SessionRevocations"/>.</param>
    /// <param name="now">The instant of the revocation.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when the session exists, revoked by this call or before.</returns>
    Task<bool> RevokeSessionAsync(Guid session, string reason, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Disables an account or enables a disabled one, and either way revokes every live session of it
    /// (<see cref="SessionRevocations.Disabled"/>) in the same transaction (spec §5.6): ending a ban is a clean slate, so
    /// no session from before it comes back, even one that reached the table after the ban's revocation. Enabling an
    /// account that is not disabled changes nothing and revokes nothing.
    /// </summary>
    /// <param name="account">The account's id.</param>
    /// <param name="disabled">Whether the account is to be disabled.</param>
    /// <param name="now">The instant of the change.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="false"/> when there is no such account.</returns>
    Task<bool> SetDisabledAsync(UserId account, bool disabled, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Inserts <paramref name="candidate"/> unless a key of its purpose and generation exists, and answers the key that
    /// stands — so replicas booting at once converge on the first one written (S2 D12).
    /// </summary>
    /// <param name="candidate">The key this caller generated, used only when it is the first.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">The deployment mounted no secret encryption key.</exception>
    Task<StoredKey> EnsureKeyAsync(KeyCandidate candidate, CancellationToken ct);

    /// <summary>The keys of <paramref name="purpose"/> not retired at <paramref name="now"/>, by generation.</summary>
    /// <param name="purpose">One of <see cref="AccountKeyPurposes"/>.</param>
    /// <param name="now">The instant to judge retirement at.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">The deployment mounted no secret encryption key.</exception>
    Task<IReadOnlyList<StoredKey>> ReadKeysAsync(string purpose, DateTimeOffset now, CancellationToken ct);
}

/// <summary>An identity a provider asserted and the verifier accepted.</summary>
/// <param name="Provider">The provider's name in the descriptor, e.g. <c>google</c>.</param>
/// <param name="Subject">The provider's stable subject (<c>sub</c>).</param>
/// <param name="Email">The asserted email, if any.</param>
/// <param name="EmailTrusted">Whether the provider's preset trusts that email (spec §5.3).</param>
/// <param name="Name">The asserted display name, if any.</param>
/// <param name="Picture">The asserted avatar URL, if any.</param>
internal sealed record VerifiedIdentity(
    string Provider, string Subject, string? Email, bool EmailTrusted, string? Name, string? Picture);

/// <summary>One sign-in, with every id and instant it writes chosen by the caller.</summary>
/// <param name="Identity">The verified identity.</param>
/// <param name="MayCreate">Whether the sign-up policy admits a new account for an identity seen for the first time.</param>
/// <param name="SubjectTokenId">The subject token's <c>jti</c>, or its hash; unique across sessions (spec §5.2 step 3).</param>
/// <param name="SessionId">The id of the session this sign-in starts.</param>
/// <param name="RefreshTokenId">The id of the session's first refresh token.</param>
/// <param name="Now">The instant of the sign-in.</param>
/// <param name="SessionExpiresAt">The session's absolute expiry.</param>
internal sealed record SignInRequest(
    VerifiedIdentity Identity,
    bool MayCreate,
    string SubjectTokenId,
    Guid SessionId,
    Guid RefreshTokenId,
    DateTimeOffset Now,
    DateTimeOffset SessionExpiresAt);

/// <summary>What a sign-in did.</summary>
internal enum SignInStatus
{
    /// <summary>An existing account signed in.</summary>
    SignedIn,

    /// <summary>A new account was created and signed in.</summary>
    Created,

    /// <summary>The identity is new and the sign-up policy refused it; nothing was written.</summary>
    SignUpRefused,

    /// <summary>The account is disabled; nothing was written.</summary>
    Disabled,

    /// <summary>The subject token already started a session; nothing was written.</summary>
    SubjectTokenReplayed,
}

/// <summary>A sign-in's status and, when it signed in, the account.</summary>
/// <param name="Status">What the sign-in did.</param>
/// <param name="Account">The account, for <see cref="SignInStatus.SignedIn"/> and <see cref="SignInStatus.Created"/>.</param>
internal readonly record struct SignInOutcome(SignInStatus Status, UserId? Account);

/// <summary>An account as <c>GET /user</c> shows it.</summary>
/// <param name="Id">The account's id.</param>
/// <param name="Email">The email the last sign-in asserted.</param>
/// <param name="EmailTrusted">Whether that email is trusted.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="AvatarUrl">The avatar URL the last sign-in asserted.</param>
/// <param name="Providers">The providers of the account's identities, in ordinal order.</param>
/// <param name="CreatedAt">When the account was created.</param>
internal sealed record AccountView(
    UserId Id,
    string? Email,
    bool EmailTrusted,
    string? DisplayName,
    string? AvatarUrl,
    IReadOnlyList<string> Providers,
    DateTimeOffset CreatedAt);

/// <summary>One presentation of a refresh token.</summary>
/// <param name="TokenId">The id the presented token's value carries (its HMAC already verified by the caller).</param>
/// <param name="ChildId">The id of the child to mint if this presentation rotates the token.</param>
/// <param name="Now">The instant of the presentation.</param>
/// <param name="Grace">How long after its rotation a token still answers its child; zero refuses any reuse.</param>
/// <param name="Idle">How long an unrotated token stays usable after it was issued.</param>
internal sealed record RefreshRequest(Guid TokenId, Guid ChildId, DateTimeOffset Now, TimeSpan Grace, TimeSpan Idle);

/// <summary>What presenting a refresh token did.</summary>
internal enum RefreshStatus
{
    /// <summary>The token was unrotated and now has the request's child.</summary>
    Rotated,

    /// <summary>The token was rotated within the grace window; its existing child is answered again.</summary>
    Replayed,

    /// <summary>The token was rotated before the grace window; its session is now revoked.</summary>
    Reused,

    /// <summary>The token, its session or its account is not usable; nothing was written.</summary>
    Refused,
}

/// <summary>A refresh's status and, when it answers a child, the child, the account, the session and its provider.</summary>
/// <param name="Status">What the refresh did.</param>
/// <param name="ChildId">The child to answer, for <see cref="RefreshStatus.Rotated"/> and <see cref="RefreshStatus.Replayed"/>.</param>
/// <param name="Account">The session's account, likewise.</param>
/// <param name="Session">The session, likewise.</param>
/// <param name="Provider">The provider the session began with, likewise.</param>
internal readonly record struct RefreshOutcome(
    RefreshStatus Status, Guid? ChildId, UserId? Account, Guid? Session, string? Provider);

/// <summary>A key a caller generated, to be stored only if it is the first of its purpose and generation.</summary>
/// <param name="Purpose">One of <see cref="AccountKeyPurposes"/>.</param>
/// <param name="Generation">The key's generation; 1 until rotation exists.</param>
/// <param name="Kid">The key's id.</param>
/// <param name="Material">The private key or HMAC secret; stored encrypted, never printed.</param>
/// <param name="Now">The instant of the attempt.</param>
internal sealed record KeyCandidate(string Purpose, int Generation, string Kid, string Material, DateTimeOffset Now)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Purpose = ").Append(Purpose);
        builder.Append(", Generation = ").Append(Generation);
        builder.Append(", Kid = ").Append(Kid);
        builder.Append(", Material = ***");
        builder.Append(", Now = ").Append(Now);
        return true;
    }
}

/// <summary>A stored key, its material decrypted.</summary>
/// <param name="Purpose">One of <see cref="AccountKeyPurposes"/>.</param>
/// <param name="Generation">The key's generation.</param>
/// <param name="Kid">The key's id.</param>
/// <param name="Material">The private key or HMAC secret; never printed.</param>
/// <param name="CreatedAt">When the key was stored.</param>
/// <param name="RetiresAt">When the key stops being read, if ever.</param>
internal sealed record StoredKey(
    string Purpose, int Generation, string Kid, string Material, DateTimeOffset CreatedAt, DateTimeOffset? RetiresAt)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Purpose = ").Append(Purpose);
        builder.Append(", Generation = ").Append(Generation);
        builder.Append(", Kid = ").Append(Kid);
        builder.Append(", Material = ***");
        builder.Append(", CreatedAt = ").Append(CreatedAt);
        builder.Append(", RetiresAt = ").Append(RetiresAt);
        return true;
    }
}

/// <summary>What an account key is for.</summary>
internal static class AccountKeyPurposes
{
    /// <summary>The ES256 key access tokens are signed with.</summary>
    internal const string Signing = "signing";

    /// <summary>The HMAC key refresh-token values are derived with (D6).</summary>
    internal const string Refresh = "refresh";
}

/// <summary>Why a session was revoked.</summary>
internal static class SessionRevocations
{
    /// <summary>The person signed out.</summary>
    internal const string Logout = "logout";

    /// <summary>A refresh token of the session was presented again after its grace window.</summary>
    internal const string Reuse = "reuse";

    /// <summary>An operator disabled the account.</summary>
    internal const string Disabled = "disabled";
}
