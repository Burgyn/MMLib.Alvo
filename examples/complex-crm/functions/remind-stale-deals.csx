// Scheduled (cron) function: nudge owners about deals that have gone quiet.
// Triggered by the "stale-deal-reminder" automation rule (queued execution).
//
// Illustrative skeleton — the exact AlvoScriptContext surface (IAlvoData with
// policy, IEmailSender, logger, limited HTTP client) is finalized with the csx
// runtime in F7.2. Data access still goes through the policy-enforcing port,
// never around it.

var cutoff = DateTime.UtcNow.AddDays(-30);

var staleDeals = await ctx.Data
    .Query("deals")
    .Where("stage in ['lead', 'offer'] && updated_at < @cutoff", new { cutoff })
    .ToListAsync(ct);

foreach (var deal in staleDeals)
{
    await ctx.Email.SendAsync(
        template: "deal-won",           // reuse a template by name; a dedicated one would be declared in `templates`
        to: deal.GetString("owner_id"), // resolved to the owner's address by the sender
        data: new { title = deal.GetString("title"), amount = deal.Get("amount") },
        ct);

    ctx.Log.Information("Reminded owner about stale deal {DealId}", deal.Id);
}
