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
/// <b>Every instant and every id is passed in.</b> The store mints nothing and reads no clock, so the caller's
/// <see cref="TimeProvider"/> decides every boundary and a test pins them exactly.
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
