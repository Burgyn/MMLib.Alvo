using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin;
using MMLib.Alvo.Identity;

namespace MMLib.Alvo.Host.Internal;

/// <summary>
/// The endpoint the set-password page posts to: a credential token redeemed for a password (design §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The steps are in the order design §2 gives, and the order is the point.</b> Antiforgery first, writing
/// nothing on failure. Then the input is bounded, before anything reads the store. Then two passwords that differ
/// are answered without touching it. Only then is the token redeemed, by the identity package, which runs the
/// password policy before it looks at the account or the token, so a weak password says nothing about either.
/// </para>
/// <para>
/// <b>Every refusal is one redirect, byte for byte</b>: an unknown address, a wrong, used or expired token, a
/// disabled person and the bootstrap administrator all answer <c>?failed=true</c> with no fragment. A mismatch and a
/// weak password carry the fragment back instead, so the person does not have to open the link again; the browser
/// keeps a redirect's fragment and never puts it in a request line.
/// </para>
/// <para>
/// <b>Nothing is logged that a person typed.</b> Not the token, not the password, and not the address, which on a
/// refusal is an attacker's input. A success is logged with the person's id, read back after the password is set.
/// </para>
/// </remarks>
internal static partial class AlvoAdminSetPassword
{
    /// <summary>The longest address the form accepts; RFC 5321's path limit, with room.</summary>
    internal const int MaximumEmailLength = 256;

    /// <summary>The longest token the form accepts; an issued one is a few hundred characters.</summary>
    internal const int MaximumTokenLength = 2048;

    /// <summary>The longest password the policy accepts, so a longer one is weak without being hashed.</summary>
    internal const int MaximumPasswordLength = 128;

    private const string Failed = $"{AlvoAdmin.SetPasswordPath}?failed=true";

    /// <summary>Redeems the posted token and sends the person on: to sign-in, or back to the page.</summary>
    /// <param name="http">The request.</param>
    /// <param name="signIn">The identity package's redemption.</param>
    /// <param name="antiforgery">The antiforgery service the form's token is checked with.</param>
    /// <param name="people">The store the person's id is read back from after a success.</param>
    /// <param name="limit">The credential limit, charged once the antiforgery check has passed.</param>
    /// <param name="loggers">Where the outcome is recorded.</param>
    /// <returns>A <c>303</c>.</returns>
    internal static async Task<IResult> SetPasswordAsync(
        HttpContext http,
        AlvoSignIn signIn,
        IAntiforgery antiforgery,
        IAlvoUserStore people,
        AlvoAdminCredentialLimit limit,
        ILoggerFactory loggers)
    {
        if (!AlvoAdminSignIn.WithinSize(http) || !await AlvoAdminSignIn.ValidAsync(http, antiforgery).ConfigureAwait(false))
        {
            return AlvoAdminRedirect.SeeOther(http, AlvoAdmin.SetPasswordPath);
        }

        var posted = Posted.From(await http.Request.ReadFormAsync().ConfigureAwait(false));
        if (limit.Charge(http, AlvoAdminCredentialLimit.SetPasswordSubject(posted.Token)) is { } wait)
        {
            return AlvoAdminCredentialLimit.Throttled(http, wait, $"{AlvoAdmin.SetPasswordPath}?throttled=true");
        }

        var logger = loggers.CreateLogger(typeof(AlvoAdminSetPassword).FullName!);
        var location = posted.Refusal()
            ?? await RedeemAsync(posted, signIn, people, logger, http.RequestAborted).ConfigureAwait(false);

        return AlvoAdminRedirect.SeeOther(http, location);
    }

    /// <summary>Redeems a bounded, matching post, and says where the person goes next.</summary>
    private static async Task<string> RedeemAsync(
        Posted posted, AlvoSignIn signIn, IAlvoUserStore people, ILogger logger, CancellationToken cancellationToken)
    {
        switch (await signIn.SetPasswordAsync(posted.Email, posted.Token, posted.Password, cancellationToken)
            .ConfigureAwait(false))
        {
            case AlvoPasswordSetOutcome.Set:
                await LogPasswordSetAsync(posted.Email, people, logger, cancellationToken).ConfigureAwait(false);
                return $"{AlvoAdmin.SignInPath}?passwordSet=true";
            case AlvoPasswordSetOutcome.PasswordRejected:
                return posted.Back("weak");
            default:
                TokenRefused(logger);
                return Failed;
        }
    }

