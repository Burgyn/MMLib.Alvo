// An invitation that expires within the run. Fifteen seconds is generous on purpose: the instant must still be in the
// future when Alvo judges the create, however slow the runner, and the preview below polls until it has passed —
// never a fixed sleep (plan D8).
tp.SetVariable("ExpiresSoon", DateTimeOffset.UtcNow.AddSeconds(15).ToString("O"));
