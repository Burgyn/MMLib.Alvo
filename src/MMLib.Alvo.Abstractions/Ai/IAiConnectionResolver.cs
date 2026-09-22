namespace MMLib.Alvo.Ai;

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
/// <b>A port in Abstractions because the agent package reads it and the core writes it.</b>
/// <c>MMLib.Alvo.Ai</c> must not reference the core (§0 principle 2), and the core must not reference the
/// agent — so the shape they agree on lives here, as every other seam between them does.
/// </para>
/// </remarks>
public interface IAiConnectionResolver
{
    /// <summary>
    /// The connection to use, or <see langword="null"/> when this deployment has none.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The connection, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <see langword="null"/> rather than a refusal: having no AI configured is the default state of every
    /// deployment, and a screen that has to catch an exception to draw "not configured" is a screen that
    /// will eventually draw a stack trace.
    /// </remarks>
    ValueTask<AlvoAiConnection?> ResolveAsync(CancellationToken ct = default);

    /// <summary>Where the connection <see cref="ResolveAsync"/> would return came from.</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The source, or <see cref="AiConnectionSource.None"/> when there is no connection.</returns>
    ValueTask<AiConnectionSource> DescribeSourceAsync(CancellationToken ct = default);
}
