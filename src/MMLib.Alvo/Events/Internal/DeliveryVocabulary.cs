namespace MMLib.Alvo.Events.Internal;

/// <summary>What runs a delivery.</summary>
internal static class DeliveryKind
{
    internal const string Handler = "handler";
    internal const string Schedule = "schedule";
    internal const string Migration = "migration";
    internal const string Action = "action";
    internal const string Function = "function";
}

/// <summary>The fixed reasons a delivery records; any other failure records its exception's type name, never its message.</summary>
internal static class DeliveryErrors
{
    internal const string WhenFailed = "when-failed";
    internal const string ChainDepthExceeded = "chain-depth-exceeded";
    internal const string Timeout = "timeout";
    internal const string EventMissing = "event-missing";
    internal const string LeaseExpired = "lease-expired";
    internal const string Retired = "retired";
    internal const string Unbound = DeliveryRequest.UnboundReason;
    internal const string EventUnreadable = "event-unreadable";
    internal const string RecordUnreadable = "record-unreadable";
    internal const string RecordNotFound = "record-not-found";
    internal const string WriteRefused = "write-refused";
    internal const string FunctionInputInvalid = "function-input-invalid";
    internal const string ActionInputInvalid = "action-input-invalid";
}
