namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// A descriptor action that cannot succeed on any retry — its input does not bind, its record is not there, its write is
/// refused: the delivery ends dead with <see cref="Reason"/>, one of <see cref="DeliveryErrors"/>, after one attempt.
/// </summary>
/// <param name="reason">The fixed dead reason the delivery records; never a value from the event.</param>
internal sealed class ActionRefusedException(string reason) : Exception(reason)
{
    /// <summary>The fixed dead reason the delivery records.</summary>
    internal string Reason => Message;
}
