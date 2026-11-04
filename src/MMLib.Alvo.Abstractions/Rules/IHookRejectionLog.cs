namespace MMLib.Alvo.Rules;

/// <summary>Where a host write hook's rejections are kept (Ruling 8), implemented by a relational driver.</summary>
internal interface IHookRejectionLog
{
    /// <summary>
    /// Records <paramref name="rejection"/>: at once with no transaction unit on the flow, or when the unit on the flow has
    /// ended — whatever it ended with.
    /// </summary>
    /// <param name="rejection">The rejection.</param>
    /// <param name="cancellationToken">A token to cancel an immediate insert.</param>
    /// <returns>A task that completes when the row is recorded or handed to the unit.</returns>
    Task RecordAsync(HookRejection rejection, CancellationToken cancellationToken);
}
