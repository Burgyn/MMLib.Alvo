// Run-unique values, computed rather than written down: `reference` is unique per tenant and Acme is new every run,
// but a fixed literal would still read as "this row was seeded" to the next person editing the case. Six digits from
// the run's ticks fit the descriptor's `work-order-ref` format (WO- and four to eight digits).
var serial = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond % 999_999;
tp.SetVariable("TheoJobReference", $"WO-{serial:D6}");
tp.SetVariable("OtherJobReference", $"WO-{serial + 1:D6}");

// RFC 3339 instants and the day the job is signed off — never literals, so the case does not age.
tp.SetVariable("TomorrowMorning", DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
tp.SetVariable("Today", DateTime.UtcNow.ToString("yyyy-MM-dd"));
