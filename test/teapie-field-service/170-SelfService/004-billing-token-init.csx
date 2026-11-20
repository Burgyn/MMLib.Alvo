// Every token expires, and says when as an RFC 3339 instant with an offset — computed per run, never a literal that
// would one day lie in the past (`auth.accessTokens.maxLifetimeDays` defaults to 90).
tp.SetVariable("BillingExpiresAt", DateTimeOffset.UtcNow.AddDays(30).ToString("O"));
tp.SetVariable("TheoTokenExpiresAt", DateTimeOffset.UtcNow.AddDays(7).ToString("O"));
