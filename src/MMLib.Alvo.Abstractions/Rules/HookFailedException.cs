namespace MMLib.Alvo.Rules;

/// <summary>
/// A host write hook failed, so the write failed — answered 500 <c>hook-failed</c> with the extension id and one fixed reason
/// (Ruling 3). An <see cref="InvalidOperationException"/>, the data port's family for an invariant the implementation relies on.
/// </summary>
/// <param name="extension">The hook's id.</param>
/// <param name="reason">One of the reason constants.</param>
internal sealed class HookFailedException(string extension, string reason)
    : InvalidOperationException($"The write hook '{extension}' failed ({reason}), so the write was rolled back.")
{
    internal const string Exception = "exception";
    internal const string Timeout = "timeout";
    internal const string Budget = "budget";
    internal const string InvalidSet = "invalid-set";
    internal const string SelfWrite = "self-write";
    internal const string Unbound = "unbound";
    internal const string RowVanished = "row-vanished";

    internal string Extension { get; } = extension;

    internal string Reason { get; } = reason;
}
