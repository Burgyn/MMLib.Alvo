namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// One webhook endpoint as a delivery actually needs it: the name the descriptor declared it under, and the
/// absolute URL it was validated into when the descriptor was applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name travels and the URL does not, and that is a disclosure decision rather than tidiness.</b> This
/// build never reads <see cref="Descriptor.WebhookEndpoint.SecretRef"/> and sends no HMAC signature, so the
/// only authentication an author has is a secret <em>inside</em> the URL — which is exactly how a Slack,
/// Teams, Zapier or Make endpoint works: <c>https://hooks.slack.com/services/T…/B…/XXXX</c> <em>is</em> the
/// bearer token. A failure message or a log line naming the URL therefore takes that credential out of the
/// descriptor and into whatever ships logs, whose read set is far wider than "whoever declared the endpoint"
/// — the premise decision D7 rests on. So every message this subsystem writes about an endpoint names
/// <see cref="Name"/>, which is the author's own vocabulary and the key they act on.
/// </para>
/// <para>
/// <b>What is still disclosed, named so nobody assumes more than is true.</b> A DNS or connection failure
/// raises an <see cref="HttpRequestException"/> whose framework-supplied message carries <c>host:port</c>.
/// That is accepted: the host is not the secret, the path and the query are.
/// </para>
/// <para>
/// <b>The URL is a <see cref="Uri"/> because it was validated at apply.</b> Parsing it at delivery is how a
/// relative or malformed URL becomes a <see cref="UriFormatException"/> thrown per attempt, retried until the
/// ceiling and read by an author as an endpoint outage rather than as the typo it is — the failure
/// compile-time resolution exists to prevent.
/// </para>
/// </remarks>
/// <param name="Name">The key the endpoint is declared under in <c>webhooks.endpoints</c>.</param>
/// <param name="Url">The endpoint's absolute, validated URL.</param>
internal sealed record WebhookTarget(string Name, Uri Url);
