// The SLA deadline is DATA (spec ruling R3): the descriptor's `sla_breach` timer fires at the job's own `respond_by`,
// so a deadline a few seconds ahead makes the timer testable in seconds — no clock control, no shortened constant,
// nothing test-only in the host. Eight seconds leaves the creates, and the control's dispatch, well inside it.
tp.SetVariable("RespondBy", DateTimeOffset.UtcNow.AddSeconds(8).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
