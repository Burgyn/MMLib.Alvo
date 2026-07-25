namespace MMLib.Alvo.Auth;

/// <summary>
/// The ambient, per-request accessor for the resolved caller (spec §4). This is
/// availability, not enforcement: <c>IAlvoData</c> still takes the <see cref="AlvoContext"/>
/// as an explicit parameter, because the outbox dispatcher, after-hooks and automation
/// actions run with no request scope and would find nothing here.
/// </summary>
public interface IAlvoContextAccessor
{
    /// <summary>Gets or sets the principal resolved for the current request, if any.</summary>
    AlvoPrincipal? Principal { get; set; }
}
