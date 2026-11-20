using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using System.Globalization;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>What <c>POST {prefix}/tokens</c> answers when it does not mint (S5 plan D7, D11, D13).</summary>
/// <remarks>
/// <b>Every detail is a fixed sentence</b>; none echoes a tenant, a role or a scope the caller sent. A tenant the person is
/// no member of is the self-service routes' one <c>not-found</c>, byte for byte.
/// </remarks>
internal static class AppTokenProblems
{
    /// <summary>The descriptor key that enables every token route.</summary>
    internal const string AccessTokensKey = "auth.accessTokens";

    /// <summary>The descriptor key that enables personal tokens.</summary>
    internal const string PersonalKey = "auth.accessTokens.personal";

    /// <summary>The descriptor key that enables service tokens.</summary>
    internal const string ServiceKey = "auth.accessTokens.service";

    /// <summary>The most live tokens one account may have minted (S5 plan D13).</summary>
    internal const int MaxLive = 100;

    private const string InvalidToken = "invalid_token";

    private const string NotOwner = "Only an owner of this tenant mints a service token for it.";

    private const string Personal =
        "A personal tenant has exactly one member, its owner, and takes no service token; mint a personal token instead.";

    /// <summary>The 404 <c>feature-disabled</c> while <paramref name="policy"/> turns no kind on, else <see langword="null"/>.</summary>
    /// <param name="policy">The applied app-token policy.</param>
    internal static IResult? Off(AppTokenPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Any ? null : ProblemResultFactory.FeatureDisabled(AccessTokensKey);
    }

    /// <summary>The 404 <c>feature-disabled</c> while <paramref name="policy"/> leaves <paramref name="kind"/> off, else <see langword="null"/>.</summary>
    /// <param name="policy">The applied app-token policy.</param>
    /// <param name="kind">The kind the request asks for.</param>
    internal static IResult? KindOff(AppTokenPolicy policy, ApiKeyKind kind)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Allows(kind) ? null : ProblemResultFactory.FeatureDisabled(kind == ApiKeyKind.Service ? ServiceKey : PersonalKey);
    }

    /// <summary>The answer for a mint the registry refused.</summary>
    /// <param name="status">What the registry did; never <see cref="MintTokenStatus.Minted"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is <see cref="MintTokenStatus.Minted"/>.</exception>
    internal static IResult Refused(MintTokenStatus status) => status switch
    {
        MintTokenStatus.NoAccount or MintTokenStatus.SessionEnded => ProblemResultFactory.BearerUnauthenticated(InvalidToken),
        MintTokenStatus.NotFound => SelfServiceProblems.NoSuchTenant(),
        MintTokenStatus.NotOwner => ProblemResultFactory.PersonForbidden(NotOwner),
        MintTokenStatus.PersonalTenant => ProblemResultFactory.MembershipConflict(Personal),
        MintTokenStatus.RolesNotHeld => ProblemResultFactory.Validation([RolesNotHeld()]),
        MintTokenStatus.LimitReached => ProblemResultFactory.MembershipConflict(Limit()),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A mint that was made is no refusal."),
    };

    private static AlvoViolation RolesNotHeld() => new(
        PayloadViolations.PointerTo(SelfServiceRoles.Member),
        "invalid-value",
        "You can only give a token roles you hold in this tenant.",
        "Send only roles GET /tenants lists for you in this tenant.");

    private static string Limit() => string.Create(
        CultureInfo.InvariantCulture,
        $"This account already has {MaxLive} live tokens, the most one account may hold; revoke one first.");
}
