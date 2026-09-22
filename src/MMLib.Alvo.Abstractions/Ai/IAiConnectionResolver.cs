namespace MMLib.Alvo.Ai;

/// <summary>
/// What this instance would dial right now, and which layer decided it.
/// </summary>
/// <param name="Connection">The connection, or <see langword="null"/> when this deployment has none.</param>
/// <param name="Source">
/// Which layer answered, or <see cref="AiConnectionSource.None"/> when neither did.
/// </param>
public readonly record struct AiConnectionResolution(AlvoAiConnection? Connection, AiConnectionSource Source);

/// <summary>
/// Resolves the AI connection this instance would use right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved per call, never cached.</b> A connection lives in configuration or in the secret store, and
/// both change without a restart — an operator who saved a key and watched the screen keep reporting "not
/// configured" would reasonably conclude the save was lost.
/// </para>
/// <para>
/// <b>One member, because every caller asks both questions.</b> <c>GET {m}/info</c> reports the connection
/// <em>and</em> where it came from, and two members would read the stored record twice — a second
/// decryption on a per-request path, and a window in which the two answers disagree.
/// </para>
/// <para>
/// <b>A port in Abstractions because the agent package reads it and the core writes it.</b>
/// <c>MMLib.Alvo.Ai</c> must not reference the core (§0 principle 2), and the core must not reference the
/// agent — so the shape they agree on lives here, as every other seam between them does.
/// </para>
/// </remarks>
public interface IAiConnectionResolver
{
    /// <summary>
    /// The connection to use and where it came from, or no connection at all.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The resolution.</returns>
    /// <remarks>
    /// An absent connection is an answer rather than a refusal: having no AI configured is the default state
    /// of every deployment, and a screen that had to catch an exception to draw "not configured" is a screen
    /// that will eventually draw a stack trace.
    /// </remarks>
    ValueTask<AiConnectionResolution> ResolveAsync(CancellationToken ct = default);
}
