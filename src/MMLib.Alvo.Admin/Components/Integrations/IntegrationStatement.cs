namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>
/// The endpoint sheet's statement (spec §6.3): the facts an operator must read before a destination receives a record.
/// </summary>
/// <remarks>
/// <para>
/// <b>The build's own sentence comes first, verbatim</b> — <c>capabilities.warned</c>'s <c>webhooks</c> consequence, which
/// says deliveries are unsigned and unprojected. These four are the facts that sentence does not carry, so this dashboard
/// says them (a recorded deviation from ruling B6, spec §3): the build publishes nothing for the egress guard, the explicit
/// <c>hidden</c> disclosure or the missing dead-letter queue. <c>HooksEditorAgreementTests</c> pins each claim to the core it
/// describes, and a follow-up moves them into <c>capabilities</c>.
/// </para>
/// <para>Sources: <c>docs/architecture/events.md</c> ("the unmasked record", "the attempt ceiling is the DLQ stand-in");
/// <c>AlvoEventOptions.WebhookAllowedNetworks</c>; <c>WebhookEgressGuard</c>.</para>
/// </remarks>
internal static class IntegrationStatement
{
    /// <summary>The host setting that admits a non-public network — the only one, and not the dashboard's.</summary>
    public const string AllowedNetworksSetting = "Alvo:Events:WebhookAllowedNetworks";

    /// <summary>The statement's title.</summary>
    public const string Title = "Deliveries to this endpoint are not signed, and carry the whole row";

    /// <summary>The disclosure #152 closes.</summary>
    public const string HiddenFields = "Each delivery carries the record's complete image — fields declared 'hidden' included.";

    /// <summary>Where the egress guard acts, and who can lift it.</summary>
    public const string PrivateDestinations =
        "A destination on a private, loopback, link-local or otherwise non-public network is accepted here and on apply, and "
        + "refused on every delivery: the check runs when the connection is made, on the address the name resolves to. A "
        + "loopback address passes only when the URL names it (localhost, 127.0.0.1, [::1]). Only the host's configuration, "
        + AllowedNetworksSetting + ", admits another network — this dashboard cannot.";

    /// <summary>What happens to a delivery that keeps failing.</summary>
    public const string NoRedelivery =
        "A delivery that keeps failing is retried up to the attempt ceiling and then abandoned; there is no dead-letter queue "
        + "and no redelivery screen yet.";

    /// <summary>The secret name field's hint.</summary>
    public const string SecretHint = "The name the signing secret will be stored under, never the secret itself. This build does not read it.";
}