    /// <summary>Records whose password was set, by id — never the address or the token.</summary>
    /// <remarks>
    /// Guarded by <see cref="ILogger.IsEnabled"/> because the lookup exists only for this line, so a host that does not
    /// log at Information pays no read; and the id is passed as itself rather than as a string built at the call, so the
    /// formatting is the generated logger's, done only when it writes (CA1873, an error in the Release build). A person
    /// the store no longer finds is written as the logger's null, never a made-up id.
    /// </remarks>
    private static async Task LogPasswordSetAsync(
        string email, IAlvoUserStore people, ILogger logger, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            var person = await people.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
            PasswordSet(logger, person?.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "A password was set with a credential token for person {PersonId}.")]
    private static partial void PasswordSet(ILogger logger, UserId? personId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "A credential token was refused on the set-password page.")]
    private static partial void TokenRefused(ILogger logger);

    /// <summary>The form as posted, with the token repaired.</summary>
    /// <param name="Email">The address.</param>
    /// <param name="Token">The token, repaired by <see cref="RepairToken"/>.</param>
    /// <param name="Password">The new password.</param>
    /// <param name="Repeat">The new password, typed again.</param>
    private sealed record Posted(string Email, string Token, string Password, string Repeat)
    {
        /// <summary>Reads the four fields.</summary>
        public static Posted From(IFormCollection form) => new(
            form["email"].ToString(),
            RepairToken(form["token"].ToString()),
            form["password"].ToString(),
            form["repeat"].ToString());

        /// <summary>
        /// The token as issued, from one that went through <c>URLSearchParams</c> or a form encoding (every <c>+</c>
        /// read as a space) and was pasted with whitespace around it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A base64 token never contains a space, so a space inside one is a <c>+</c>. Turning it back repairs the
        /// token and can make no other token valid: the repaired string is still redeemed, and still refused unless
        /// it is the token as issued.
        /// </para>
        /// <para>
        /// <b>Trimming alone would eat a trailing <c>+</c></b>: a token that ends in one arrives ending in a space,
        /// which is indistinguishable from pasted whitespace. Base64 (RFC 4648 §4) comes in quartets of characters,
        /// so the spaces straight after the trimmed token give back as many <c>+</c> as its last quartet is short,
        /// and never more than one quartet's worth. An issued token starts with the data-protection header, never
        /// with a <c>+</c>, so the leading whitespace is always whitespace.
        /// </para>
        /// </remarks>
        /// <param name="posted">The token field as posted.</param>
        /// <returns>The repaired token; empty when nothing but whitespace was posted.</returns>
        private static string RepairToken(string posted)
        {
            var trimmed = posted.Trim();
            var after = posted.AsSpan(posted.Length - posted.TrimStart().Length + trimmed.Length);
            var spacesAfter = after.IndexOfAnyExcept(' ') is var end and >= 0 ? end : after.Length;
            var missing = trimmed.Length is 0 ? 0 : (4 - (trimmed.Length % 4)) % 4;

            return trimmed.Replace(' ', '+') + new string('+', Math.Min(missing, spacesAfter));
        }

        /// <summary>
        /// Where a post that must not reach the store goes: over-long or missing input, a password over the policy's
        /// ceiling, two passwords that differ; or <see langword="null"/> for a post that may be redeemed.
        /// </summary>
        public string? Refusal()
        {
            if (Email.Length is 0 or > MaximumEmailLength || Token.Length is 0 or > MaximumTokenLength)
            {
                return Failed;
            }

            if (Password.Length > MaximumPasswordLength)
            {
                return Back("weak");
            }

            return string.Equals(Password, Repeat, StringComparison.Ordinal) ? null : Back("mismatch");
        }

        /// <summary>Back to the page with <paramref name="problem"/>, carrying the link's fragment.</summary>
        /// <remarks>The same fragment the dashboard's link is built with; a host test pins the two together.</remarks>
        public string Back(string problem)
            => $"{AlvoAdmin.SetPasswordPath}?problem={problem}"
                + $"#email={Uri.EscapeDataString(Email)}&token={Uri.EscapeDataString(Token)}";
    }
}
