namespace MMLib.Alvo.Rules;

/// <summary>Which side of the data path a failed host extension ran on.</summary>
internal enum HookFailureStage
{
    Write,
    Read,
}

/// <summary>
/// A host write hook or read extension failed, so the write or the read failed — answered 500 <c>hook-failed</c> with the
/// extension ids and one fixed reason (Ruling 3). An <see cref="InvalidOperationException"/>, the data port's family for an
/// invariant the implementation relies on. The host's own exception is logged where it failed, never carried here.
/// </summary>
#pragma warning disable RCS1194 // Deliberately no standard constructors: a failure always names its extensions and reason.
internal sealed class HookFailedException : InvalidOperationException
{
    internal const string Exception = "exception";
    internal const string Timeout = "timeout";
    internal const string Budget = "budget";
    internal const string InvalidSet = "invalid-set";
    internal const string SelfWrite = "self-write";
    internal const string Unbound = "unbound";
    internal const string RowVanished = "row-vanished";
    internal const string Translation = "translation";
    internal const string InvalidFilter = "invalid-filter";
    internal const string InvalidHide = "invalid-hide";

    /// <summary>A write hook's failure.</summary>
    /// <param name="extension">The hook's id.</param>
    /// <param name="reason">One of the reason constants.</param>
    internal HookFailedException(string extension, string reason)
        : this([extension], reason, HookFailureStage.Write, $"The write hook '{extension}' failed ({reason}), so the write was rolled back.")
    {
    }

    private HookFailedException(IReadOnlyList<string> extensions, string reason, HookFailureStage stage, string message)
        : base(message)
    {
        Extensions = extensions;
        Reason = reason;
        Stage = stage;
    }

    /// <summary>Gets the first extension's id — a write hook's only one.</summary>
    internal string Extension => Extensions[0];

    /// <summary>Gets every extension the failure names.</summary>
    internal IReadOnlyList<string> Extensions { get; }

    internal string Reason { get; }

    internal HookFailureStage Stage { get; }

    /// <summary>A read extension's failure: the read answered nothing.</summary>
    /// <param name="extensions">The ids of the extensions the failed step ran.</param>
    /// <param name="reason">One of the reason constants.</param>
    /// <returns>The failure.</returns>
    internal static HookFailedException OfRead(IReadOnlyList<string> extensions, string reason) =>
        new(extensions, reason, HookFailureStage.Read,
            $"The read extension '{string.Join("', '", extensions)}' failed ({reason}), so the read answered nothing.");
}
#pragma warning restore RCS1194
