// A token always says when it expires; one day is plenty for a case that uses it for a few requests.
tp.SetVariable("TokenExpiresAt", DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
